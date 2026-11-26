using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.Composition.Tests.EntityFrameworkCore;

public sealed class RetainedVolumeRepositoryFailedAddTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_failed_add_leaves_nothing_behind_for_the_next_save_of_the_same_context()
    {
        var faults = new FaultingSaveInterceptor();
        var builder = new DbContextOptionsBuilder<WorkloadsDbContext>();
        builder.UseInMemoryDatabase(Guid.NewGuid().ToString("n")).AddInterceptors(faults);
        await using var db = new WorkloadsDbContext(builder.Options, new WorkloadsStoreOptions());
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "gateway", Now);
        db.Set<WorkloadInstance>().Add(instance);
        await db.SaveChangesAsync();
        var volumes = new RetainedVolumeRepository(db);
        var instances = new WorkloadInstanceRepository(db, new NullWorkloadStatusRealtimeNotifier());

        faults.Fault = new DbUpdateException("insert failed");
        var add = () => volumes.AddAsync(RetainedVolume.Record(
            Guid.NewGuid(), "zwave", "gateway", "state", "zwave-state", "1Gi", VolumeCriticality.Critical, null,
            WorkloadProviderId.Parse("any"), RetainedVolumeSchemeId.Kubernetes, instance.GroupInstanceId, instance.Id, Now));
        await add.Should().ThrowAsync<DbUpdateException>();

        faults.Fault = null;
        instance.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, "Teardown could not record its retained volumes.", Now);
        await instances.UpdateAsync(instance);

        (await volumes.ListAllAsync()).Should().BeEmpty("the failed insert must not be replayed by the next save");
        db.ChangeTracker.Entries<RetainedVolume>().Should().BeEmpty();
    }

    private sealed class FaultingSaveInterceptor : SaveChangesInterceptor
    {
        public Exception? Fault { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Fault is null
                ? ValueTask.FromResult(result)
                : ValueTask.FromException<InterceptionResult<int>>(Fault);
    }
}
