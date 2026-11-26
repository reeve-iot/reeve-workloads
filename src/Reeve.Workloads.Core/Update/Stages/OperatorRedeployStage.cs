using Microsoft.Extensions.Logging;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Health;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Core.Update.Stages;

internal sealed class OperatorRedeployStage(
    IWorkloadProviderRegistry providerRegistry,
    IObservedWorkloadVersionReader observedVersionReader,
    WorkloadHealthContributorDispatch healthContributors,
    IWorkloadDiagnosticsSinkFactory diagnostics,
    IWorkloadClock clock,
    ILogger<OperatorRedeployStage> logger)
    : IStage<WorkloadUpdateStageContext>
{
    public const string VersionSkewSuffix = "version_skew";

    private readonly IWorkloadProviderRegistry _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
    private readonly IObservedWorkloadVersionReader _observedVersionReader = observedVersionReader ?? throw new ArgumentNullException(nameof(observedVersionReader));
    private readonly WorkloadHealthContributorDispatch _healthContributors = healthContributors ?? throw new ArgumentNullException(nameof(healthContributors));
    private readonly IWorkloadDiagnosticsSinkFactory _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    private readonly IWorkloadClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILogger<OperatorRedeployStage> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public string Id => WorkloadUpdateStages.OperatorRedeploy;

    public StagePlacement Placement => StagePlacement.After(WorkloadUpdateStages.Sidecar);

    public StageReversibility Reversibility => StageReversibility.Reversible;

    public StageBudget Budget => StageBudget.None;

    public async ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var internals = context.RequireInternals();
        var manual = internals.Plan.Workloads.Where(IsManual).ToList();
        if (manual.Count == 0)
        {
            return StageOutcome.Done;
        }

        var added = AddedByThisUpdate(internals);
        var awaited = manual.Where(item => !added.Contains(item.WorkloadId)).ToList();
        var sink = _diagnostics.Create(context.GroupInstanceId);
        if (await AllSidecarsReportTheTargetAsync(internals, awaited, cancellationToken).ConfigureAwait(false))
        {
            foreach (var workload in context.Workloads)
            {
                await sink.ClearAsync(SkewCode(workload.WorkloadId), cancellationToken).ConfigureAwait(false);
            }

            return StageOutcome.Done;
        }

        var now = _clock.UtcNow;
        var addedManual = manual.Select(item => item.WorkloadId).Where(added.Contains).ToList();
        var addedNote = addedManual.Count == 0
            ? string.Empty
            : $" The workloads this update adds ({string.Join(", ", addedManual.Order(StringComparer.Ordinal))}) are not "
                + "waited on: they get their rows when the update commits, and are deployed from the Deployment tab "
                + "after that, as on a fresh install.";
        foreach (var workload in awaited)
        {
            WorkloadUpdateSteps.Ensure(internals.Intent, workload.WorkloadId, now).Advance(
                WorkloadUpdatePhase.ApplyingSidecar,
                WorkloadUpdateStepState.Applying,
                "awaiting operator sidecar redeploy (manual)",
                now);

            await sink.RaiseAsync(
                new WorkloadDiagnostic
                {
                    Code = SkewCode(workload.WorkloadId),
                    Severity = WorkloadDiagnosticSeverity.Warning,
                    Message = $"Manual update parked for '{workload.WorkloadId}' — redeploy the sidecar to "
                        + $"{context.ToVersion}. The update waits until the running sidecar reports the new version "
                        + "and the rendering this update produced (avoiding a new-module ↔ old-sidecar skew).",
                    FixHint = "Re-render the deployment artifacts from the plugin's Deployment tab and redeploy the "
                        + "sidecar to the new version; the update continues automatically once the running sidecar "
                        + "reports it and the current rendering." + addedNote,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["pluginTypeId"] = context.GroupId,
                        ["targetVersion"] = context.ToVersion,
                        ["provider"] = "manual",
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }

        return StageOutcome.Working(
            $"manual provider — parked until the operator redeploys the sidecar to {context.ToVersion}");
    }

    public async ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sink = _diagnostics.Create(context.GroupInstanceId);
        foreach (var workload in context.Workloads)
        {
            await sink.ClearAsync(SkewCode(workload.WorkloadId), cancellationToken).ConfigureAwait(false);
        }

        return StageOutcome.Done;
    }

    private static HashSet<string> AddedByThisUpdate(WorkloadUpdateStageInternals internals)
        => internals.Plan.Workloads
            .Where(item => item.TargetSpec.InstanceId
                == GroupManifestUpdatePlanner.AddedWorkloadInstanceId(internals.Intent.Id, item.WorkloadId))
            .Select(item => item.WorkloadId)
            .ToHashSet(StringComparer.Ordinal);

    private static string SkewCode(string workloadId) => $"workload.{workloadId}.{VersionSkewSuffix}";

    private bool IsManual(WorkloadUpdatePlanItem item) => !_providerRegistry.Describe(item.Reference.Provider).Materializes;

    private async Task<bool> AllSidecarsReportTheTargetAsync(
        WorkloadUpdateStageInternals internals, IReadOnlyList<WorkloadUpdatePlanItem> awaited, CancellationToken cancellationToken)
    {
        foreach (var item in awaited)
        {
            string? observed;
            try
            {
                observed = await _observedVersionReader
                    .GetObservedAppVersionAsync(item.Reference.InstanceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Reading the observed sidecar version of manual workload {WorkloadId} of intent {IntentId} faulted; "
                    + "keeping the update parked (fail-safe).",
                    item.WorkloadId, internals.Intent.Id);
                return false;
            }

            if (!string.Equals(observed, internals.Intent.ToVersion, StringComparison.Ordinal)
                || !await RunsTheTargetRenderingAsync(internals, item, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> RunsTheTargetRenderingAsync(
        WorkloadUpdateStageInternals internals, WorkloadUpdatePlanItem item, CancellationToken cancellationToken)
    {
        var mode = item.TargetSpec.Health.Mode;
        if (mode.IsLibraryOwned)
        {
            return true;
        }

        var expectedHash = WorkloadUpdateSteps.Find(internals.Intent, item.WorkloadId) is { } step
            ? WorkloadUpdateSteps.ExpectedHashFor(step, item)
            : item.TargetSpec.SpecHash;
        var contribution = await _healthContributors.ObserveAsync(
            new WorkloadHealthObservation
            {
                InstanceId = item.Reference.InstanceId,
                HealthMode = mode,
                DesiredSpecHash = expectedHash,
                InstalledGroupVersion = internals.Intent.ToVersion,
            },
            cancellationToken).ConfigureAwait(false);
        if (contribution.Presence.Kind == WorkloadPresenceSignalKind.PresentMatching)
        {
            return true;
        }

        _logger.LogInformation(
            "Manual workload {WorkloadId} of intent {IntentId} reports version {Version} but not the rendering this "
            + "update applies (presence {Presence}); keeping the update parked until the operator redeploys it.",
            item.WorkloadId, internals.Intent.Id, internals.Intent.ToVersion, contribution.Presence.Kind);
        return false;
    }
}
