using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadUpdateIntentDocumentPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;

    public WorkloadUpdateIntentDocumentPersistenceTests(PostgresFixture postgres) => this.postgres = postgres;

    [SkippableFact]
    public async Task An_intent_and_its_step_read_back_every_document_they_wrote()
    {
        var store = await postgres.CreateStoreAsync();
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "1.0.0", "2.0.0", Now);
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                TargetSpecs = Specs(),
                PreviousSpecs = new Dictionary<string, WorkloadDesiredSpec> { ["matter"] = Bare("matter") },
                PreviousParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
                {
                    ["zigbee"] = new Dictionary<string, string?> { ["port"] = "5540", ["token"] = null },
                    ["matter"] = new Dictionary<string, string?> { ["port"] = "5541" },
                },
                FromDigests = new Dictionary<string, string?>(StringComparer.Ordinal) { ["zigbee"] = "sha256:from" },
                ToDigests = new Dictionary<string, string?>(StringComparer.Ordinal) { ["zigbee"] = "sha256:to" },
                OperatorParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["port"] = "5541" },
                AdoptedResourceWorkloadIds = ["zigbee"],
            },
            Now);
        intent.Fail("the gate failed", Now);
        intent.CaptureEvidence(
            new WorkloadUpdateEvidence("2.0.0", Now, [new WorkloadEvidenceCapture("zigbee", ["boom"], ["BackOff"], null)]));
        intent.RecordStageJournal(new Reeve.Pipeline.PipelineJournal(
            [
                new Reeve.Pipeline.StageEntry("stage.params", Now, Reeve.Pipeline.PipelineDirection.Forward,
                    Reeve.Pipeline.StageEntryState.Done, null),
                new Reeve.Pipeline.StageEntry("stage.sidecar", Now.AddSeconds(3), Reeve.Pipeline.PipelineDirection.Compensating,
                    Reeve.Pipeline.StageEntryState.Working, "restoring 'zigbee'"),
            ],
            Reeve.Pipeline.PipelineDirection.Compensating,
            Reeve.Pipeline.PipelineState.Running));
        var step = WorkloadUpdateIntentStep.Create(Guid.NewGuid(), intent.Id, "zigbee", Now);
        step.SetGateClockState(
            new WorkloadGateClockState
            {
                ClockStartedAtUtc = Now,
                AccumulatedPause = TimeSpan.FromSeconds(95),
                GraceConsumed = true,
            },
            Now);
        intent.AddStep(step);

        await using (var writer = store.NewContext())
        {
            writer.WorkloadUpdateIntents.Add(intent);
            await writer.SaveChangesAsync();
        }

        await using var reader = store.NewContext();
        var loaded = await reader.WorkloadUpdateIntents.Include(row => row.Steps).SingleAsync(row => row.Id == intent.Id);

        loaded.TargetSpecsDocument.Should().BeEquivalentTo(intent.TargetSpecsDocument, options => options.WithStrictOrdering());
        loaded.PreviousSpecsDocument.Should().BeEquivalentTo(intent.PreviousSpecsDocument);
        loaded.PreviousParametersDocument.Should().BeEquivalentTo(intent.PreviousParametersDocument);
        loaded.OperatorParametersDocument.Should().BeEquivalentTo(intent.OperatorParametersDocument);
        loaded.AdoptedResourceWorkloadIds.Should().Equal("zigbee");
        loaded.FromDigestsDocument.Should().BeEquivalentTo(intent.FromDigestsDocument);
        loaded.ToDigestsDocument.Should().BeEquivalentTo(intent.ToDigestsDocument);
        loaded.EvidenceDocument.Should().BeEquivalentTo(intent.EvidenceDocument);
        loaded.StageJournalDocument.Should().BeEquivalentTo(intent.StageJournalDocument, options => options.WithStrictOrdering());
        loaded.ReadStageJournal().Value.Should().BeEquivalentTo(intent.ReadStageJournal().Value, options => options.WithStrictOrdering());
        loaded.Steps.Single().GateClockStateDocument.Should().BeEquivalentTo(step.GateClockStateDocument);

        var zigbee = loaded.TargetSpecsDocument!.Workloads.Single(workload => workload.Key == "zigbee");
        zigbee.Network!.Attachments.Should().Equal("iot", "cam");
        zigbee.ScanReadyNodeNames.Should().NotBeNull().And.BeEmpty();
        zigbee.Observability!.Metrics!.Port.Should().Be(9090);
        var matter = loaded.TargetSpecsDocument.Workloads.Single(workload => workload.Key == "matter");
        matter.ScanReadyNodeNames.Should().BeNull();
        matter.Observability.Should().BeNull();
        matter.Health.Http.Should().BeNull();
    }

    [SkippableFact]
    public async Task Settling_an_intent_writes_the_dropped_documents_empty()
    {
        var store = await postgres.CreateStoreAsync();
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "1.0.0", "2.0.0", Now);
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                PreviousParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
                {
                    ["zigbee"] = new Dictionary<string, string?> { ["port"] = "5540" },
                },
                OperatorParameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["port"] = "5541" },
                AdoptedResourceWorkloadIds = ["zigbee"],
            },
            Now);

        await using (var writer = store.NewContext())
        {
            writer.WorkloadUpdateIntents.Add(intent);
            await writer.SaveChangesAsync();
            intent.Fail("the gate failed", Now);
            intent.MarkRollbackFailed("the old image is gone", Now);
            await writer.SaveChangesAsync();
        }

        await using var reader = store.NewContext();
        var loaded = await reader.WorkloadUpdateIntents.SingleAsync(row => row.Id == intent.Id);

        loaded.PreviousParametersDocument!.Workloads.Should().BeEmpty();
        loaded.OperatorParametersDocument!.Entries.Should().BeEmpty();
        loaded.AdoptedResourceWorkloadIds.Should().BeNull();
    }

    private static Dictionary<string, WorkloadDesiredSpec> Specs() => new(StringComparer.Ordinal)
    {
        ["zigbee"] = Bare("zigbee") with
        {
            Labels = new Dictionary<string, string> { ["b.io/y"] = "2", ["a.io/x"] = "1" },
            Platforms = ["linux/arm64", "linux/amd64"],
            Network = new ResolvedNetwork
            {
                Mode = WorkloadNetworkMode.OperatorNetwork,
                Attachments = ["iot", "cam"],
                Ports = [new ResolvedPort { Name = "http", Port = 8080, Protocol = "TCP", HostPort = 18080 }],
            },
            Privileges = new ResolvedPrivileges
            {
                DeviceGroupIds = [20, 5],
                HostPaths = [new ResolvedHostPath { HostPath = "/sys/class", MountPath = "/host/sys" }],
                NodeLocalState = new ResolvedNodeLocalState { HostPath = "/var/lib/reeve/zigbee", Criticality = VolumeCriticality.Critical },
            },
            Volumes =
            [
                new ResolvedVolume
                {
                    Id = "state",
                    MountPath = "/data",
                    Size = "1Gi",
                    Criticality = VolumeCriticality.Recoverable,
                    ResourceName = "reeve-zigbee-state",
                },
            ],
            Health = new WorkloadHealth
            {
                Mode = WorkloadHealthMode.Container,
                Http = new WorkloadHttpProbe { Port = 8080, LivenessPath = "/live" },
            },
            Observability = new WorkloadObservability { Metrics = new WorkloadMetrics { Port = 9090, Path = "/metrics" } },
            Env = [new ResolvedEnvVar { Name = "TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "token" }],
            HostRequirements = [new HostRequirement { Kind = HostRequirementKind.DbusService, Name = "bluetoothd" }],
            UnsupportedPodPatch = new UnsupportedPodPatchSpec { PatchJson = "[{\"op\":\"add\",\"path\":\"/x\",\"value\":1}]" },
            ScanReadyNodeNames = [],
            ScanIneligibleNodeNames = ["node-c"],
        },
        ["matter"] = Bare("matter"),
    };

    private static WorkloadDesiredSpec Bare(string workloadId) => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "reeve",
        WorkloadId = workloadId,
        InstanceId = Guid.Parse("12345678-0000-0000-0000-000000000000"),
        Inst8 = "12345678",
        Provider = StandInProviders.KubernetesId,
        ResourceName = $"reeve-{workloadId}",
        SecretName = $"reeve-{workloadId}-secret",
        Labels = new Dictionary<string, string>(),
        Image = "ghcr.io/reeve/" + workloadId + "@sha256:" + new string('a', 64),
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "0123abcd",
    };
}
