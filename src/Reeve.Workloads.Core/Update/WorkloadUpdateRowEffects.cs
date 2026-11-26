using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update;

internal interface IWorkloadUpdateRowEffects
{
    Task EnsureRowsAsync(WorkloadUpdateIntent intent, WorkloadUpdatePlan plan, CancellationToken cancellationToken = default);

    Task RemoveAddedRowsAsync(WorkloadUpdateIntent intent, WorkloadUpdatePlan plan, CancellationToken cancellationToken = default);

    /// <summary>Begins the teardown of every row whose workload the candidate no longer declares, keeping its data.
    /// Idempotent: a row already tearing down is left to the teardown pass.</summary>
    Task TearDownDroppedRowsAsync(WorkloadUpdatePlan plan, CancellationToken cancellationToken = default);
}

internal sealed class WorkloadUpdateRowEffects(
    IWorkloadInstanceRepository instances,
    IWorkloadClock clock,
    ILogger<WorkloadUpdateRowEffects> logger,
    UnpinnedReferenceRolloutGuard rolloutGuard,
    IWorkloadDiagnosticsSinkFactory diagnostics,
    IWorkloadDroppedWorkloadTeardown droppedTeardown)
    : IWorkloadUpdateRowEffects
{
    private readonly IWorkloadDroppedWorkloadTeardown _droppedTeardown =
        droppedTeardown ?? throw new ArgumentNullException(nameof(droppedTeardown));
    private readonly IWorkloadInstanceRepository _instances = instances ?? throw new ArgumentNullException(nameof(instances));
    private readonly IWorkloadClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILogger<WorkloadUpdateRowEffects> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly UnpinnedReferenceRolloutGuard _rolloutGuard = rolloutGuard ?? throw new ArgumentNullException(nameof(rolloutGuard));

    public async Task EnsureRowsAsync(
        WorkloadUpdateIntent intent, WorkloadUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(plan);

        var rows = new Dictionary<string, WorkloadInstance>(StringComparer.Ordinal);
        var uninstalling = false;
        foreach (var row in await _instances.ListByPluginInstanceAsync(plan.GroupInstanceId, cancellationToken)
                     .ConfigureAwait(false))
        {
            rows[row.WorkloadId] = row;
            uninstalling |= row.State == WorkloadInstanceState.Terminating && Declares(plan.CandidateManifest, row.WorkloadId);
        }

        if (uninstalling)
        {
            _logger.LogDebug(
                "Group instance {GroupInstanceId} is being uninstalled; the update to {Version} creates no row.",
                plan.GroupInstanceId, plan.ToVersion);
            return;
        }

        var staged = intent.ReadTargetSpecs();
        if (staged is not { Verdict: WorkloadDocumentVerdict.Value, Value: { } stagedSpecs })
        {
            _logger.LogWarning(
                "Update intent {IntentId} has no readable target snapshot; it creates no row.", intent.Id);
            return;
        }

        var now = _clock.UtcNow;
        foreach (var item in plan.Workloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var instanceId = item.TargetSpec.InstanceId;
            if (rows.TryGetValue(item.WorkloadId, out var existing))
            {
                if (existing.Id != instanceId)
                {
                    _logger.LogWarning(
                        "Workload '{WorkloadId}' of group instance {GroupInstanceId} already has row {RowId}; the plan named it {InstanceId}.",
                        item.WorkloadId, plan.GroupInstanceId, existing.Id, instanceId);
                }

                continue;
            }

            if (!stagedSpecs.TryGetValue(item.WorkloadId, out var stagedSpec) || stagedSpec.InstanceId != instanceId)
            {
                _logger.LogDebug(
                    "Workload '{WorkloadId}' of group instance {GroupInstanceId} has no row, but update intent {IntentId} did not add it under {InstanceId}; no row is created.",
                    item.WorkloadId, plan.GroupInstanceId, intent.Id, instanceId);
                continue;
            }

            var created = WorkloadInstance.Create(instanceId, plan.GroupInstanceId, plan.GroupId, item.WorkloadId, now);
            created.SetProvider(item.TargetSpec.Provider, now);
            if (plan.CandidateManifest.Workloads.FirstOrDefault(workload => workload.Id == item.WorkloadId) is { } workload)
            {
                _rolloutGuard.RecordApplied(created,
                    plan.CandidateUnpinnedReferences.GetValueOrDefault(item.WorkloadId)
                        ?? new Dictionary<string, string?>(StringComparer.Ordinal),
                    plan.CandidateResolvedParameters.GetValueOrDefault(item.WorkloadId)
                        ?? new Dictionary<string, string?>(StringComparer.Ordinal),
                    WorkloadPasswordNames.For(workload), now);
            }
            await _instances.AddAsync(created, cancellationToken).ConfigureAwait(false);
            rows[item.WorkloadId] = created;

            _logger.LogInformation(
                "Created row {InstanceId} for workload '{WorkloadId}', which update intent {IntentId} added to group instance {GroupInstanceId}.",
                instanceId, item.WorkloadId, intent.Id, plan.GroupInstanceId);
        }
    }

    public async Task RemoveAddedRowsAsync(
        WorkloadUpdateIntent intent, WorkloadUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var row in await _instances.ListByPluginInstanceAsync(plan.GroupInstanceId, cancellationToken)
                     .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (row.Id != GroupManifestUpdatePlanner.AddedWorkloadInstanceId(intent.Id, row.WorkloadId))
            {
                continue;
            }

            if (row.State == WorkloadInstanceState.Terminating)
            {
                _logger.LogDebug(
                    "Row {InstanceId} of workload '{WorkloadId}' is being uninstalled; the rollback of update intent {IntentId} leaves it to the uninstall.",
                    row.Id, row.WorkloadId, intent.Id);
                continue;
            }

            await _instances.DeleteAsync(row, cancellationToken).ConfigureAwait(false);
            await WorkloadRepairsScanner.ClearOpenIssuesAsync(diagnostics, row, _logger, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Deleted row {InstanceId} of workload '{WorkloadId}', which update intent {IntentId} had added to group instance {GroupInstanceId}; the update is being rolled back.",
                row.Id, row.WorkloadId, intent.Id, plan.GroupInstanceId);
        }
    }

    public async Task TearDownDroppedRowsAsync(WorkloadUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var row in await _instances.ListByPluginInstanceAsync(plan.GroupInstanceId, cancellationToken)
                     .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Declares(plan.CandidateManifest, row.WorkloadId) || row.State == WorkloadInstanceState.Terminating)
            {
                continue;
            }

            var lastDeclaration = plan.PreviousUnreadable
                ? null
                : plan.PreviousManifest.Workloads.FirstOrDefault(
                    workload => string.Equals(workload.Id, row.WorkloadId, StringComparison.Ordinal));
            _logger.LogInformation(
                "Version {Version} of group instance {GroupInstanceId} no longer declares workload '{WorkloadId}'; tearing down row {InstanceId}, keeping its data.",
                plan.ToVersion, plan.GroupInstanceId, row.WorkloadId, row.Id);
            var whyUndeclared = plan.PreviousUnreadable
                ? $"the manifest of version '{plan.FromVersion}', which it ran, cannot be read"
                : $"version '{plan.FromVersion}' does not declare it either: an update that dropped workloads without tearing them down "
                    + "removed it from the plugin and left its row behind";
            await _droppedTeardown.BeginTeardownAsync(row, lastDeclaration, whyUndeclared, plan.ToVersion, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool Declares(DeploymentManifest manifest, string workloadId)
        => manifest.Workloads.Any(workload => string.Equals(workload.Id, workloadId, StringComparison.Ordinal));
}
