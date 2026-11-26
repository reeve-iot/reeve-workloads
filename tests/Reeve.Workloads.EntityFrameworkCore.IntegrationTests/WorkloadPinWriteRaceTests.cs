using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class WorkloadPinWriteRaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;

    public WorkloadPinWriteRaceTests(PostgresFixture postgres) => this.postgres = postgres;

    private static WorkloadAttachmentPin Pin() => new()
    {
        Mode = WorkloadNetworkMode.OperatorNetwork,
        AttachmentNames = ["lan"],
    };

    private async Task<(WorkloadUpdateInitiationResult Result, Dictionary<string, WorkloadInstance> Stored)> InitiateAsync(
        string[] workloads, string[] rewritten)
    {
        var store = await postgres.CreateStoreAsync();
        var group = Guid.NewGuid();
        await using (var writer = store.NewContext())
        {
            foreach (var workload in workloads)
            {
                writer.WorkloadInstances.Add(WorkloadInstance.Create(Guid.NewGuid(), group, "matter", workload, Now));
            }

            await writer.SaveChangesAsync();
        }

        await using var context = store.NewContext();
        var repository = new WorkloadInstanceRepository(context, NullWorkloadStatusRealtimeNotifier.Instance);
        var initiator = new WorkloadUpdateInitiator(
            new WorkloadUpdateIntentRepository(context),
            new RacingPlanner(group, workloads, repository, store, rewritten),
            repository,
            new NoNudge(),
            Pipeline<WorkloadUpdateStageContext>.Resolve([new OnlyStage()]),
            new FixedClock(),
            NullLogger<WorkloadUpdateInitiator>.Instance);

        var result = await initiator.InitiateAsync(
            new WorkloadUpdateInitiationRequest
            {
                PluginInstanceId = group,
                CandidateVersion = "2.6.0",
                Precondition = new WorkloadWritePrecondition
                {
                    RequirementsFingerprint = RacingPlanner.Fingerprint(group, workloads),
                    AttachmentPins = workloads.ToDictionary(
                        workload => workload, workload => Pin(), StringComparer.Ordinal),
                },
            });

        await using var check = store.NewContext();
        return (result, await check.WorkloadInstances.AsNoTracking().ToDictionaryAsync(row => row.WorkloadId));
    }

    private static void AssertEveryPinLanded(Dictionary<string, WorkloadInstance> stored, string[] workloads, string[] rewritten)
    {
        foreach (var workload in workloads)
        {
            stored[workload].AttachmentPinDocument.Should().NotBeNull($"the pin on {workload} lands");
        }

        foreach (var workload in rewritten)
        {
            stored[workload].GetResolvedParameters()["port"].Should().Be("5541", $"the rewrite of {workload} survives the pin");
        }
    }

    [SkippableFact]
    public async Task A_pin_is_written_when_only_another_pinned_row_was_rewritten()
    {
        string[] workloads = ["matter-server", "otbr"];
        string[] rewritten = ["otbr"];

        var (result, stored) = await InitiateAsync(workloads, rewritten);

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);
        AssertEveryPinLanded(stored, workloads, rewritten);
    }

    [SkippableFact]
    public async Task Pins_are_written_when_every_pinned_row_was_rewritten_at_once()
    {
        string[] workloads = ["matter-server", "otbr", "relay", "hub"];

        var (result, stored) = await InitiateAsync(workloads, workloads);

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started);
        AssertEveryPinLanded(stored, workloads, workloads);
    }

    private sealed class NoNudge : IWorkloadUpdateReconcileNudge
    {
        public Task NudgeAsync(Guid pluginInstanceId, string reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FixedClock : IWorkloadClock
    {
        public DateTimeOffset UtcNow => Now.AddMinutes(2);
    }

    private sealed class OnlyStage : IStage<WorkloadUpdateStageContext>
    {
        public string Id => "stage.test";

        public StagePlacement Placement => StagePlacement.First;

        public StageReversibility Reversibility => StageReversibility.Reversible;

        public StageBudget Budget => StageBudget.None;

        public ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);

        public ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);
    }

    private sealed class RacingPlanner(
        Guid group,
        string[] workloads,
        IWorkloadInstanceRepository instances,
        WorkloadsStore store,
        string[] rewritten) : IWorkloadUpdatePlanner
    {
        public static string Fingerprint(Guid group, string[] workloads)
            => WorkloadRequirementsFacts.Fingerprint(Requirements(group, workloads));

        public async Task<WorkloadUpdatePlan> BuildPlanAsync(
            Guid pluginInstanceId,
            string candidateVersion,
            Guid? intentId,
            IReadOnlyDictionary<string, string?>? operatorParameters = null,
            IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds = null,
            WorkloadUpdatePlanOrigin? origin = null,
            CancellationToken cancellationToken = default)
        {
            await instances.ListByPluginInstanceAsync(group, cancellationToken);
            await using var racer = store.NewContext();
            foreach (var row in await racer.WorkloadInstances.Where(row => rewritten.Contains(row.WorkloadId)).ToListAsync(cancellationToken))
            {
                row.SetResolvedParameters(new Dictionary<string, string?> { ["port"] = "5541" }, Now.AddMinutes(1));
            }

            await racer.SaveChangesAsync(cancellationToken);

            var items = workloads.Select(workload => Item(group, workload)).ToList();
            var targetSpecs = items.ToDictionary(item => item.WorkloadId, item => item.TargetSpec, StringComparer.Ordinal);
            var empty = new DeploymentManifest { SchemaVersion = 1, Workloads = [] };
            var requirements = Requirements(group, workloads);
            return new WorkloadUpdatePlan
            {
                GroupInstanceId = group,
                GroupId = "matter",
                SourceKey = "22222222-2222-2222-2222-222222222222",
                FromVersion = "2.5.0",
                ToVersion = candidateVersion,
                PreviousManifest = empty,
                CandidateManifest = empty,
                PreviousRequirements = WorkloadRequirementsFacts.FromSpecs(new Dictionary<string, WorkloadDesiredSpec>()),
                CandidateRequirements = requirements,
                RequirementsFingerprint = WorkloadRequirementsFacts.Fingerprint(requirements),
                PreviousResolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
                PreviousStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
                CandidateResolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
                CandidateStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
                Workloads = items,
                TargetSpecs = targetSpecs,
                PreviousSpecs = new Dictionary<string, WorkloadDesiredSpec>(),
                FromDigests = new Dictionary<string, string?>(StringComparer.Ordinal),
                ToDigests = new Dictionary<string, string?>(StringComparer.Ordinal),
            };
        }

        private static IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> Requirements(Guid group, string[] workloads)
            => WorkloadRequirementsFacts.FromSpecs(
                workloads.ToDictionary(workload => workload, workload => Spec(group, workload), StringComparer.Ordinal));

        private static WorkloadDesiredSpec Spec(Guid group, string workload) => new()
        {
            GroupInstanceId = group,
            GroupId = "matter",
            WorkloadId = workload,
            InstanceId = group,
            Inst8 = "11111111",
            Provider = StandInProviders.KubernetesId,
            ResourceName = $"matter-{workload}-11111111",
            SecretName = $"matter-{workload}-11111111-secret",
            Labels = new Dictionary<string, string>(),
            Image = "reeve/matter@sha256:deadbeef",
            Kind = WorkloadKind.Service,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            SpecHash = "sha-new",
        };

        private static WorkloadUpdatePlanItem Item(Guid group, string workload) => new()
        {
            WorkloadId = workload,
            Reference = new WorkloadRef
            {
                InstanceId = group,
                GroupId = "matter",
                WorkloadId = workload,
                Inst8 = "11111111",
                Provider = StandInProviders.KubernetesId,
            },
            TargetSpec = Spec(group, workload),
            PreviousSpec = null,
            CurrentExtensions = new WorkloadExtensions(),
            CandidateExtensions = new WorkloadExtensions(),
            Certification = WorkloadHealthGateCertification.HostEvidence,
            PreviousCertification = WorkloadHealthGateCertification.HostEvidence,
        };
    }
}
