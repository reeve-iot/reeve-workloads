using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Composition.Tests.EntityFrameworkCore;

public sealed class WorkloadsAgnosticWriteTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task A_resolved_parameter_write_lands_on_a_store_without_json()
    {
        await using var db = await SeededAsync("gateway");

        var written = await new WorkloadResolvedParameterWriter(db).SetResolvedParametersAsync(
            GroupInstanceId,
            new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
            {
                ["gateway"] = new Dictionary<string, string?>(StringComparer.Ordinal) { ["serialPort"] = "/dev/ttyUSB0" },
            },
            Now);

        written.Should().BeGreaterThan(0);
        var reloaded = await db.Set<WorkloadInstance>().FirstAsync(instance => instance.WorkloadId == "gateway");
        reloaded.GetResolvedParameters()["serialPort"].Should().Be("/dev/ttyUSB0");
    }

    [Fact]
    public async Task A_document_survives_a_round_trip_through_a_store_without_json()
    {
        await using var db = await SeededAsync("gateway");
        var instance = await db.Set<WorkloadInstance>().FirstAsync();
        instance.SnapshotRetainedVolumes(
            [
                new RetainedVolumeSnapshotItem
                {
                    VolumeId = "state",
                    ResourceName = "reeve-gateway-state",
                    Size = "1Gi",
                    Criticality = VolumeCriticality.Recoverable,
                },
            ],
            Now);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reloaded = await db.Set<WorkloadInstance>().FirstAsync();

        reloaded.GetRetainedVolumesSnapshot().Should().ContainSingle()
            .Which.ResourceName.Should().Be("reeve-gateway-state");
    }

    [Fact]
    public async Task An_attachment_pin_survives_a_round_trip_in_declared_order_through_a_store_without_json()
    {
        await using var db = await SeededAsync("gateway");
        var instance = await db.Set<WorkloadInstance>().FirstAsync();
        instance.PinAttachment(
            new Reeve.Workloads.Abstractions.Domain.Network.WorkloadAttachmentPin
            {
                Mode = Reeve.Workloads.Abstractions.Contracts.WorkloadNetworkMode.OperatorNetwork,
                AttachmentNames = ["iot", "cam"],
            },
            Now);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var pin = (await db.Set<WorkloadInstance>().FirstAsync()).ReadAttachmentPin().Value;

        pin!.AttachmentNames.Should().Equal("iot", "cam");
        pin.Mode.Should().Be(Reeve.Workloads.Abstractions.Contracts.WorkloadNetworkMode.OperatorNetwork);
    }

    [Fact]
    public async Task An_intent_spec_carrying_credentials_is_written_and_queried_on_a_store_without_json()
    {
        await using var db = await SeededAsync("gateway");
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), GroupInstanceId, "1.0.0", "2.0.0", Now);
        intent.StageSnapshots(new WorkloadUpdateSnapshots { TargetSpecs = CredentialSpecs.Map() }, Now);
        db.Set<WorkloadUpdateIntent>().Add(intent);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reloaded = await db.Set<WorkloadUpdateIntent>().SingleAsync(row => row.GroupInstanceId == GroupInstanceId);
        var spec = reloaded.ReadTargetSpecs().Value!["gateway"];

        spec.ProvisionedCredentials.Should().Equal(CredentialSpecs.Provisioned);
        spec.Env.Select(env => env.CredentialId).Should().Equal("workload-token", null);
    }

    [Fact]
    public async Task A_cohort_intent_is_written_and_queried_on_a_store_without_json()
    {
        await using var db = await SeededAsync("gateway");
        var cohortId = Guid.NewGuid();
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), GroupInstanceId, "1.0.0", "2.0.0", Now,
            new WorkloadUpdateCohortPlace(cohortId, 0, 1));
        db.Set<WorkloadUpdateIntent>().Add(intent);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reloaded = await db.Set<WorkloadUpdateIntent>().SingleAsync(row => row.CohortId == cohortId);

        reloaded.CohortOrdinal.Should().Be(0);
        reloaded.CohortSize.Should().Be(1);
        reloaded.CohortId.Should().Be(cohortId);
    }

    [Fact]
    public async Task An_unwritten_document_reads_back_as_absent()
    {
        await using var db = await SeededAsync("gateway");
        db.ChangeTracker.Clear();

        var reloaded = await db.Set<WorkloadInstance>().FirstAsync();

        reloaded.RetainedVolumesSnapshotDocument.Should().BeNull();
        reloaded.GetRetainedVolumesSnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task A_renamed_state_reason_is_written_and_queried_on_a_store_without_json()
    {
        await using var db = await SeededAsync("gateway");
        var row = await db.Set<WorkloadInstance>().SingleAsync();
        row.TransitionTo(row.State, WorkloadStateReason.RunningVersionBehind, "the running version is behind", Now);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reloaded = await db.Set<WorkloadInstance>()
            .SingleAsync(instance => instance.StateReason == WorkloadStateReason.RunningVersionBehind);

        reloaded.StateReason.Should().Be(WorkloadStateReason.RunningVersionBehind);
        reloaded.StateMessage.Should().Be("the running version is behind");
    }

    private static async Task<WorkloadsDbContext> SeededAsync(string workloadId)
    {
        var builder = new DbContextOptionsBuilder<WorkloadsDbContext>();
        builder.UseInMemoryDatabase(Guid.NewGuid().ToString("n"));
        var db = new WorkloadsDbContext(builder.Options, new WorkloadsStoreOptions());
        db.Set<WorkloadInstance>().Add(
            WorkloadInstance.Create(Guid.NewGuid(), GroupInstanceId, "zwave", workloadId, Now));
        await db.SaveChangesAsync();
        return db;
    }
}
