using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadResourceLimitsService : IWorkloadResourceLimitsService
{
    private const string RequestsCpuKey = "resources.requests.cpu";
    private const string RequestsMemoryKey = "resources.requests.memory";
    private const string LimitsCpuKey = "resources.limits.cpu";
    private const string LimitsMemoryKey = "resources.limits.memory";

    private readonly IWorkloadInstanceRepository repository;
    private readonly IActiveWorkloadIntentProvider intentProvider;
    private readonly IWorkloadReconcileInputsProvider inputsProvider;
    private readonly IWorkloadReconcileNudge reconcileNudge;
    private readonly IWorkloadClock clock;
    private readonly IWorkloadAuditSink audit;
    private readonly ILogger<WorkloadResourceLimitsService> logger;
    private readonly IWorkloadProviderRegistry providerRegistry;

    public WorkloadResourceLimitsService(
        IWorkloadInstanceRepository repository,
        IActiveWorkloadIntentProvider intentProvider,
        IWorkloadReconcileInputsProvider inputsProvider,
        IWorkloadReconcileNudge reconcileNudge,
        IWorkloadClock clock,
        IWorkloadAuditSink audit,
        ILogger<WorkloadResourceLimitsService> logger,
        IWorkloadProviderRegistry providerRegistry)
    {
        this.repository = repository;
        this.intentProvider = intentProvider;
        this.inputsProvider = inputsProvider;
        this.reconcileNudge = reconcileNudge ?? throw new ArgumentNullException(nameof(reconcileNudge));
        this.clock = clock;
        this.audit = audit;
        this.logger = logger;
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
    }

    public async Task<WorkloadResourcesResponse?> GetAsync(
        Guid workloadInstanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return null;
        }

        var answer = await inputsProvider.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            logger.LogWarning(
                "workloads.resources: instance={InstanceId} no resource read — the host refused its bootstrap: {Refusal}.",
                instance.Id, refusal);
            return null;
        }

        if (answer.Inputs is not { } inputs)
        {
            return null;
        }

        var declared = DeclaredValues(inputs.WorkloadManifest.Resources);
        var overrides = StoredOverrides(instance.GetResolvedParameters());

        return new WorkloadResourcesResponse
        {
            Declared = declared,
            Overrides = overrides,
            Effective = Effective(overrides, declared),
            RequestsCpuApplicable = inputs.EffectiveProvider.SupportsCpuRequests,
        };
    }

    public async Task<WorkloadResourceLimitsResult> SetAsync(
        Guid workloadInstanceId,
        SetWorkloadResourcesRequest request,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var instance = await repository.GetByIdAsync(workloadInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return WorkloadResourceLimitsResult.NotFound();
        }

        if (instance.IsOwnershipReleased(providerRegistry) && !instance.IsHeldByHost)
        {
            return WorkloadResourceLimitsResult.Rejected(
                $"Reeve released ownership of the workload instance ({instance.State}); its resource limits cannot be changed. "
                + "Re-adopt the instance first.");
        }

        if (instance.IsHeldByHost)
        {
            return WorkloadResourceLimitsResult.Rejected(
                "The host holds the workload instance suspended; its resource limits cannot be changed until the host resumes it.");
        }

        if (!instance.CanMutate)
        {
            return WorkloadResourceLimitsResult.Rejected(
                $"The workload instance is {instance.State}; its resource limits cannot be changed.");
        }

        var answer = await inputsProvider.GetInputsAsync(instance, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            return WorkloadResourceLimitsResult.HostRefused(refusal);
        }

        if (answer.Inputs is not { } inputs)
        {
            return WorkloadResourceLimitsResult.NotFound();
        }

        var activeIntentId = await intentProvider
            .GetActiveIntentIdAsync(instance.GroupInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (activeIntentId is { } intentId)
        {
            return WorkloadResourceLimitsResult.Conflict(intentId);
        }

        var incoming = new WorkloadResourceValues
        {
            RequestsCpu = Normalize(request.RequestsCpu),
            RequestsMemory = Normalize(request.RequestsMemory),
            LimitsCpu = Normalize(request.LimitsCpu),
            LimitsMemory = Normalize(request.LimitsMemory),
        };

        var perAxisError = FirstAxisError(incoming);
        if (perAxisError is not null)
        {
            return WorkloadResourceLimitsResult.Rejected(perAxisError);
        }

        var declared = DeclaredValues(inputs.WorkloadManifest.Resources);
        var effective = Effective(incoming, declared);
        var pairError = ResourceQuantityPolicy.ValidatePair(new ResolvedResources
        {
            RequestsCpu = effective.RequestsCpu,
            RequestsMemory = effective.RequestsMemory,
            LimitsCpu = effective.LimitsCpu,
            LimitsMemory = effective.LimitsMemory,
        });
        if (pairError is not null)
        {
            return WorkloadResourceLimitsResult.Rejected(pairError);
        }

        var stored = instance.GetResolvedParameters();
        var existing = StoredOverrides(stored);
        if (SameOverrides(existing, incoming))
        {
            return WorkloadResourceLimitsResult.NoChange(effective);
        }

        var merged = new Dictionary<string, string?>(stored, StringComparer.Ordinal);
        Apply(merged, RequestsCpuKey, incoming.RequestsCpu);
        Apply(merged, RequestsMemoryKey, incoming.RequestsMemory);
        Apply(merged, LimitsCpuKey, incoming.LimitsCpu);
        Apply(merged, LimitsMemoryKey, incoming.LimitsMemory);

        var now = clock.UtcNow;
        instance.SetResolvedParameters(merged, now);
        instance.SetAppliedSpecHash(null, now);

        instance.BeginOperatorRedeploy("Applying operator resource limits.", now);

        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
        await reconcileNudge.NudgeInstanceAsync(instance.Id, "workload-resource-limits", cancellationToken)
            .ConfigureAwait(false);

        var changes = DescribeChanges(existing, incoming, declared);
        await audit.WriteAsync(
            new WorkloadAuditEntry(
                actorUserId,
                "workload.resources.set",
                nameof(WorkloadInstance),
                instance.Id,
                new { Changes = changes }),
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "workloads.resources_set: instance={InstanceId} actor={ActorUserId} changed {Changes}; reconcile nudged.",
            instance.Id, actorUserId, string.Join(", ", changes));

        return WorkloadResourceLimitsResult.Success(effective);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Apply(Dictionary<string, string?> merged, string key, string? value)
    {
        if (value is null)
        {
            merged.Remove(key);
        }
        else
        {
            merged[key] = value;
        }
    }

    private static string? FirstAxisError(WorkloadResourceValues values)
        => ResourceQuantityPolicy.Validate(ResourceQuantityKind.Cpu, values.RequestsCpu)
            ?? ResourceQuantityPolicy.Validate(ResourceQuantityKind.Memory, values.RequestsMemory)
            ?? ResourceQuantityPolicy.Validate(ResourceQuantityKind.Cpu, values.LimitsCpu)
            ?? ResourceQuantityPolicy.Validate(ResourceQuantityKind.Memory, values.LimitsMemory);

    private static WorkloadResourceValues DeclaredValues(WorkloadResources? declared) => new()
    {
        RequestsCpu = Normalize(declared?.Requests?.Cpu),
        RequestsMemory = Normalize(declared?.Requests?.Memory),
        LimitsCpu = Normalize(declared?.Limits?.Cpu),
        LimitsMemory = Normalize(declared?.Limits?.Memory),
    };

    private static WorkloadResourceValues StoredOverrides(IReadOnlyDictionary<string, string?> stored) => new()
    {
        RequestsCpu = Read(stored, RequestsCpuKey),
        RequestsMemory = Read(stored, RequestsMemoryKey),
        LimitsCpu = Read(stored, LimitsCpuKey),
        LimitsMemory = Read(stored, LimitsMemoryKey),
    };

    private static string? Read(IReadOnlyDictionary<string, string?> stored, string key)
        => stored.TryGetValue(key, out var value) ? Normalize(value) : null;

    private static WorkloadResourceValues Effective(
        WorkloadResourceValues overrides, WorkloadResourceValues declared) => new()
    {
        RequestsCpu = overrides.RequestsCpu ?? declared.RequestsCpu,
        RequestsMemory = overrides.RequestsMemory ?? declared.RequestsMemory,
        LimitsCpu = overrides.LimitsCpu ?? declared.LimitsCpu,
        LimitsMemory = overrides.LimitsMemory ?? declared.LimitsMemory,
    };

    private static bool SameOverrides(WorkloadResourceValues a, WorkloadResourceValues b)
        => string.Equals(a.RequestsCpu, b.RequestsCpu, StringComparison.Ordinal)
            && string.Equals(a.RequestsMemory, b.RequestsMemory, StringComparison.Ordinal)
            && string.Equals(a.LimitsCpu, b.LimitsCpu, StringComparison.Ordinal)
            && string.Equals(a.LimitsMemory, b.LimitsMemory, StringComparison.Ordinal);

    private static IReadOnlyList<string> DescribeChanges(
        WorkloadResourceValues before, WorkloadResourceValues after, WorkloadResourceValues declared)
    {
        var changes = new List<string>();
        Add(changes, "requests.cpu", before.RequestsCpu, after.RequestsCpu, declared.RequestsCpu);
        Add(changes, "requests.memory", before.RequestsMemory, after.RequestsMemory, declared.RequestsMemory);
        Add(changes, "limits.cpu", before.LimitsCpu, after.LimitsCpu, declared.LimitsCpu);
        Add(changes, "limits.memory", before.LimitsMemory, after.LimitsMemory, declared.LimitsMemory);
        return changes;
    }

    private static void Add(List<string> changes, string axis, string? before, string? after, string? declared)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        changes.Add($"{axis}: {Render(before, declared)} → {Render(after, declared)}");
    }

    private static string Render(string? value, string? declared)
        => value is not null
            ? $"{value} (override)"
            : declared is not null ? $"{declared} (declared)" : "unset";
}
