using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Core.Tests;


public sealed class WorkloadParameterEditGateTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryRepository repository = new();

    private WorkloadParameterEditService Build() => new(
        repository,
        new UnreachableIntentProvider(),
        new UnreachableInputsProvider(),
        new UnreachableReconcileNudge(),
        new FixedClock(Now),
        new WorkloadDeviceGroupIdWriter(new UnreachableDeviceGroups()),
        NullLogger<WorkloadParameterEditService>.Instance,
        new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider()),
        new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider()),
        new NoWorkloadAuditSink(),
        ProviderRegistryFakes.FirstPartyRegistry());


    private Task<WorkloadParameterEditResult> EditAsync()
        => Build().UpdateParametersAsync(
            InstanceId,
            new Dictionary<string, string?> { ["port"] = "8080" },
            actorUserId: null,
            precondition: AnyPrecondition);

    private static readonly WorkloadWritePrecondition AnyPrecondition = new() { RequirementsFingerprint = "judged" };

    [Fact]
    public async Task An_edit_cannot_resurrect_an_instance_that_is_being_torn_down()
    {
        var instance = Seed(WorkloadInstanceState.Terminating);

        var result = await EditAsync();

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        instance.State.Should().Be(WorkloadInstanceState.Terminating);
        instance.AppliedSpecHash.Should().Be("applied-hash", "a refused edit clears nothing");
        repository.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task An_edit_cannot_half_mutate_an_instance_nobody_can_observe()
    {
        var instance = Seed(WorkloadInstanceState.Unknown);

        var result = await EditAsync();

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        instance.AppliedSpecHash.Should().Be("applied-hash");
        instance.GetResolvedParameters().Should().NotContainKey("port");
        repository.Updates.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Switching)]
    [InlineData(WorkloadInstanceState.ExternallyManaged)]
    public async Task An_edit_cannot_run_on_an_ownership_frozen_instance(WorkloadInstanceState state)
    {
        var instance = Seed(state);

        var result = await EditAsync();

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        instance.AppliedSpecHash.Should().Be("applied-hash");
        repository.Updates.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Terminating)]
    [InlineData(WorkloadInstanceState.Unknown)]
    [InlineData(WorkloadInstanceState.Switching)]
    [InlineData(WorkloadInstanceState.ExternallyManaged)]
    public async Task Every_refusal_names_the_state_that_caused_it(WorkloadInstanceState state)
    {
        Seed(state);

        var result = await EditAsync();

        result.Error.Should().Contain(state.ToString());
    }

    [Theory]
    [InlineData(WorkloadInstanceState.Healthy)]
    [InlineData(WorkloadInstanceState.Degraded)]
    [InlineData(WorkloadInstanceState.Failed)]
    [InlineData(WorkloadInstanceState.Deploying)]
    [InlineData(WorkloadInstanceState.Suspended)]
    public async Task An_edit_is_admitted_on_every_live_state(WorkloadInstanceState state)
    {
        Seed(state);

        await FluentActions.Awaiting(EditAsync)
            .Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*reconcile inputs*", "the gate admitted it and the next collaborator is the inputs provider");
    }

    [Fact]
    public async Task An_unknown_instance_is_not_found_before_anything_is_read()
    {
        var result = await EditAsync();

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.NotFound);
    }

    [Fact]
    public async Task A_null_value_set_is_refused_before_the_instance_is_even_loaded()
    {
        await FluentActions
            .Awaiting(() => Build().UpdateParametersAsync(
                InstanceId, null!, null, AnyPrecondition))
            .Should().ThrowAsync<ArgumentNullException>();

        repository.Reads.Should().Be(0);
    }

    [Fact]
    public async Task A_null_precondition_is_refused_before_the_instance_is_even_loaded()
    {
        await FluentActions
            .Awaiting(() => Build().UpdateParametersAsync(
                InstanceId, new Dictionary<string, string?>(), null, null!))
            .Should().ThrowAsync<ArgumentNullException>();

        repository.Reads.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_fingerprint_is_refused_before_the_instance_is_even_loaded(string fingerprint)
    {
        await FluentActions
            .Awaiting(() => Build().UpdateParametersAsync(
                InstanceId,
                new Dictionary<string, string?>(),
                null,
                new WorkloadWritePrecondition { RequirementsFingerprint = fingerprint }))
            .Should().ThrowAsync<ArgumentException>();

        repository.Reads.Should().Be(0);
    }

    private WorkloadInstance Seed(WorkloadInstanceState state)
    {
        var instance = WorkloadInstance.Create(
            InstanceId, GroupInstanceId, "com.reeve.matter", "gateway", Now - TimeSpan.FromHours(1));
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.SetAppliedSpecHash("applied-hash", Now);

        switch (state)
        {
            case WorkloadInstanceState.Healthy:
                break;
            case WorkloadInstanceState.ExternallyManaged:
                instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.ExternalHealthObserved, null, Now);
                instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);
                break;
            default:
                instance.TransitionTo(state, ReasonFor(state), null, Now);
                break;
        }

        instance.State.Should().Be(state);
        repository.Add(instance);
        return instance;
    }

    private static WorkloadStateReason ReasonFor(WorkloadInstanceState state) => state switch
    {
        WorkloadInstanceState.Degraded => WorkloadStateReason.HealthLost,
        WorkloadInstanceState.Failed => WorkloadStateReason.HealthLost,
        WorkloadInstanceState.Unknown => WorkloadStateReason.HealthLost,
        WorkloadInstanceState.Terminating => WorkloadStateReason.Uninstalling,
        WorkloadInstanceState.Suspended => WorkloadStateReason.OperatorPaused,
        WorkloadInstanceState.Switching => WorkloadStateReason.ExternalHealthObserved,
        _ => WorkloadStateReason.Redeploying,
    };

    private sealed class FixedClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class InMemoryRepository : IWorkloadInstanceRepository
    {
        private readonly List<WorkloadInstance> instances = [];

        public int Reads { get; private set; }

        public List<Guid> Updates { get; } = [];

        public void Add(WorkloadInstance instance) => instances.Add(instance);

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(instances.FirstOrDefault(i => i.Id == id));
        }

        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            Updates.Add(instance.Id);
            return Task.CompletedTask;
        }

        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid groupInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("by-workload lookup");

        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid groupInstanceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("sibling listing");

        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("state listing");

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("full listing");

        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("state census");

        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("an edit never creates");

        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid id, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("restore binding");

        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("an edit never deletes");
    }

    private sealed class UnreachableIntentProvider : IActiveWorkloadIntentProvider
    {
        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid groupInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("intent target spec");

        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken)
            => throw new NotSupportedException("intent id lookup");
    }

    private sealed class UnreachableInputsProvider : IWorkloadReconcileInputsProvider
    {
        public Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("reconcile inputs");
    }

    private sealed class UnreachableReconcileNudge : IWorkloadReconcileNudge
    {
        public Task NudgeInstanceAsync(Guid instanceId, string reason, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("reconcile nudge");

        public Task NudgeAllAsync(string reason, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("reconcile nudge");
    }

    private sealed class UnreachableDeviceGroups : IWorkloadDeviceGroupSource
    {
        public Task<IReadOnlyList<int?>> FindDeviceGroupIdsAsync(string devicePath, CancellationToken cancellationToken)
            => throw new NotSupportedException("device group listing");
    }
}
