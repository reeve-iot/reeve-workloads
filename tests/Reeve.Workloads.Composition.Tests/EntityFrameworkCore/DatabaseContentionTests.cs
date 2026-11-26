using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.Composition.Tests.EntityFrameworkCore;

public sealed class DatabaseContentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private const string Ef10SaveChanges = "ef10-save";
    private const string Ef11SaveChanges = "ef11-save";
    private const string Ef10Query = "ef10-query";
    private const string Ef11Query = "ef11-query";

    public static TheoryData<string, string> EveryShape()
    {
        var data = new TheoryData<string, string>();
        foreach (var shape in new[] { Ef10SaveChanges, Ef11SaveChanges, Ef10Query, Ef11Query })
        {
            data.Add(shape, PostgresErrorCodes.SerializationFailure);
            data.Add(shape, PostgresErrorCodes.DeadlockDetected);
        }

        return data;
    }

    public static TheoryData<string, string> EverySaveShape()
    {
        var data = new TheoryData<string, string>();
        foreach (var shape in new[] { Ef10SaveChanges, Ef11SaveChanges })
        {
            data.Add(shape, PostgresErrorCodes.SerializationFailure);
            data.Add(shape, PostgresErrorCodes.DeadlockDetected);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void A_serialization_failure_or_deadlock_is_recognised_whatever_wraps_it(string shape, string sqlState)
    {
        DatabaseContention.IsSerializationFailureOrDeadlock(Contention(shape, sqlState)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void A_serialization_failure_or_deadlock_is_never_a_lost_optimistic_race(string shape, string sqlState)
    {
        DatabaseContention.IsLostOptimisticRace(Contention(shape, sqlState)).Should().BeFalse();
    }

    [Fact]
    public void A_concurrency_token_mismatch_is_a_lost_race_and_not_contention()
    {
        var lostRace = LostRace();

        DatabaseContention.IsLostOptimisticRace(lostRace).Should().BeTrue();
        DatabaseContention.IsSerializationFailureOrDeadlock(lostRace).Should().BeFalse();
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation)]
    [InlineData(PostgresErrorCodes.LockNotAvailable)]
    [InlineData(PostgresErrorCodes.QueryCanceled)]
    public void Other_database_errors_are_not_contention(string sqlState)
    {
        var failure = new DbUpdateException("save", new PostgresException("other", "ERROR", "ERROR", sqlState));

        DatabaseContention.IsSerializationFailureOrDeadlock(failure).Should().BeFalse();
        DatabaseContention.IsLostOptimisticRace(failure).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(EverySaveShape))]
    public async Task A_group_update_lets_contention_through_untranslated(string shape, string sqlState)
    {
        var fault = Contention(shape, sqlState);
        var (repository, row) = await GroupAsync(fault);

        var thrown = await FluentActions.Awaiting(() => repository.UpdateAsync(row)).Should().ThrowAsync<Exception>();

        thrown.Which.Should().BeSameAs(fault,
            "a deadlock is not someone else's write, so callers must not re-read and carry on as if they had lost a race");
    }

    [Fact]
    public async Task A_group_update_that_loses_its_token_is_a_conflict()
    {
        var (repository, row) = await GroupAsync(LostRace());

        var thrown = await FluentActions.Awaiting(() => repository.UpdateAsync(row))
            .Should().ThrowAsync<WorkloadConcurrencyConflictException>();

        thrown.Which.EntityId.Should().Be(row.Id);
    }

    [Theory]
    [MemberData(nameof(EverySaveShape))]
    public async Task An_instance_update_lets_contention_through_untranslated(string shape, string sqlState)
    {
        var fault = Contention(shape, sqlState);
        var (repository, instance) = await InstanceAsync(fault);

        var thrown = await FluentActions.Awaiting(() => repository.UpdateAsync(instance)).Should().ThrowAsync<Exception>();

        thrown.Which.Should().BeSameAs(fault,
            "a deadlock is not someone else's write, so callers must not re-read and carry on as if they had lost a race");
    }

    [Fact]
    public async Task An_instance_update_that_loses_its_token_is_a_conflict()
    {
        var (repository, instance) = await InstanceAsync(LostRace());

        var thrown = await FluentActions.Awaiting(() => repository.UpdateAsync(instance))
            .Should().ThrowAsync<WorkloadConcurrencyConflictException>();

        thrown.Which.EntityId.Should().Be(instance.Id);
    }

    private static Exception Contention(string shape, string sqlState)
    {
        var postgres = new PostgresException("could not serialize access", "ERROR", "ERROR", sqlState);
        return shape switch
        {
            Ef10SaveChanges => new InvalidOperationException(
                "An exception has been raised that is likely due to a transient failure.",
                new DbUpdateException("An error occurred while saving the entity changes.", postgres)),
            Ef11SaveChanges => new DbUpdateConcurrencyException("An error occurred while saving the entity changes.", postgres),
            Ef10Query => new InvalidOperationException(
                "An exception has been raised that is likely due to a transient failure.", postgres),
            Ef11Query => postgres,
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };
    }

    private static DbUpdateConcurrencyException LostRace() =>
        new("The database operation was expected to affect 1 row(s), but actually affected 0 row(s).");

    private static async Task<(WorkloadGroupRepository Repository, WorkloadGroup Row)> GroupAsync(Exception fault)
    {
        var faults = new FaultingSaveInterceptor();
        var db = NewContext(faults);
        var repository = new WorkloadGroupRepository(db);
        await repository.TryAddAsync(WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.2.0", Now));
        var row = (await repository.GetAsync("zigbee2mqtt"))!;
        row.MoveTo("1.3.0", Now.AddMinutes(1));
        faults.Fault = fault;
        return (repository, row);
    }

    private static async Task<(WorkloadInstanceRepository Repository, WorkloadInstance Instance)> InstanceAsync(Exception fault)
    {
        var faults = new FaultingSaveInterceptor();
        var db = NewContext(faults);
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "gateway", Now);
        db.Set<WorkloadInstance>().Add(instance);
        await db.SaveChangesAsync();
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, Now);
        faults.Fault = fault;
        return (new WorkloadInstanceRepository(db, new NullWorkloadStatusRealtimeNotifier()), instance);
    }

    private static WorkloadsDbContext NewContext(FaultingSaveInterceptor faults)
    {
        var builder = new DbContextOptionsBuilder<WorkloadsDbContext>();
        builder.UseInMemoryDatabase(Guid.NewGuid().ToString("n")).AddInterceptors(faults);
        return new WorkloadsDbContext(builder.Options, new WorkloadsStoreOptions());
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
