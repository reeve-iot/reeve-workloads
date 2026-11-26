using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadPinDocumentAcrossVersionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;

    public WorkloadPinDocumentAcrossVersionsTests(PostgresFixture postgres) => this.postgres = postgres;

    private static WorkloadAttachmentPin Pin() => new()
    {
        Mode = WorkloadNetworkMode.OperatorNetwork,
        AttachmentNames = ["lan", "iot"],
    };

    private static Task<bool> HasConfigDigestKeyAsync(WorkloadsDbContext context, Guid id)
        => context.Database
            .SqlQuery<bool>($"SELECT jsonb_exists(attachment_pin_document, 'ConfigDigest') AS \"Value\" FROM workloads.workload_instances WHERE id = {id}")
            .SingleAsync();

    [SkippableFact]
    public async Task A_pin_document_18_0_0_wrote_with_a_ConfigDigest_key_reads_back_and_is_rewritten_without_it()
    {
        var store = await postgres.CreateStoreAsync();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "matter", "matter-server", Now);
        instance.PinAttachment(Pin(), Now);
        await using (var writer = store.NewContext())
        {
            writer.WorkloadInstances.Add(instance);
            await writer.SaveChangesAsync();
        }

        await using (var seeder = store.NewContext())
        {
            (await HasConfigDigestKeyAsync(seeder, instance.Id)).Should().BeFalse();
            await seeder.Database.ExecuteSqlAsync(
                $"UPDATE workloads.workload_instances SET attachment_pin_document = attachment_pin_document || '{{\"ConfigDigest\":\"sha256:written-by-18\"}}'::jsonb WHERE id = {instance.Id}");
            (await HasConfigDigestKeyAsync(seeder, instance.Id)).Should().BeTrue("the stored spelling is ConfigDigest");
        }

        await using (var reader = store.NewContext())
        {
            var loaded = await reader.WorkloadInstances.SingleAsync(row => row.Id == instance.Id);
            var read = loaded.ReadAttachmentPin();

            read.Verdict.Should().Be(WorkloadDocumentVerdict.Value);
            read.Value!.Mode.Should().Be(WorkloadNetworkMode.OperatorNetwork);
            read.Value.AttachmentNames.Should().Equal("lan", "iot");

            loaded.PinAttachment(Pin() with { AttachmentNames = ["lan"] }, Now.AddMinutes(1));
            await reader.SaveChangesAsync();
        }

        await using var check = store.NewContext();
        (await HasConfigDigestKeyAsync(check, instance.Id)).Should().BeFalse("the rewrite drops the key from the document");
        var stored = await check.WorkloadInstances.AsNoTracking().SingleAsync(row => row.Id == instance.Id);
        stored.ReadAttachmentPin().Value!.AttachmentNames.Should().Equal("lan");
    }
}
