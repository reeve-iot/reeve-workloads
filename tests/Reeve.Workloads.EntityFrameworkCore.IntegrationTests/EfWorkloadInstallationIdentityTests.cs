using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class EfWorkloadInstallationIdentityTests
{
    private readonly PostgresFixture postgres;

    public EfWorkloadInstallationIdentityTests(PostgresFixture postgres) => this.postgres = postgres;

    [SkippableFact]
    public async Task A_fresh_database_mints_an_id_and_leaves_exactly_one_row()
    {
        var store = await postgres.CreateStoreAsync();

        await using var context = store.NewContext();
        var id = await new EfWorkloadInstallationIdentity(context).GetInstallationIdAsync();

        id.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(id, out _).Should().BeTrue("the label carries a GUID, and hosts parse it back");

        await using var reader = store.NewContext();
        var rows = await reader.WorkloadInstallation.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle()
            .Which.Should().Match<WorkloadInstallationRecord>(
                row => row.Id == WorkloadInstallationRecord.SingletonId && row.InstallationId == id);
    }

    [SkippableFact]
    public async Task A_restart_reads_back_the_SAME_id()
    {
        var store = await postgres.CreateStoreAsync();

        await using var first = store.NewContext();
        var minted = await new EfWorkloadInstallationIdentity(first).GetInstallationIdAsync();

        await using var second = store.NewContext();
        var readBack = await new EfWorkloadInstallationIdentity(second).GetInstallationIdAsync();

        readBack.Should().Be(minted);
    }

    [SkippableFact]
    public async Task A_SECOND_database_mints_a_DIFFERENT_id()
    {
        var first = await postgres.CreateStoreAsync();
        var second = await postgres.CreateStoreAsync();

        await using var firstContext = first.NewContext();
        await using var secondContext = second.NewContext();

        var firstId = await new EfWorkloadInstallationIdentity(firstContext).GetInstallationIdAsync();
        var secondId = await new EfWorkloadInstallationIdentity(secondContext).GetInstallationIdAsync();

        secondId.Should().NotBe(firstId);
    }

    [SkippableFact]
    public async Task A_replica_that_LOSES_the_mint_returns_the_winner_s_id()
    {
        var store = await postgres.CreateStoreAsync();

        await using var winner = store.NewContext();
        await using var transaction = await winner.Database.BeginTransactionAsync();
        winner.WorkloadInstallation.Add(
            WorkloadInstallationRecord.Mint("11111111-2222-3333-4444-555555555555", DateTimeOffset.UtcNow));
        await winner.SaveChangesAsync();

        await using var loser = store.NewContext();
        var mint = Task.Run(() => new EfWorkloadInstallationIdentity(loser).GetInstallationIdAsync());

        (await store.WaitForABlockedWriterAsync(TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the second minter must reach its own insert and block on the singleton key");

        await transaction.CommitAsync();

        (await mint).Should().Be("11111111-2222-3333-4444-555555555555");

        await using var reader = store.NewContext();
        var rows = await reader.WorkloadInstallation.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle()
            .Which.InstallationId.Should().Be("11111111-2222-3333-4444-555555555555");
    }

    [SkippableFact]
    public async Task Two_replicas_minting_at_once_agree_on_ONE_id()
    {
        var store = await postgres.CreateStoreAsync();

        await using var replicaA = store.NewContext();
        await using var replicaB = store.NewContext();

        var results = await Task.WhenAll(
            Task.Run(() => new EfWorkloadInstallationIdentity(replicaA).GetInstallationIdAsync()),
            Task.Run(() => new EfWorkloadInstallationIdentity(replicaB).GetInstallationIdAsync()));

        results[0].Should().Be(results[1], "a replica that lost the insert must return the WINNER's id");

        await using var reader = store.NewContext();
        var rows = await reader.WorkloadInstallation.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle().Which.InstallationId.Should().Be(results[0]);
    }

    [SkippableFact]
    public async Task Repeated_reads_on_ONE_context_never_mint_twice()
    {
        var store = await postgres.CreateStoreAsync();

        await using var context = store.NewContext();
        var identity = new EfWorkloadInstallationIdentity(context);

        var first = await identity.GetInstallationIdAsync();
        var second = await identity.GetInstallationIdAsync();
        var third = await identity.GetInstallationIdAsync();

        new[] { second, third }.Should().AllBe(first);
    }
}
