using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadInstanceRefTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private static WorkloadInstance Instance(
        string workloadId = "border-router",
        WorkloadProviderId? provider = null,
        Guid? groupInstanceId = null)
    {
        var instance = WorkloadInstance.Create(
            Guid.NewGuid(), groupInstanceId ?? Guid.NewGuid(), "acme.thread", workloadId, Now);
        if (provider is { } bound)
        {
            instance.SetProvider(bound, Now);
        }

        return instance;
    }

    [Fact]
    public void A_ref_carries_every_field_the_fleet_pass_uses_and_derives_the_discriminator()
    {
        var groupInstanceId = Guid.NewGuid();
        var instance = Instance("zwave-js", StandInProviders.KubernetesId, groupInstanceId);

        var reference = WorkloadInstanceRef.FromInstance(instance);

        reference.Id.Should().Be(instance.Id);
        reference.GroupInstanceId.Should().Be(groupInstanceId);
        reference.GroupId.Should().Be("acme.thread");
        reference.WorkloadId.Should().Be("zwave-js");
        reference.Provider.Should().Be(StandInProviders.KubernetesId);
        reference.Inst8.Should().Be(instance.Inst8, "the discriminator is derived from the id, never carried");
    }

    [Fact]
    public void An_instance_that_never_deployed_carries_no_provider()
    {
        WorkloadInstanceRef.FromInstance(Instance()).Provider.Should().BeNull();
    }

    [Fact]
    public void Projecting_a_null_instance_is_rejected()
        => ((Action)(() => WorkloadInstanceRef.FromInstance(null!))).Should().Throw<ArgumentNullException>();

    [Fact]
    public void A_bound_ref_addresses_the_same_workload_as_the_entity_it_came_from()
    {
        var instance = Instance("matter-server", StandInProviders.KubernetesId);

        WorkloadRef.FromInstanceRef(WorkloadInstanceRef.FromInstance(instance))
            .Should().Be(WorkloadRef.FromInstance(instance),
                "the fleet status read addresses the same object whether the pass loaded the row or projected it");
    }

    [Fact]
    public void An_unbound_ref_cannot_be_addressed()
        => ((Action)(() => WorkloadRef.FromInstanceRef(WorkloadInstanceRef.FromInstance(Instance()))))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*no bound provider*");

    [Fact]
    public async Task The_default_ref_list_projects_every_row_the_full_list_returns()
    {
        var store = new ListOnlyRepository();
        var a = Instance("a-workload", StandInProviders.KubernetesId);
        var b = Instance("b-workload");
        store.Rows.Add(a);
        store.Rows.Add(b);

        var references = await ((IWorkloadInstanceRepository)store).ListRefsAsync();

        references.Should().Equal(WorkloadInstanceRef.FromInstance(a), WorkloadInstanceRef.FromInstance(b));
        store.ListAllCalls.Should().Be(1, "the default has nothing to project from but the full list");
    }

    [Fact]
    public async Task The_default_ref_list_of_an_empty_store_is_empty()
        => (await ((IWorkloadInstanceRepository)new ListOnlyRepository()).ListRefsAsync()).Should().BeEmpty();

    [Fact]
    public async Task The_default_state_read_answers_with_the_rows_state()
    {
        var store = new ListOnlyRepository();
        var instance = Instance();
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        store.Rows.Add(instance);

        (await ((IWorkloadInstanceRepository)store).TryGetStateAsync(instance.Id)).Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public async Task A_row_resting_in_the_enums_default_state_is_not_reported_as_absent()
    {
        var store = new ListOnlyRepository();
        var instance = Instance();
        store.Rows.Add(instance);

        var state = await ((IWorkloadInstanceRepository)store).TryGetStateAsync(instance.Id);

        state.Should().NotBeNull();
        state.Should().Be(WorkloadInstanceState.PendingConfiguration);
    }

    [Fact]
    public async Task A_row_that_is_not_there_reads_back_as_null()
        => (await ((IWorkloadInstanceRepository)new ListOnlyRepository()).TryGetStateAsync(Guid.NewGuid())).Should().BeNull();

    [Fact]
    public async Task The_default_re_read_answers_with_the_row()
    {
        var store = new ListOnlyRepository();
        var instance = Instance();
        store.Rows.Add(instance);

        (await ((IWorkloadInstanceRepository)store).ReloadAsync(instance.Id)).Should().BeSameAs(instance);
    }

    [Fact]
    public async Task A_re_read_of_a_row_that_is_not_there_answers_null()
        => (await ((IWorkloadInstanceRepository)new ListOnlyRepository()).ReloadAsync(Guid.NewGuid())).Should().BeNull();

    private sealed class ListOnlyRepository : IWorkloadInstanceRepository
    {
        public List<WorkloadInstance> Rows { get; } = [];

        public int ListAllCalls { get; private set; }

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
        {
            ListAllCalls++;
            return Task.FromResult<IReadOnlyList<WorkloadInstance>>([.. Rows]);
        }

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Rows.Find(row => row.Id == id));

        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadInstance?>(null);

        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>([]);

        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>([]);

        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(
                new Dictionary<WorkloadInstanceState, int>());

        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            Rows.Add(instance);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
