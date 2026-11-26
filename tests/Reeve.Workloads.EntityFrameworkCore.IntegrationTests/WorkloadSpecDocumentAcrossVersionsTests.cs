using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Tests.Shared;
using CredentialFree = Reeve.Workloads.Tests.Shared.CredentialFree;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadSpecDocumentAcrossVersionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;

    public WorkloadSpecDocumentAcrossVersionsTests(PostgresFixture postgres) => this.postgres = postgres;

    [SkippableFact]
    public async Task A_document_without_credential_fields_reads_back_with_no_credentials()
    {
        var store = await postgres.CreateStoreAsync();
        var intent = await WriteAsync(store);

        await using (var seeder = store.NewContext())
        {
            await seeder.Database.ExecuteSqlAsync($"""
                UPDATE workloads.workload_update_intents
                SET target_specs_document = jsonb_set(target_specs_document, ARRAY['Workloads'], (
                    SELECT jsonb_agg(
                        (workload.value - 'ProvisionedCredentials')
                        || jsonb_build_object('Env', (
                            SELECT coalesce(jsonb_agg(env.value - 'CredentialId' ORDER BY env.ordinality), '[]'::jsonb)
                            FROM jsonb_array_elements(workload.value -> 'Env') WITH ORDINALITY AS env(value, ordinality)))
                        ORDER BY workload.ordinality)
                    FROM jsonb_array_elements(target_specs_document -> 'Workloads') WITH ORDINALITY AS workload(value, ordinality)))
                WHERE id = {intent.Id}
                """);
            var stored = await seeder.Database
                .SqlQuery<string>($"SELECT target_specs_document::text AS \"Value\" FROM workloads.workload_update_intents WHERE id = {intent.Id}")
                .SingleAsync();
            stored.Should().NotContain("CredentialId").And.NotContain("ProvisionedCredentials").And.Contain("REEVE_WORKLOAD_TOKEN");
        }

        await using var reader = store.NewContext();
        var loaded = await reader.WorkloadUpdateIntents.SingleAsync(row => row.Id == intent.Id);
        var spec = loaded.ReadTargetSpecs().Value!["gateway"];

        loaded.TargetSpecsDocument!.Workloads.Single().ProvisionedCredentials.Should().BeNull();
        spec.ProvisionedCredentials.Should().BeEmpty();
        spec.Env.Select(env => (env.Name, env.CredentialId)).Should().Equal(("REEVE_WORKLOAD_TOKEN", null), ("LOG_LEVEL", null));
    }

    [SkippableFact]
    public async Task A_document_with_credentials_reads_back_with_its_credentials()
    {
        var store = await postgres.CreateStoreAsync();
        var intent = await WriteAsync(store);

        await using var reader = store.NewContext();
        var loaded = await reader.WorkloadUpdateIntents.SingleAsync(row => row.Id == intent.Id);
        var spec = loaded.ReadTargetSpecs().Value!["gateway"];

        spec.ProvisionedCredentials.Should().Equal(CredentialSpecs.Provisioned);
        spec.Env.Select(env => env.CredentialId).Should().Equal("workload-token", null);
    }

    [SkippableFact]
    public async Task A_document_with_credentials_reads_back_through_a_credential_free_shape()
    {
        var store = await postgres.CreateStoreAsync();
        var intent = await WriteAsync(store);

        await using var credentialFree = new CredentialFreeWorkloadsDbContext(
            new DbContextOptionsBuilder<CredentialFreeWorkloadsDbContext>().UseNpgsql(store.ConnectionString).Options);
        var loaded = await credentialFree.Intents.SingleAsync(row => row.Id == intent.Id);

        loaded.TargetSpecsDocument.Should().BeEquivalentTo(
            intent.TargetSpecsDocument, options => options.ExcludingMissingMembers().WithStrictOrdering());
        loaded.TargetSpecsDocument!.Workloads.Single().Env.Select(env => (env.Name, env.SecretRef))
            .Should().Equal(("REEVE_WORKLOAD_TOKEN", "reeve-gateway-secret"), ("LOG_LEVEL", null));
    }

    private static async Task<WorkloadUpdateIntent> WriteAsync(WorkloadsStore store)
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "1.0.0", "2.0.0", Now);
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                TargetSpecs = CredentialSpecs.Map(),
            },
            Now);
        intent.TargetSpecsDocument!.Workloads.Single().ProvisionedCredentials.Should().HaveCount(2);

        await using var writer = store.NewContext();
        writer.WorkloadUpdateIntents.Add(intent);
        await writer.SaveChangesAsync();
        return intent;
    }
}

internal sealed class CredentialFreeIntentRow
{
    public Guid Id { get; set; }

    public CredentialFree.WorkloadSpecMapDocument? TargetSpecsDocument { get; set; }
}

internal sealed class CredentialFreeWorkloadsDbContext(DbContextOptions<CredentialFreeWorkloadsDbContext> options) : DbContext(options)
{
    public DbSet<CredentialFreeIntentRow> Intents => Set<CredentialFreeIntentRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<CredentialFreeIntentRow>(builder =>
        {
            builder.ToTable("workload_update_intents", "workloads");
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Id).HasColumnName("id");
            builder.ComplexProperty(row => row.TargetSpecsDocument, document =>
            {
                document.ToJson("target_specs_document");
                document.ComplexCollection(map => map.Workloads, spec =>
                {
                    spec.ComplexCollection(workload => workload.Labels);
                    spec.ComplexCollection(workload => workload.NodeSelector);
                    spec.ComplexProperty(workload => workload.Resources);
                    spec.ComplexProperty(workload => workload.Network, network => network.ComplexCollection(n => n.Ports));
                    spec.ComplexProperty(workload => workload.Privileges, privileges =>
                    {
                        privileges.ComplexCollection(p => p.HostPaths);
                        privileges.ComplexProperty(p => p.NodeLocalState);
                    });
                    spec.ComplexCollection(workload => workload.Volumes);
                    spec.ComplexCollection(workload => workload.ConfigMapMounts);
                    spec.ComplexProperty(workload => workload.Health, health => health.ComplexProperty(h => h.Http));
                    spec.ComplexProperty(workload => workload.Observability, observability => observability.ComplexProperty(o => o.Metrics));
                    spec.ComplexCollection(workload => workload.Env);
                    spec.ComplexCollection(workload => workload.HostRequirements);
                });
            });
        });
}
