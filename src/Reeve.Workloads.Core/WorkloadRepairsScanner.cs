using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core;

/// <summary>Escalation scan over every workload instance, raising/clearing <see cref="IWorkloadDiagnosticsSink"/> diagnostics.
/// De-spammed: the registry is called only when severity+message+fixHint is materially new, so a stable condition never re-pushes each pass.</summary>
public sealed class WorkloadRepairsScanner
{
    private const string CodePrefix = "workload.";
    private const string StuckSuffix = ".stuck";
    private const string CrashLoopSuffix = ".crashloop";
    private const string ParkedSuffix = ".parked";
    private const string RollbackFailedSuffix = ".rollback_failed";
    private const string VersionSkewSuffix = ".version_skew";

    private const string ParkedSourceRollback = "auto-rollback";

    private static readonly HashSet<WorkloadInstanceState> StuckStates = new()
    {
        WorkloadInstanceState.Degraded,
        WorkloadInstanceState.Failed,
        WorkloadInstanceState.Unknown,
        WorkloadInstanceState.Terminating,
    };

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IWorkloadDiagnosticsSinkFactory registryFactory;
    private readonly IWorkloadProviderRegistry providerRegistry;
    private readonly IWorkloadClock clock;
    private readonly WorkloadRepairsOptions options;
    private readonly ILogger<WorkloadRepairsScanner> logger;

    private readonly ConcurrentDictionary<Guid, CrashLoopBaseline> crashLoopBaselines = new();

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> badSetEnteredAtUtc = new();

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> versionSkewEnteredAtUtc = new();

    private readonly ConcurrentDictionary<(Guid InstanceId, string Code), RaiseSignature> lastRaised = new();

    private readonly ConcurrentDictionary<Guid, Guid> groupInstanceOf = new();

    private readonly record struct RaiseSignature(WorkloadDiagnosticSeverity Severity, string Message, string? FixHint);

    private readonly record struct CrashLoopBaseline(
        int Count, DateTimeOffset AnchoredAtUtc, int LastObservedCount, DateTimeOffset LastIncreaseAtUtc);

    public WorkloadRepairsScanner(
        IServiceScopeFactory scopeFactory,
        IWorkloadDiagnosticsSinkFactory registryFactory,
        IWorkloadProviderRegistry providerRegistry,
        IWorkloadClock clock,
        IOptions<WorkloadRepairsOptions> options,
        ILogger<WorkloadRepairsScanner> logger)
    {
        this.scopeFactory = scopeFactory;
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        this.registryFactory = registryFactory;
        this.clock = clock;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <summary>The stable per-workload stuck-escalation code (unique per workload so the registry dedup is disjoint).</summary>
    public static string StuckCode(string workloadId) => CodePrefix + workloadId + StuckSuffix;

    /// <summary>The stable per-workload crash-loop code (distinct from the stuck code, disjoint per workload).</summary>
    public static string CrashLoopCode(string workloadId) => CodePrefix + workloadId + CrashLoopSuffix;

    /// <summary>The stable per-workload parked-release code (Info now, Warning after 7 days).</summary>
    public static string ParkedCode(string workloadId) => CodePrefix + workloadId + ParkedSuffix;

    /// <summary>The stable per-workload rollback-failed code (terminal Error: the release rolled back AND the rollback
    /// itself failed — "stuck forever with nothing running").</summary>
    public static string RollbackFailedCode(string workloadId) => CodePrefix + workloadId + RollbackFailedSuffix;

    /// <summary>The stable per-workload manual-provider VersionSkew code (10-min warn-not-block skew).</summary>
    public static string VersionSkewCode(string workloadId) => CodePrefix + workloadId + VersionSkewSuffix;

    internal static async Task ClearOpenIssuesAsync(
        IWorkloadDiagnosticsSinkFactory sinks, WorkloadInstance row, ILogger logger, CancellationToken cancellationToken)
    {
        var sink = sinks.Create(row.GroupInstanceId);
        foreach (var code in new[]
        {
            StuckCode(row.WorkloadId), CrashLoopCode(row.WorkloadId), VersionSkewCode(row.WorkloadId),
            ParkedCode(row.WorkloadId), RollbackFailedCode(row.WorkloadId),
        })
        {
            try
            {
                await sink.ClearAsync(code, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Clearing {Code} of deleted workload instance {InstanceId} failed.", code, row.Id);
            }
        }
    }

    /// <summary>One escalation pass over every workload instance in a fresh DI scope.
    /// A single instance's evaluation fault never aborts the pass.</summary>
    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        var repository = scope.ServiceProvider.GetRequiredService<IWorkloadInstanceRepository>();
        var intentRepository = scope.ServiceProvider.GetRequiredService<IWorkloadUpdateIntentRepository>();

        var instances = await repository.ListAllAsync(cancellationToken).ConfigureAwait(false);

        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                groupInstanceOf[instance.Id] = instance.GroupInstanceId;
                var registry = registryFactory.Create(instance.GroupInstanceId);
                await EvaluateStuckStateAsync(registry, instance, cancellationToken).ConfigureAwait(false);
                await EvaluateCrashLoopAsync(registry, instance, cancellationToken).ConfigureAwait(false);
                await EvaluateManualVersionSkewAsync(registry, instance, cancellationToken).ConfigureAwait(false);
                await EvaluateParkedAsync(registry, instance, intentRepository, cancellationToken)
                    .ConfigureAwait(false);
                await EvaluateRollbackFailedAsync(registry, instance, intentRepository, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workload Repairs evaluation of instance {InstanceId} threw; continuing.", instance.Id);
            }
        }

        await PruneTrackingAsync(instances, cancellationToken).ConfigureAwait(false);
    }

    private async Task PruneTrackingAsync(IReadOnlyList<WorkloadInstance> present, CancellationToken cancellationToken)
    {
        var liveInstanceIds = present.Select(i => i.Id).ToHashSet();
        PruneById(crashLoopBaselines, liveInstanceIds);
        PruneById(badSetEnteredAtUtc, liveInstanceIds);
        PruneById(versionSkewEnteredAtUtc, liveInstanceIds);

        foreach (var key in lastRaised.Keys)
        {
            if (liveInstanceIds.Contains(key.InstanceId))
            {
                continue;
            }

            if (groupInstanceOf.TryGetValue(key.InstanceId, out var groupInstanceId))
            {
                try
                {
                    await registryFactory.Create(groupInstanceId).ClearAsync(key.Code, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Clearing {Code} of deleted workload instance {InstanceId} failed; retrying next pass.",
                        key.Code, key.InstanceId);
                    continue;
                }
            }

            lastRaised.TryRemove(key, out _);
        }

        foreach (var id in groupInstanceOf.Keys)
        {
            if (!liveInstanceIds.Contains(id) && !lastRaised.Keys.Any(k => k.InstanceId == id))
            {
                groupInstanceOf.TryRemove(id, out _);
            }
        }

        registryFactory.Prune(present.Select(i => i.GroupInstanceId).ToHashSet());
    }

    private static void PruneById<TValue>(ConcurrentDictionary<Guid, TValue> map, HashSet<Guid> liveInstanceIds)
    {
        if (map.IsEmpty)
        {
            return;
        }

        foreach (var id in map.Keys)
        {
            if (!liveInstanceIds.Contains(id))
            {
                map.TryRemove(id, out _);
            }
        }
    }

    private async Task RaiseIfChangedAsync(
        IWorkloadDiagnosticsSink registry,
        Guid instanceId,
        string code,
        WorkloadDiagnosticSeverity severity,
        string message,
        string fixHint,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken)
    {
        var signature = new RaiseSignature(severity, message, fixHint);
        if (lastRaised.TryGetValue((instanceId, code), out var previous) && previous == signature)
        {
            return;
        }

        await registry.RaiseAsync(
            new WorkloadDiagnostic
            {
                Code = code,
                Severity = severity,
                Message = message,
                FixHint = fixHint,
                Metadata = metadata,
            },
            cancellationToken).ConfigureAwait(false);
        lastRaised[(instanceId, code)] = signature;
    }

    private async Task ClearAndForgetAsync(
        IWorkloadDiagnosticsSink registry, Guid instanceId, string code, CancellationToken cancellationToken)
    {
        lastRaised.TryRemove((instanceId, code), out _);
        await registry.ClearAsync(code, cancellationToken).ConfigureAwait(false);
    }

    private async Task EvaluateStuckStateAsync(
        IWorkloadDiagnosticsSink registry, WorkloadInstance instance, CancellationToken cancellationToken)
    {
        var code = StuckCode(instance.WorkloadId);

        if (!StuckStates.Contains(instance.State))
        {
            badSetEnteredAtUtc.TryRemove(instance.Id, out _);
            await ClearAndForgetAsync(registry, instance.Id, code, cancellationToken).ConfigureAwait(false);
            return;
        }

        var enteredAt = badSetEnteredAtUtc.GetOrAdd(instance.Id, _ => instance.LastTransitionAtUtc);

        var stuckFor = clock.UtcNow - enteredAt;
        var stalledDeploy = instance.State == WorkloadInstanceState.Degraded
            && instance.StateReason == WorkloadStateReason.Provisioning;
        if (!stalledDeploy && stuckFor <= options.StuckWarningAfter)
        {
            return;
        }

        var minutes = (int)Math.Round(stuckFor.TotalMinutes);

        var thresholdMinutes = (int)Math.Round(options.StuckWarningAfter.TotalMinutes);
        var message = stalledDeploy
            ? $"Workload '{instance.WorkloadId}' is {instance.State}: {instance.StateMessage ?? "its deploy did not become ready."}"
            : string.IsNullOrWhiteSpace(instance.StateMessage)
                ? $"Workload '{instance.WorkloadId}' has been {instance.State} for over {thresholdMinutes} min (reason: {instance.StateReason})."
                : $"Workload '{instance.WorkloadId}' has been {instance.State} for over {thresholdMinutes} min: {instance.StateMessage}";

        await RaiseIfChangedAsync(
            registry,
            instance.Id,
            code,
            instance.State == WorkloadInstanceState.Failed ? WorkloadDiagnosticSeverity.Error : WorkloadDiagnosticSeverity.Warning,
            message,
            fixHint: instance.State == WorkloadInstanceState.Terminating
                ? "Reeve retries the removal on every pass. Make the provider reachable again (for example the Docker socket or the Kubernetes namespace setting) so it can remove the workload."
                : "Check the workload's provider status and logs; a redeploy from the plugin's Deployment tab may clear it.",
            metadata: new Dictionary<string, object?>
            {
                ["workloadId"] = instance.WorkloadId,
                ["instanceId"] = instance.Id.ToString(),
                ["state"] = instance.State.ToString(),
                ["stuckMinutes"] = minutes,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EvaluateCrashLoopAsync(
        IWorkloadDiagnosticsSink registry, WorkloadInstance instance, CancellationToken cancellationToken)
    {
        var code = CrashLoopCode(instance.WorkloadId);
        var current = instance.RestartCount;
        var now = clock.UtcNow;

        if (!crashLoopBaselines.TryGetValue(instance.Id, out var baseline))
        {
            crashLoopBaselines[instance.Id] = new CrashLoopBaseline(current, now, current, now);
            return;
        }

        if (current < baseline.Count)
        {
            crashLoopBaselines[instance.Id] = new CrashLoopBaseline(current, now, current, now);
            await ClearAndForgetAsync(registry, instance.Id, code, cancellationToken).ConfigureAwait(false);
            return;
        }

        var climbed = current > baseline.LastObservedCount;
        var lastIncreaseAt = climbed ? now : baseline.LastIncreaseAtUtc;
        var settled = now - lastIncreaseAt >= options.CrashLoopWindow;
        var rise = current - baseline.Count;

        if (rise >= options.CrashLoopRestartThreshold && !settled)
        {
            crashLoopBaselines[instance.Id] =
                baseline with { LastObservedCount = current, LastIncreaseAtUtc = lastIncreaseAt };

            var message = $"Workload '{instance.WorkloadId}' is crash-looping (restart count rising since the loop began).";
            await RaiseIfChangedAsync(
                registry,
                instance.Id,
                code,
                WorkloadDiagnosticSeverity.Warning,
                message,
                fixHint: "The workload keeps restarting — inspect its logs/config; pause it from the Deployment tab if it is thrashing.",
                metadata: new Dictionary<string, object?>
                {
                    ["workloadId"] = instance.WorkloadId,
                    ["instanceId"] = instance.Id.ToString(),
                    ["restartCount"] = current,
                    ["restartRise"] = rise,
                    ["restartBaseline"] = baseline.Count,
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        crashLoopBaselines[instance.Id] = settled || now - baseline.AnchoredAtUtc >= options.CrashLoopWindow
            ? new CrashLoopBaseline(current, now, current, now)
            : baseline with { LastObservedCount = current, LastIncreaseAtUtc = lastIncreaseAt };

        await ClearAndForgetAsync(registry, instance.Id, code, cancellationToken).ConfigureAwait(false);
    }

    private async Task EvaluateManualVersionSkewAsync(
        IWorkloadDiagnosticsSink registry, WorkloadInstance instance, CancellationToken cancellationToken)
    {
        var code = VersionSkewCode(instance.WorkloadId);

        var isManualSkew = instance.Provider is { } bound
            && !providerRegistry.Describe(bound).Materializes
            && instance.State == WorkloadInstanceState.VersionSkew;
        if (!isManualSkew)
        {
            versionSkewEnteredAtUtc.TryRemove(instance.Id, out _);
            await ClearAndForgetAsync(registry, instance.Id, code, cancellationToken).ConfigureAwait(false);
            return;
        }

        var enteredAt = versionSkewEnteredAtUtc.GetOrAdd(instance.Id, _ => instance.LastTransitionAtUtc);

        var skewFor = clock.UtcNow - enteredAt;
        if (skewFor <= options.ManualVersionSkewWarningAfter)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(instance.StateMessage)
            ? $"Workload '{instance.WorkloadId}' is in a manual-provider version skew — the installed module and the running sidecar protocol versions do not overlap. Re-render the artifacts and redeploy the sidecar."
            : $"Workload '{instance.WorkloadId}' is in a manual-provider version skew: {instance.StateMessage} Re-render the artifacts and redeploy the sidecar.";

        var minutes = (int)Math.Round(skewFor.TotalMinutes);
        await RaiseIfChangedAsync(
            registry,
            instance.Id,
            code,
            WorkloadDiagnosticSeverity.Warning,
            message,
            fixHint: "Manual provider: re-render the deployment artifacts from the plugin's Deployment tab and redeploy the sidecar so its protocol version catches up to the installed module.",
            metadata: new Dictionary<string, object?>
            {
                ["workloadId"] = instance.WorkloadId,
                ["instanceId"] = instance.Id.ToString(),
                ["provider"] = "manual",
                ["skewMinutes"] = minutes,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EvaluateParkedAsync(
        IWorkloadDiagnosticsSink registry,
        WorkloadInstance instance,
        IWorkloadUpdateIntentRepository intentRepository,
        CancellationToken cancellationToken)
    {
        var code = ParkedCode(instance.WorkloadId);

        ParkedSignal? parked;
        try
        {
            parked = await ResolveParkedAsync(instance, intentRepository, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Workload Repairs parked-source lookup for instance {InstanceId} failed; leaving the parked issue unchanged.",
                instance.Id);
            return;
        }

        if (parked is null)
        {
            await ClearAndForgetAsync(registry, instance.Id, code, cancellationToken).ConfigureAwait(false);
            return;
        }

        var age = clock.UtcNow - parked.ParkedAtUtc;
        var severity = age > options.ParkedWarningAfter ? WorkloadDiagnosticSeverity.Warning : WorkloadDiagnosticSeverity.Info;

        var message =
            $"Release rolled back to the last-known-good and is parked at '{parked.ParkedVersion}'{FormatReason(parked.Reason)}.";
        var fixHint = "Review the rolled-back release on the plugin's Deployment tab; re-run the update once the cause is fixed.";

        await RaiseIfChangedAsync(
            registry,
            instance.Id,
            code,
            severity,
            message,
            fixHint,
            metadata: new Dictionary<string, object?>
            {
                ["workloadId"] = instance.WorkloadId,
                ["instanceId"] = instance.Id.ToString(),
                ["parkedVersion"] = parked.ParkedVersion,
                ["parkedAtUtc"] = parked.ParkedAtUtc.ToString("O"),
                ["source"] = parked.Source,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ParkedSignal?> ResolveParkedAsync(
        WorkloadInstance instance,
        IWorkloadUpdateIntentRepository intentRepository,
        CancellationToken cancellationToken)
    {
        var latest = await intentRepository
            .GetLatestForInstanceAsync(instance.GroupInstanceId, cancellationToken).ConfigureAwait(false);
        if (latest is not { Outcome: WorkloadUpdateOutcome.RolledBack })
        {
            return null;
        }

        var parkedAt = latest.CompletedAtUtc ?? latest.PhaseEnteredAtUtc;
        return new ParkedSignal(latest.ToVersion, parkedAt, ParkedSourceRollback, latest.Error);
    }

    private async Task EvaluateRollbackFailedAsync(
        IWorkloadDiagnosticsSink registry,
        WorkloadInstance instance,
        IWorkloadUpdateIntentRepository intentRepository,
        CancellationToken cancellationToken)
    {
        var code = RollbackFailedCode(instance.WorkloadId);

        WorkloadUpdateIntent? latest;
        try
        {
            latest = await intentRepository
                .GetLatestForInstanceAsync(instance.GroupInstanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Workload Repairs rollback-failed lookup for instance {InstanceId} failed; leaving the issue unchanged.",
                instance.Id);
            return;
        }

        if (latest is not { Outcome: WorkloadUpdateOutcome.RollbackFailed })
        {
            await ClearAndForgetAsync(registry, instance.Id, code, cancellationToken).ConfigureAwait(false);
            return;
        }

        var fromVersion = latest.FromVersion ?? "unknown";
        var what = latest.ReadStageJournal() switch
        {
            { Verdict: WorkloadDocumentVerdict.Value, Value.Direction: Reeve.Pipeline.PipelineDirection.Forward } =>
                $"The update from '{fromVersion}' to '{latest.ToVersion}' did not finish and was NOT rolled back — "
                + "whatever it had done stays, and the new version may be running only in part. ",
            { Verdict: WorkloadDocumentVerdict.Unreadable } =>
                $"The update from '{fromVersion}' to '{latest.ToVersion}' did not finish, and its record of what ran "
                + "is unreadable, so whether anything was rolled back is not known. ",
            _ => $"Rollback of the update from '{fromVersion}' to '{latest.ToVersion}' FAILED — the new version may "
                + "still be running, or the previous one may be back but not healthy. ",
        };
        var message =
            what
            + "Nothing retries this on its own; the deployment has to be reconciled by hand."
            + (string.IsNullOrWhiteSpace(latest.Error) ? string.Empty : $" Why: {latest.Error}");

        await RaiseIfChangedAsync(
            registry,
            instance.Id,
            code,
            WorkloadDiagnosticSeverity.Error,
            message,
            fixHint: "Open this update on the plugin's Deployment tab for the captured logs and the exact failure, then "
                + "reconcile it yourself: Redeploy re-applies whatever Reeve currently holds as desired, which after a "
                + "failed rollback is not guaranteed to be either version — check which one comes back.",
            metadata: new Dictionary<string, object?>
            {
                ["workloadId"] = instance.WorkloadId,
                ["instanceId"] = instance.Id.ToString(),
                ["fromVersion"] = fromVersion,
                ["toVersion"] = latest.ToVersion,
                ["fromDigests"] = latest.ReadFromDigests(),
                ["toDigests"] = latest.ReadToDigests(),
                ["evidence"] = latest.ReadEvidence(),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record ParkedSignal(string ParkedVersion, DateTimeOffset ParkedAtUtc, string Source, string? Reason);

    private static string FormatReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})";
}
