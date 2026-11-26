using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadSpecUserNamespaceDocumentTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public async Task The_flag_round_trips_through_the_document()
    {
        var store = await postgres.CreateStoreAsync();
        var intent = await WriteAsync(store);
        await using var reader = store.NewContext();
        var loaded = await reader.WorkloadUpdateIntents.SingleAsync(row => row.Id == intent.Id);
        loaded.ReadTargetSpecs().Value!["gateway"].UserNamespace.Should().BeTrue();
    }

    [SkippableFact]
    public async Task A_document_without_the_keys_reads_back_false()
    {
        var store = await postgres.CreateStoreAsync();
        var intent = await WriteAsync(store);
        await using (var seeder = store.NewContext())
        {
            await seeder.Database.ExecuteSqlAsync($"""
                UPDATE workloads.workload_update_intents
                SET target_specs_document = jsonb_set(target_specs_document, ARRAY['Workloads'], (
                    SELECT jsonb_agg(
                        (workload.value - 'UserNamespace' - 'UserNamespaceSkippedForHostNetwork')
                        ORDER BY workload.ordinality)
                    FROM jsonb_array_elements(target_specs_document -> 'Workloads') WITH ORDINALITY AS workload(value, ordinality)))
                WHERE id = {intent.Id}
                """);
        }
        await using var reader = store.NewContext();
        var loaded = await reader.WorkloadUpdateIntents.SingleAsync(row => row.Id == intent.Id);
        var spec = loaded.ReadTargetSpecs().Value!["gateway"];
        spec.UserNamespace.Should().BeFalse();
        spec.UserNamespaceSkippedForHostNetwork.Should().BeFalse();
    }

    [SkippableFact]
    public async Task A_document_with_the_flag_reads_through_a_credential_free_shape()
    {
        var store = await postgres.CreateStoreAsync();
        var intent = await WriteAsync(store);
        await using var credentialFree = new CredentialFreeWorkloadsDbContext(
            new DbContextOptionsBuilder<CredentialFreeWorkloadsDbContext>().UseNpgsql(store.ConnectionString).Options);
        (await credentialFree.Intents.SingleAsync(row => row.Id == intent.Id)).TargetSpecsDocument!.Workloads.Should().ContainSingle();
    }

    private static async Task<WorkloadUpdateIntent> WriteAsync(WorkloadsStore store)
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "1.0.0", "2.0.0", Now);
        intent.StageSnapshots(new WorkloadUpdateSnapshots
        {
            TargetSpecs = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal)
            {
                ["gateway"] = CredentialSpecs.Spec() with { UserNamespace = true },
            },
        }, Now);
        await using var writer = store.NewContext();
        writer.WorkloadUpdateIntents.Add(intent);
        await writer.SaveChangesAsync();
        return intent;
    }
}
