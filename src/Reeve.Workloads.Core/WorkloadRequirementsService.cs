using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadRequirementsService : IWorkloadRequirementsService
{
    private readonly IWorkloadGroupManifestSource manifests;
    private readonly IWorkloadOrchestratorSettingsService settings;
    private readonly IWorkloadProviderRegistry providers;
    private readonly IWorkloadInstanceRepository instances;
    private readonly IWorkloadReconcileInputsProvider inputs;
    private readonly IWorkloadUpdatePlanner planner;

    public WorkloadRequirementsService(
        IWorkloadGroupManifestSource manifests,
        IWorkloadOrchestratorSettingsService settings,
        IWorkloadProviderRegistry providers,
        IWorkloadInstanceRepository instances,
        IWorkloadReconcileInputsProvider inputs,
        IWorkloadUpdatePlanner planner)
    {
        this.manifests = manifests;
        this.settings = settings;
        this.providers = providers;
        this.instances = instances;
        this.inputs = inputs;
        this.planner = planner;
    }

    public async Task<WorkloadRequirementsResult> ForInstallAsync(
        WorkloadInstallRequirementsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        DeploymentManifest? declared;
        bool refused;
        if (query.Version is { } version)
        {
            var read = await manifests
                .GetAtVersionAsync(query.SourceKey, query.GroupId, version, cancellationToken)
                .ConfigureAwait(false);
            (declared, refused) = (read.Manifest, read.Refused);
        }
        else
        {
            var available = await manifests.GetAvailableAsync(query.GroupId, cancellationToken).ConfigureAwait(false);
            if (available.HighestAvailable is not null)
            {
                return WorkloadRequirementsResult.NotFound(
                    $"Module '{query.GroupId}' publishes versions, so it is installed at one.");
            }

            (declared, refused) = (available.Bundled, available.Refused);
        }

        var at = query.Version ?? "(bundled)";
        if (refused)
        {
            return WorkloadRequirementsResult.Invalid(
                $"Module '{query.GroupId}' {at} declares a deployment this build refuses to read.");
        }

        if (declared is not { } manifest)
        {
            return WorkloadRequirementsResult.NotFound(
                $"Module '{query.GroupId}' has no deployment manifest at version {at}.");
        }

        var providerId = query.Provider
            ?? (await settings.GetAsync(cancellationToken).ConfigureAwait(false)).EffectiveProvider;
        var descriptor = providers.Describe(providerId);

        var after = new Dictionary<string, WorkloadPrivilegeRequirements>(StringComparer.Ordinal);
        try
        {
            foreach (var workload in manifest.Workloads)
            {
                var resolved = WorkloadParameterResolver.Resolve(workload, query.Parameters, descriptor);
                after[workload.Id] = WorkloadPrivilegeRequirements.FromWorkload(workload, WorkloadRequirementsFacts.NonNull(resolved), descriptor);
            }
        }
        catch (WorkloadParameterResolutionException ex)
        {
            return WorkloadRequirementsResult.Invalid(ex.Message);
        }

        return WorkloadRequirementsResult.Found(Read(before: null, after));
    }

    public async Task<WorkloadRequirementsResult> ForParameterEditAsync(
        Guid workloadInstanceId,
        IReadOnlyDictionary<string, string?> newValues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newValues);

        var instance = await instances.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return WorkloadRequirementsResult.NotFound($"No workload instance {workloadInstanceId}.");
        }

        var answer = await inputs.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            return WorkloadRequirementsResult.HostRefused(refusal);
        }

        if (answer.Inputs is not { } resolvedInputs)
        {
            return WorkloadRequirementsResult.NotFound(
                $"Workload '{instance.WorkloadId}' of module '{instance.GroupId}' is no longer declared.");
        }

        var manifest = resolvedInputs.WorkloadManifest;
        var provider = resolvedInputs.EffectiveProvider;
        var stored = resolvedInputs.ResolvedParameters;
        var merged = WorkloadParameterEditMerge.Merge(stored, newValues);

        IReadOnlyDictionary<string, string?> oldResolved;
        IReadOnlyDictionary<string, string?> newResolved;
        try
        {
            oldResolved = WorkloadParameterResolver.Resolve(manifest, stored, provider);
            newResolved = WorkloadParameterResolver.Resolve(manifest, merged, provider);
        }
        catch (WorkloadParameterResolutionException ex)
        {
            return WorkloadRequirementsResult.Invalid(ex.Message);
        }

        return WorkloadRequirementsResult.Found(Read(
            before: WorkloadRequirementsFacts.ForWorkload(instance.WorkloadId, manifest, oldResolved, provider),
            after: WorkloadRequirementsFacts.ForWorkload(instance.WorkloadId, manifest, newResolved, provider)));
    }

    public async Task<WorkloadRequirementsResult> ForUpdateAsync(
        Guid groupInstanceId,
        string targetVersion,
        IReadOnlyDictionary<string, string?>? operatorParameters = null,
        IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);

        WorkloadUpdatePlan plan;
        try
        {
            plan = await planner
                .BuildPlanAsync(
                    groupInstanceId, targetVersion, intentId: null, operatorParameters, adoptDeclaredResourceWorkloadIds,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkloadParameterResolutionException ex)
        {
            return WorkloadRequirementsResult.Invalid(ex.Message);
        }
        catch (WorkloadHostRefusedException refused)
        {
            return WorkloadRequirementsResult.HostRefused(refused.Reason);
        }
        catch (InvalidOperationException ex)
        {
            return WorkloadRequirementsResult.NotFound(ex.Message);
        }

        return WorkloadRequirementsResult.Found(Read(before: plan.PreviousRequirements, after: plan.CandidateRequirements));
    }

    private static WorkloadRequirementsRead Read(
        IReadOnlyDictionary<string, WorkloadPrivilegeRequirements>? before,
        IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> after)
    {
        var previous = before ?? new Dictionary<string, WorkloadPrivilegeRequirements>();
        return new WorkloadRequirementsRead
        {
            Before = before,
            After = after,
            Changes = after.Keys
                .Where(previous.ContainsKey)
                .ToDictionary(
                    id => id,
                    id => WorkloadPrivilegeRequirements.Compare(previous[id], after[id]),
                    StringComparer.Ordinal),
            AddedWorkloadIds = before is null
                ? []
                : after.Keys.Where(id => !previous.ContainsKey(id)).Order(StringComparer.Ordinal).ToList(),
            RemovedWorkloadIds = previous.Keys.Where(id => !after.ContainsKey(id)).Order(StringComparer.Ordinal).ToList(),
            Fingerprint = WorkloadRequirementsFacts.Fingerprint(after),
        };
    }
}
