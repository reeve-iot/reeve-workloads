using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Manual;

internal sealed class ManualWorkloadProvider : IWorkloadProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ManualWorkloadProvider> _logger;

    public ManualWorkloadProvider(IServiceScopeFactory scopeFactory, ILogger<ManualWorkloadProvider> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WorkloadProviderDescriptor Descriptor => ManualProvider.Descriptor;

    public Task<WorkloadApplyResult> ApplyAsync(
        WorkloadDesiredSpec spec,
        IProgress<WorkloadApplyProgress>? progress,
        CancellationToken cancellationToken = default,
        bool forceConflicts = false)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return Task.FromResult(WorkloadApplyResult.Applied(
            spec.SpecHash,
            "Manual mode: nothing is deployed by Reeve — deploy the rendered artifacts (compose / kubernetes / " +
            "helm-values / env). Reeve is awaiting the operator's deploy and will observe whether it is live."));
    }

    public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Manual teardown of instance {InstanceId} is a no-op — the operator removes their own deployment.",
            reference.InstanceId);
        return Task.CompletedTask;
    }

    public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Manual suspend of instance {InstanceId} is a no-op — Reeve cannot pause the operator's deployment.",
            reference.InstanceId);
        return Task.CompletedTask;
    }

    public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Manual resume of instance {InstanceId} is a no-op — Reeve cannot start the operator's deployment.",
            reference.InstanceId);
        return Task.CompletedTask;
    }

    public async Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
    {
        bool live;
        try
        {
            var scope = _scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var liveness = scope.ServiceProvider.GetRequiredService<IWorkloadExternalLiveness>();
                live = await liveness.IsLiveAsync(reference.InstanceId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Manual GetStatus of instance {InstanceId} could not read whether it is live; reporting Unknown.",
                reference.InstanceId);
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Unknown,
                ProviderReady = false,
                Message = "Manual mode: whether the workload is live could not be read — status is unknown.",
            };
        }

        return ManualStatusMapper.Map(live);
    }

    public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var checks = new List<PreflightCheck>();
        AppendInformationalNotes(spec, checks);

        return Task.FromResult(new WorkloadPreflightResult { Checks = checks });
    }

    public Task<WorkloadLogs> GetRecentLogsAsync(
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        CancellationToken cancellationToken = default)
    {
        var guidance = WorkloadLogLine.Line(
            $"Manual mode: Reeve cannot fetch logs for this workload — run `docker logs reeve-wl-{reference.Inst8}` " +
            $"(or `kubectl logs -l reeve.io/workload-instance={reference.InstanceId}`) on the host where you deployed it.");

        return Task.FromResult(new WorkloadLogs { Lines = new[] { guidance } });
    }

    public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken = default)
        => Task.FromResult(WorkloadAttachmentExistence.NotApplicable(
            "Manual mode: Reeve does not manage this runtime and cannot look up the networks it is attached to."));

    private static void AppendInformationalNotes(WorkloadDesiredSpec spec, List<PreflightCheck> checks)
    {
        if (spec.HostRequirements.Count > 0)
        {
            var names = spec.HostRequirements
                .Select(r => string.IsNullOrWhiteSpace(r.Name) ? r.Kind.ToString() : r.Name!)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (names.Count > 0)
            {
                checks.Add(PreflightCheck.Pass(
                    "host-requirements",
                    $"This workload declares host prerequisites ({string.Join(", ", names)}) — ensure the node you " +
                    "deploy it on satisfies them (manual mode cannot verify this).",
                    PreflightSeverity.Info));
            }
        }

        if (spec.Platforms.Count > 0)
        {
            checks.Add(PreflightCheck.Pass(
                "platforms",
                $"This workload targets platform(s) {string.Join(", ", spec.Platforms)} — deploy it on a matching " +
                "architecture (manual mode cannot enforce node affinity).",
                PreflightSeverity.Info));
        }
    }
}
