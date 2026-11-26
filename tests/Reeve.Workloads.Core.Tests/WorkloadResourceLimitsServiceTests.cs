using Reeve.Workloads.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Testing;
using Xunit;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadResourceLimitsServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private const string PluginTypeId = "acme.zigbee";
    private const string WorkloadId = "coordinator";

    private readonly InMemoryRepository repository = new();
    private readonly FakeWorkloadClusterBus bus = new();
    private readonly StubIntentProvider intentProvider = new();
    private readonly StubInputsProvider inputsProvider = new();
    private readonly RecordingAuditWriter audit = new();

    private WorkloadResourceLimitsService CreateService(
        Microsoft.Extensions.Logging.ILogger<WorkloadResourceLimitsService>? logger = null)
        => new(repository, intentProvider, inputsProvider, bus.Nudge, new FixedClock(Now), audit,
            logger ?? NullLogger<WorkloadResourceLimitsService>.Instance, ProviderRegistryFakes.FirstPartyRegistry());

    private static WorkloadManifest BuildManifest(
        string? requestsCpu = null,
        string? requestsMemory = null,
        string? limitsCpu = null,
        string? limitsMemory = null) => new()
    {
        Id = WorkloadId,
        DisplayName = "Zigbee coordinator",
        Image = new WorkloadImage { Ref = "ghcr.io/reeve/z2m", Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container, HeartbeatSeconds = 30 },
        Resources = requestsCpu is null && requestsMemory is null && limitsCpu is null && limitsMemory is null
            ? null
            : new WorkloadResources
            {
                Requests = requestsCpu is null && requestsMemory is null
                    ? null
                    : new ResourceQuantities { Cpu = requestsCpu, Memory = requestsMemory },
                Limits = limitsCpu is null && limitsMemory is null
                    ? null
                    : new ResourceQuantities { Cpu = limitsCpu, Memory = limitsMemory },
            },
    };

    private WorkloadInstance SeedHealthy(
        WorkloadManifest manifest,
        IReadOnlyDictionary<string, string?>? parameters = null,
        WorkloadProviderDescriptor? provider = null)
    {
        var effective = provider ?? KubernetesProvider.Descriptor;
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), PluginTypeId, WorkloadId, Now);
        instance.SetProvider(effective.Id, Now);
        instance.SetAppliedSpecHash("applied-hash", Now);
        if (parameters is not null)
        {
            instance.SetResolvedParameters(parameters, Now);
        }
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        repository.AddAsync(instance).GetAwaiter().GetResult();
        inputsProvider.Manifest = manifest;
        inputsProvider.Provider = effective;
        return instance;
    }

    [Fact]
    public async Task An_unknown_instance_is_not_found()
    {
        inputsProvider.Manifest = BuildManifest();

        var result = await CreateService().SetAsync(
            Guid.NewGuid(), new SetWorkloadResourcesRequest { LimitsCpu = "250m" }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.NotFound);
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_refusal_refuses_the_write_with_the_hosts_reason_and_writes_nothing()
    {
        var instance = SeedHealthy(BuildManifest());
        inputsProvider.Refusal = "host says no";

        var result = await CreateService().SetAsync(
            instance.Id, new SetWorkloadResourcesRequest { LimitsCpu = "250m" }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.HostRefused);
        result.Error.Should().Contain("host says no");
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("resources.limits.cpu");
        bus.Published.Should().BeEmpty();
        audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task The_read_under_a_host_refusal_answers_nothing_and_logs_the_reason()
    {
        var instance = SeedHealthy(BuildManifest(limitsCpu: "500m"));
        inputsProvider.Refusal = "host says no";
        var logger = new Reeve.Workloads.Core.Tests.Hosting.RecordingLogger<WorkloadResourceLimitsService>();

        (await CreateService(logger).GetAsync(instance.Id, CancellationToken.None)).Should().BeNull();
        logger.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Text.Contains("host says no"));
    }

    [Fact]
    public async Task An_ownership_frozen_instance_is_rejected_without_mutation()
    {
        var instance = SeedHealthy(BuildManifest());
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.OperatorPaused, null, Now);
        await repository.UpdateAsync(instance);

        var result = await CreateService().SetAsync(
            instance.Id, new SetWorkloadResourcesRequest { LimitsCpu = "250m" }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.Rejected);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("resources.limits.cpu");
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task An_externally_managed_instance_is_rejected_without_mutation()
    {
        var instance = SeedHealthy(BuildManifest());
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.OperatorPaused, null, Now);
        await repository.UpdateAsync(instance);
        instance.CanMutate.Should().BeTrue();

        var result = await CreateService().SetAsync(
            instance.Id, new SetWorkloadResourcesRequest { LimitsCpu = "250m" }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.Rejected);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("resources.limits.cpu");
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connected_manual_instance_takes_the_change()
    {
        var instance = SeedHealthy(BuildManifest(), provider: ManualProvider.Descriptor);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);
        await repository.UpdateAsync(instance);

        var result = await CreateService().SetAsync(
            instance.Id, new SetWorkloadResourcesRequest { LimitsCpu = "250m" }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["resources.limits.cpu"].Should().Be("250m");
        stored.AppliedSpecHash.Should().BeNull("the operator redeploys the re-rendered artifact");
    }

    [Fact]
    public async Task A_host_held_instance_is_rejected_and_keeps_its_hold()
    {
        var instance = SeedHealthy(BuildManifest());
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", Now);
        await repository.UpdateAsync(instance);

        var result = await CreateService().SetAsync(
            instance.Id, new SetWorkloadResourcesRequest { LimitsCpu = "250m" }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.Rejected);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.IsHeldByHost.Should().BeTrue("an operator cannot lift the host's hold");
        stored.GetResolvedParameters().Should().NotContainKey("resources.limits.cpu");
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_paused_instance_stores_the_limits_and_stays_paused()
    {
        var instance = SeedHealthy(BuildManifest());
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "paused", Now);
        await repository.UpdateAsync(instance);

        var result = await CreateService().SetAsync(
            instance.Id, new SetWorkloadResourcesRequest { LimitsCpu = "250m" }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.State.Should().Be(WorkloadInstanceState.Suspended, "a pause is a deliberate hold; resume applies the change");
        stored.StateReason.Should().Be(WorkloadStateReason.OperatorPaused);
        stored.AppliedSpecHash.Should().BeNull();
        stored.GetResolvedParameters()["resources.limits.cpu"].Should().Be("250m");
    }

    [Fact]
    public async Task An_active_update_intent_conflicts_and_echoes_the_intent_id()
    {
        var instance = SeedHealthy(BuildManifest());
        var intentId = Guid.NewGuid();
        intentProvider.ActiveIntentId = intentId;

        var result = await CreateService().SetAsync(
            instance.Id, new SetWorkloadResourcesRequest { LimitsCpu = "250m" }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.Conflict);
        result.ActiveIntentId.Should().Be(intentId);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("resources.limits.cpu");
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_garbage_quantity_is_rejected_and_nothing_is_persisted()
    {
        var instance = SeedHealthy(BuildManifest());

        var result = await CreateService().SetAsync(
            instance.Id, new SetWorkloadResourcesRequest { LimitsMemory = "64 MB" }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.Rejected);
        result.Error.Should().Contain("64 MB");
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("resources.limits.memory");
        stored.AppliedSpecHash.Should().Be("applied-hash");
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_limit_below_its_manifest_declared_request_is_rejected()
    {
        var instance = SeedHealthy(BuildManifest(requestsCpu: "500m"));

        var result = await CreateService().SetAsync(
            instance.Id, new SetWorkloadResourcesRequest { LimitsCpu = "250m" }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.Rejected);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("resources.limits.cpu");
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Setting_an_override_persists_it_clears_the_applied_hash_and_nudges()
    {
        var instance = SeedHealthy(BuildManifest(limitsMemory: "256Mi"));

        var result = await CreateService().SetAsync(
            instance.Id,
            new SetWorkloadResourcesRequest { LimitsMemory = "512Mi", LimitsCpu = "250m" },
            Guid.NewGuid(),
            CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.Success);
        result.Effective!.LimitsMemory.Should().Be("512Mi");

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["resources.limits.memory"].Should().Be("512Mi");
        stored.GetResolvedParameters()["resources.limits.cpu"].Should().Be("250m");
        stored.AppliedSpecHash.Should().BeNull("the reconciler must re-apply");
        stored.State.Should().Be(WorkloadInstanceState.Deploying);

        bus.Published.Should().ContainSingle();
        var (channel, payload) = bus.Published[0];
        channel.Should().Be(WorkloadClusterChannels.ReconcileRequest);
        payload.Should().BeOfType<WorkloadReconcileRequestMessage>().Which.InstanceId.Should().Be(instance.Id);

        audit.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Clearing_an_axis_removes_the_key_and_falls_back_to_the_declaration()
    {
        var instance = SeedHealthy(
            BuildManifest(limitsMemory: "256Mi"),
            new Dictionary<string, string?> { ["resources.limits.memory"] = "512Mi" });

        var result = await CreateService().SetAsync(
            instance.Id, new SetWorkloadResourcesRequest { LimitsMemory = null }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("resources.limits.memory");
        result.Effective!.LimitsMemory.Should().Be("256Mi", "the declaration takes over again");
    }

    [Fact]
    public async Task Posting_the_values_already_stored_changes_nothing_and_does_not_nudge()
    {
        var instance = SeedHealthy(
            BuildManifest(limitsMemory: "256Mi"),
            new Dictionary<string, string?> { ["resources.limits.memory"] = "512Mi" });

        var result = await CreateService().SetAsync(
            instance.Id, new SetWorkloadResourcesRequest { LimitsMemory = "512Mi" }, null, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadResourceLimitsOutcome.NoChange);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.AppliedSpecHash.Should().Be("applied-hash", "a no-op must not force a re-apply");
        stored.State.Should().Be(WorkloadInstanceState.Healthy);
        bus.Published.Should().BeEmpty();
        audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task The_read_reports_declared_override_and_effective_per_axis()
    {
        var instance = SeedHealthy(
            BuildManifest(limitsCpu: "100m", limitsMemory: "256Mi"),
            new Dictionary<string, string?> { ["resources.limits.cpu"] = "50m" });

        var read = await CreateService().GetAsync(instance.Id, CancellationToken.None);

        read!.Declared.LimitsCpu.Should().Be("100m");
        read.Overrides.LimitsCpu.Should().Be("50m");
        read.Effective.LimitsCpu.Should().Be("50m");
        read.Declared.LimitsMemory.Should().Be("256Mi");
        read.Overrides.LimitsMemory.Should().BeNull();
        read.Effective.LimitsMemory.Should().Be("256Mi");
        read.RequestsCpuApplicable.Should().BeTrue("this instance runs on Kubernetes");
    }

    [Fact]
    public async Task Docker_reports_requests_cpu_as_not_applicable_because_the_provider_ignores_it()
    {
        var instance = SeedHealthy(BuildManifest(), provider: DockerProvider.Descriptor);

        var read = await CreateService().GetAsync(instance.Id, CancellationToken.None);

        read!.RequestsCpuApplicable.Should().BeFalse();
    }

    private sealed class StubIntentProvider : IActiveWorkloadIntentProvider
    {
        public Guid? ActiveIntentId { get; set; }

        public Task<Reeve.Workloads.Abstractions.Domain.DesiredSpec.WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(
            Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<Reeve.Workloads.Abstractions.Domain.DesiredSpec.WorkloadDesiredSpec?>(null);

        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken)
            => Task.FromResult(ActiveIntentId);
    }

    private sealed class StubInputsProvider : IWorkloadReconcileInputsProvider
    {
        public WorkloadManifest? Manifest { get; set; }
        public WorkloadProviderDescriptor Provider { get; set; } = KubernetesProvider.Descriptor;

        public string? Refusal { get; set; }

        public Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => ReconcileInputsAnswers.Of(Manifest is null
                ? null
                : new WorkloadReconcileInputs
                {
                    Namer = DefaultWorkloadResourceNamer.Instance,
                    WorkloadManifest = Manifest,
                    ResolvedParameters = instance.GetResolvedParameters(),
                    Settings = new WorkloadOrchestratorSettings(),
                    Bootstrap = new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "reeve-wl-secret" },
                    EffectiveProvider = Provider,
                    InstallationId = "test-install",
                }, Refusal);
    }

    private sealed class RecordingAuditWriter : IWorkloadAuditSink
    {
        public List<Reeve.Workloads.Abstractions.WorkloadAuditEntry> Entries { get; } = [];

        public Task WriteAsync(Reeve.Workloads.Abstractions.WorkloadAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }


    private sealed class FixedClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class InMemoryRepository : IWorkloadInstanceRepository
    {
        private readonly Dictionary<Guid, WorkloadInstance> byId = [];

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(byId.GetValueOrDefault(id));

        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => Task.FromResult(byId.Values.FirstOrDefault(
                i => i.GroupInstanceId == pluginInstanceId && i.WorkloadId == workloadId));

        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(
                byId.Values.Where(i => i.GroupInstanceId == pluginInstanceId).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(byId.Values.Where(i => i.State == state).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(byId.Values.ToList());

        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(
                byId.Values.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count()));

        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            byId[instance.Id] = instance;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            byId[instance.Id] = instance;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            byId.Remove(instance.Id);
            return Task.CompletedTask;
        }
    }
}
