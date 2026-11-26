using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Groups;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Update;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class AddedWorkloadIdentityTests
{
    private const string GroupId = "acme.zwave";
    private const string Existing = "controller";
    private const string Added = "ui";
    private const string From = "1.0.0";
    private const string To = "1.1.0";
    private static readonly Guid GroupInstanceId = new("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset At = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_gate_and_the_rollback_act_on_the_added_workload_the_apply_created()
    {
        var planner = RealPlanner();
        var first = await planner.BuildPlanAsync(GroupInstanceId, To, intentId: null);
        var h = new OrchestratorHarness(first, At);
        h.Planner.Real = planner;
        h.Gate.VerdictsByWorkload[Added] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        OperatorRedeployed(h, first);
        var intent = await h.StageManualAsync(first);

        for (var tick = 0; tick < 30 && !intent.IsTerminal; tick++)
        {
            await h.Orchestrator.AdvanceAsync(intent);
        }

        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);

        var applied = h.Provider.AppliedSpecs.Single(spec => spec.WorkloadId == Added);
        var created = WorkloadRef.FromDesiredSpec(applied);
        using var scope = new AssertionScope();
        h.Gate.Requests.Single(request => request.Reference.WorkloadId == Added).Reference.Should().Be(created,
            "the gate must certify the workload the apply created, not one a later build renamed");
        h.Provider.TornDownReferences.Should().Equal([created],
            "a rollback that tears down another name leaves the applied one running with nothing that owns it");
        intent.ReadTargetSpecs().Should().Match<WorkloadDocumentReading<IReadOnlyDictionary<string, WorkloadDesiredSpec>>>(
            reading => reading.Verdict == WorkloadDocumentVerdict.Value
                && reading.Value![Added].InstanceId == applied.InstanceId
                && reading.Value[Added].SpecHash == applied.SpecHash,
            "the snapshot the initiator stages is the intent's record of what it converges on, so it must name the "
            + "workload the walk applied");
    }

    [Fact]
    public async Task A_retried_update_applies_its_added_workload_under_a_new_identity()
    {
        var planner = RealPlanner();
        var first = await planner.BuildPlanAsync(GroupInstanceId, To, intentId: null);
        var h = new OrchestratorHarness(first, At);
        h.Planner.Real = planner;
        h.Gate.VerdictsByWorkload[Added] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        OperatorRedeployed(h, first);
        var attempt = await h.StageManualAsync(first);
        for (var tick = 0; tick < 30 && !attempt.IsTerminal; tick++)
        {
            await h.Orchestrator.AdvanceAsync(attempt);
        }

        attempt.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack, "the retry can only be staged once the attempt is over");

        var retry = await h.StageManualAsync(first);
        for (var tick = 0; tick < 10 && h.Provider.AppliedSpecs.Count(spec => spec.WorkloadId == Added) < 2; tick++)
        {
            await h.Orchestrator.AdvanceAsync(retry);
        }

        var applied = h.Provider.AppliedSpecs.Where(spec => spec.WorkloadId == Added).Select(spec => spec.InstanceId).ToList();
        applied.Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    private static IWorkloadUpdatePlanner RealPlanner()
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(Manifest(Existing), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(Manifest(Existing, Added), Refused: false);
        var modules = new InMemoryWorkloadGroupRepository();
        modules.Seed(GroupId, FakeManifests.SourceKey, From);
        var instances = new FakeInstances();
        instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), GroupInstanceId, GroupId, Existing, At));

        return new GroupManifestUpdatePlanner(
            manifests,
            modules,
            instances,
            new DefaultSettings(),
            new NoIdentity(),
            new WorkloadProviderRegistry([]),
            new WorkloadResourceNamerRegistry([]),
            new FakeHostBootstrapSource(),
            Options.Create(new ProductImageOptions()),
            NullLogger<GroupManifestUpdatePlanner>.Instance,
            [],
            new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider()), new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver());
    }

    private static void OperatorRedeployed(OrchestratorHarness h, WorkloadUpdatePlan plan)
    {
        foreach (var item in plan.Workloads)
        {
            h.ObservedVersions.VersionsByInstance[item.Reference.InstanceId] = To;
        }
    }

    private static DeploymentManifest Manifest(params string[] workloadIds) => new()
    {
        SchemaVersion = 1,
        Workloads = [.. workloadIds.Select(Workload)],
    };

    private static WorkloadManifest Workload(string workloadId) => new()
    {
        Id = workloadId,
        DisplayName = workloadId,
        Image = new WorkloadImage { Ref = "registry.example/zwave", Digest = new string('a', 64) },
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        Extensions = new WorkloadExtensions
        {
            ByNamespace = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["reeve"] = JsonDocument
                    .Parse("""{"protocol":{"version":"1","minSupported":"1"}}""").RootElement.Clone(),
            },
        },
    };
}
