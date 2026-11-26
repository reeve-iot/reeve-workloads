using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadInstanceRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;

    public WorkloadInstanceRepositoryTests(PostgresFixture postgres) => this.postgres = postgres;

    private static WorkloadInstance Instance(Guid pluginInstanceId, string workloadId, string pluginTypeId = "matter")
        => WorkloadInstance.Create(Guid.NewGuid(), pluginInstanceId, pluginTypeId, workloadId, Now);

    private async Task<(WorkloadInstanceRepository Sut, IAsyncDisposable Context)> RepositoryAsync()
    {
        var (sut, context) = await RepositoryWithContextAsync();
        return (sut, context);
    }

    private async Task<(WorkloadInstanceRepository Sut, WorkloadsDbContext Context)> RepositoryWithContextAsync()
    {
        var store = await postgres.CreateStoreAsync();
        var context = store.NewContext();
        return (new WorkloadInstanceRepository(context, NullWorkloadStatusRealtimeNotifier.Instance), context);
    }

    [SkippableFact]
    public async Task Add_then_get_by_id_round_trips()
    {
        var (sut, context) = await RepositoryAsync();
        await using var _ = context;

        var instance = Instance(Guid.NewGuid(), "matter-server");
        await sut.AddAsync(instance);

        var loaded = await sut.GetByIdAsync(instance.Id);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(instance.Id);
        loaded.WorkloadId.Should().Be("matter-server");
    }

    [SkippableFact]
    public async Task Updating_a_loaded_instance_that_did_not_change_writes_nothing_and_keeps_its_version()
    {
        var (sut, context) = await RepositoryAsync();
        await using var _ = context;
        var instance = Instance(Guid.NewGuid(), "matter-server");
        await sut.AddAsync(instance);
        var loaded = (await sut.GetByIdAsync(instance.Id))!;
        var version = loaded.Version;

        await sut.UpdateAsync(loaded);

        loaded.Version.Should().Be(version);
        loaded.SetDesiredSpecHash("hash-two", Now.AddMinutes(1));
        await sut.UpdateAsync(loaded);
        loaded.Version.Should().Be(version + 1);
    }

    [SkippableFact]
    public async Task Get_by_id_returns_null_for_missing()
    {
        var (sut, context) = await RepositoryAsync();
        await using var _ = context;

        (await sut.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
    }

    [SkippableFact]
    public async Task Get_by_workload_uses_the_business_key()
    {
        var (sut, context) = await RepositoryAsync();
        await using var _ = context;

        var pluginInstanceId = Guid.NewGuid();
        var target = Instance(pluginInstanceId, "zwave-js");
        await sut.AddAsync(target);
        await sut.AddAsync(Instance(pluginInstanceId, "other-workload"));
        await sut.AddAsync(Instance(Guid.NewGuid(), "zwave-js"));

        var loaded = await sut.GetByWorkloadAsync(pluginInstanceId, "zwave-js");

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(target.Id);
    }

    [SkippableFact]
    public async Task List_by_plugin_instance_returns_only_matching_ordered_by_workload_id()
    {
        var (sut, context) = await RepositoryAsync();
        await using var _ = context;

        var pluginInstanceId = Guid.NewGuid();
        await sut.AddAsync(Instance(pluginInstanceId, "b-workload"));
        await sut.AddAsync(Instance(pluginInstanceId, "a-workload"));
        await sut.AddAsync(Instance(Guid.NewGuid(), "c-workload"));

        var list = await sut.ListByPluginInstanceAsync(pluginInstanceId);

        list.Should().HaveCount(2);
        list.Select(i => i.WorkloadId).Should().ContainInOrder("a-workload", "b-workload");
    }

    [SkippableFact]
    public async Task List_by_state_filters_on_current_state()
    {
        var (sut, context) = await RepositoryAsync();
        await using var _ = context;

        var healthy = Instance(Guid.NewGuid(), "w1");
        healthy.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        healthy.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        await sut.AddAsync(healthy);

        var pending = Instance(Guid.NewGuid(), "w2");
        await sut.AddAsync(pending);

        var healthyList = await sut.ListByStateAsync(WorkloadInstanceState.Healthy);
        var pendingList = await sut.ListByStateAsync(WorkloadInstanceState.PendingConfiguration);

        healthyList.Should().ContainSingle().Which.Id.Should().Be(healthy.Id);
        pendingList.Should().ContainSingle().Which.Id.Should().Be(pending.Id);
    }

    [SkippableFact]
    public async Task List_all_returns_every_instance_for_reconcile()
    {
        var (sut, context) = await RepositoryAsync();
        await using var _ = context;

        await sut.AddAsync(Instance(Guid.NewGuid(), "w1"));
        await sut.AddAsync(Instance(Guid.NewGuid(), "w2"));

        (await sut.ListAllAsync()).Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task Update_persists_a_state_transition()
    {
        var (sut, context) = await RepositoryAsync();
        await using var _ = context;

        var instance = Instance(Guid.NewGuid(), "w1");
        await sut.AddAsync(instance);

        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        await sut.UpdateAsync(instance);

        (await sut.GetByIdAsync(instance.Id))!.State.Should().Be(WorkloadInstanceState.Deploying);
    }

    [SkippableFact]
    public async Task Delete_removes_the_row_the_terminal_teardown_step()
    {
        var (sut, context) = await RepositoryAsync();
        await using var _ = context;

        var instance = Instance(Guid.NewGuid(), "w1");
        await sut.AddAsync(instance);

        await sut.DeleteAsync(instance);

        (await sut.GetByIdAsync(instance.Id)).Should().BeNull();
    }

    [SkippableFact]
    public async Task List_refs_returns_every_instance_with_the_fields_the_pass_addresses_by()
    {
        var (sut, context) = await RepositoryWithContextAsync();
        await using var _ = context;

        var pluginInstanceId = Guid.NewGuid();
        var deployed = Instance(pluginInstanceId, "zwave-js", "acme.zwave");
        deployed.SetProvider(WorkloadProviderId.Parse("kubernetes"), Now);
        await sut.AddAsync(deployed);
        var neverDeployed = Instance(Guid.NewGuid(), "border-router", "acme.thread");
        await sut.AddAsync(neverDeployed);

        var references = await sut.ListRefsAsync();

        references.Should().HaveCount(2);
        var reference = references.Single(r => r.Id == deployed.Id);
        reference.GroupInstanceId.Should().Be(pluginInstanceId);
        reference.GroupId.Should().Be("acme.zwave");
        reference.WorkloadId.Should().Be("zwave-js");
        reference.Provider.Should().Be(WorkloadProviderId.Parse("kubernetes"),
            "the provider crosses the projection through the same value converter the entity does");
        reference.Inst8.Should().Be(deployed.Inst8);

        references.Single(r => r.Id == neverDeployed.Id).Provider.Should().BeNull(
            "an instance that never deployed still has to be enqueued — it just has no runtime object");
    }

    [SkippableFact]
    public async Task List_refs_agrees_with_the_full_list_about_which_rows_exist()
    {
        var (sut, context) = await RepositoryAsync();
        await using var _ = context;

        await sut.AddAsync(Instance(Guid.NewGuid(), "w1"));
        await sut.AddAsync(Instance(Guid.NewGuid(), "w2"));
        await sut.AddAsync(Instance(Guid.NewGuid(), "w3"));

        var all = await sut.ListAllAsync();
        var references = await sut.ListRefsAsync();

        references.Select(r => r.Id).Should().BeEquivalentTo(all.Select(i => i.Id));
    }

    [SkippableFact]
    public async Task List_refs_tracks_nothing()
    {
        var (sut, context) = await RepositoryWithContextAsync();
        await using var _ = context;

        await sut.AddAsync(Instance(Guid.NewGuid(), "w1"));
        context.ChangeTracker.Clear();

        await sut.ListRefsAsync();

        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Try_get_state_reads_the_state_and_tracks_nothing()
    {
        var (sut, context) = await RepositoryWithContextAsync();
        await using var _ = context;

        var instance = Instance(Guid.NewGuid(), "w1");
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        await sut.AddAsync(instance);
        context.ChangeTracker.Clear();

        (await sut.TryGetStateAsync(instance.Id)).Should().Be(WorkloadInstanceState.Deploying);
        context.ChangeTracker.Entries().Should().BeEmpty(
            "the barrier's tick is a pure read — the tracked copy of this row is the one GetByIdAsync loads");
    }

    [SkippableFact]
    public async Task Try_get_state_tells_an_absent_row_apart_from_the_enums_zero_state()
    {
        var (sut, context) = await RepositoryAsync();
        await using var _ = context;

        var pending = Instance(Guid.NewGuid(), "w1");
        await sut.AddAsync(pending);

        (await sut.TryGetStateAsync(pending.Id)).Should().Be(WorkloadInstanceState.PendingConfiguration);
        (await sut.TryGetStateAsync(Guid.NewGuid())).Should().BeNull();
    }

    [SkippableFact]
    public async Task Try_get_state_sees_what_the_last_update_wrote()
    {
        var (sut, context) = await RepositoryWithContextAsync();
        await using var _ = context;

        var instance = Instance(Guid.NewGuid(), "w1");
        await sut.AddAsync(instance);
        (await sut.TryGetStateAsync(instance.Id)).Should().Be(WorkloadInstanceState.PendingConfiguration);

        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        await sut.UpdateAsync(instance);

        (await sut.TryGetStateAsync(instance.Id)).Should().Be(WorkloadInstanceState.Deploying);
    }

    [SkippableFact]
    public async Task The_two_projections_ask_the_server_for_columns_and_never_for_the_documents()
    {
        var store = await postgres.CreateStoreAsync();
        var sql = new StatementRecorder();
        await using var context = store.NewContext(sql);
        var sut = new WorkloadInstanceRepository(context, NullWorkloadStatusRealtimeNotifier.Instance);

        var instance = Instance(Guid.NewGuid(), "w1");
        instance.SetProvider(WorkloadProviderId.Parse("docker"), Now);
        await sut.AddAsync(instance);
        sql.Statements.Clear();

        var documents = new[]
        {
            Column(context, nameof(WorkloadInstance.ParametersDocument)),
            Column(context, nameof(WorkloadInstance.RetainedVolumesSnapshotDocument)),
            Column(context, nameof(WorkloadInstance.AttachmentPinDocument)),
        };

        await sut.ListRefsAsync();
        var refSelect = sql.Statements.Should().ContainSingle().Subject;
        refSelect.Should().ContainAll(
            Column(context, nameof(WorkloadInstance.Id)),
            Column(context, nameof(WorkloadInstance.GroupInstanceId)),
            Column(context, nameof(WorkloadInstance.GroupId)),
            Column(context, nameof(WorkloadInstance.WorkloadId)),
            Column(context, nameof(WorkloadInstance.Provider)));
        refSelect.Should().NotContainAny(documents);

        sql.Statements.Clear();
        await sut.TryGetStateAsync(instance.Id);
        var stateSelect = sql.Statements.Should().ContainSingle().Subject;
        stateSelect.Should().Contain(Column(context, nameof(WorkloadInstance.State)));
        stateSelect.Should().NotContainAny(documents);
    }

    private static string Column(DbContext context, string property)
    {
        var entity = context.Model.FindEntityType(typeof(WorkloadInstance))
            ?? throw new InvalidOperationException("WorkloadInstance is not mapped.");

        return entity.FindProperty(property)?.GetColumnName()
            ?? entity.FindComplexProperty(property)?.ComplexType.GetContainerColumnName()
            ?? throw new InvalidOperationException($"'{property}' is neither a property nor a complex property.");
    }

    private sealed class StatementRecorder : DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Statements.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Statements.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
