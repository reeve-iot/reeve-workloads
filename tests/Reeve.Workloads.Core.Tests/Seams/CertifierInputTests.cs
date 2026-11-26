using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Groups;
using Reeve.Workloads.Core.HealthGate;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Tests.Update;
using Reeve.Workloads.Core.Tests.Update.Stages;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Seams;

public sealed class CertifierInputTests
{
    private const string GroupId = "acme.zwave";
    private const string From = "1.0.0";
    private const string To = "1.1.0";
    private static readonly DateTimeOffset At = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid GroupInstanceId = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid IntentId = new("99999999-8888-7777-6666-555555555555");
    private static readonly WorkloadHealthMode SessionMode = RecordingRolloutCertifier.SessionMode;

    private static readonly WorkloadExtensions AlphaPrevious = Extensions("alpha-previous");
    private static readonly WorkloadExtensions AlphaCandidate = Extensions("alpha-candidate");
    private static readonly WorkloadExtensions BetaPrevious = Extensions("beta-previous");
    private static readonly WorkloadExtensions BetaCandidate = Extensions("beta-candidate");
    private static readonly WorkloadExtensions GammaCandidate = Extensions("gamma-candidate");

    [Fact]
    public async Task Forward_the_certifier_is_asked_about_the_candidate_against_the_side_that_runs_now()
    {
        var rig = new Rig();
        var plan = await rig.PlanAsync(BothSides());
        var alpha = Item(plan, "alpha");

        await rig.TickGateAsync(plan);

        var request = rig.Certifier.Requests.Single(r => r.InstanceId == alpha.Reference.InstanceId);
        request.ExpectedSpecHash.Should().Be(alpha.TargetSpec.SpecHash,
            "nothing has recorded an applied hash yet, so the rendered target is what must be running");
        request.ToVersion.Should().Be(To);
        request.CurrentExtensions.Should().BeSameAs(AlphaPrevious);
        request.CandidateExtensions.Should().BeSameAs(AlphaCandidate);
    }

    [Fact]
    public async Task Forward_the_hash_the_apply_recorded_wins_over_the_rendered_target()
    {
        var rig = new Rig();
        var plan = await rig.PlanAsync(BothSides());
        var alpha = Item(plan, "alpha");

        await rig.TickGateAsync(plan, intent =>
            WorkloadUpdateSteps.Ensure(intent, "alpha", At).SetAppliedSpecHash("applied-by-the-provider", At));

        rig.Certifier.Requests.Single(r => r.InstanceId == alpha.Reference.InstanceId)
            .ExpectedSpecHash.Should().Be("applied-by-the-provider");
        rig.Certifier.Requests.Single(r => r.InstanceId == Item(plan, "beta").Reference.InstanceId)
            .ExpectedSpecHash.Should().Be(Item(plan, "beta").TargetSpec.SpecHash,
                "a step with no recorded hash still falls back to its own target");
    }

    [Fact]
    public async Task An_added_workload_is_certified_with_the_candidates_extensions_on_both_sides()
    {
        var rig = new Rig();
        var plan = await rig.PlanAsync(BothSides());
        var gamma = Item(plan, "gamma");

        await rig.TickGateAsync(plan);

        var request = rig.Certifier.Requests.Single(r => r.InstanceId == gamma.Reference.InstanceId);
        request.InstanceId.Should().Be(GroupManifestUpdatePlanner.AddedWorkloadInstanceId(IntentId, "gamma"),
            "an added workload is certified under the id the apply created it with");
        request.CurrentExtensions.Should().BeSameAs(GammaCandidate);
        request.CandidateExtensions.Should().BeSameAs(GammaCandidate);
    }

    public static TheoryData<string> UnreadablePreviousSides => ["not-materialised", "refused", "no-from-version"];

    [Theory]
    [MemberData(nameof(UnreadablePreviousSides))]
    public async Task An_unreadable_previous_side_is_certified_with_the_candidates_extensions(string how)
    {
        var rig = new Rig();
        var manifests = new FakeManifests();
        manifests.AtVersion[To] = new GroupManifestRead(Candidate(), Refused: false);
        if (how == "refused")
        {
            manifests.AtVersion[From] = new GroupManifestRead(null, Refused: true);
        }

        var plan = await rig.PlanAsync(manifests, installedVersion: how == "no-from-version" ? null : From);

        await rig.TickGateAsync(plan);

        var alpha = rig.Certifier.Requests.Single(r => r.InstanceId == Item(plan, "alpha").Reference.InstanceId);
        alpha.CurrentExtensions.Should().BeSameAs(AlphaCandidate);
        alpha.CandidateExtensions.Should().BeSameAs(AlphaCandidate);
    }

    [Fact]
    public async Task Rollback_the_certifier_is_asked_about_the_recorded_previous_spec_on_both_sides()
    {
        var rig = new Rig();
        var planned = await rig.PlanAsync(BothSides());
        var plan = planned with
        {
            Workloads = [.. planned.Workloads.Select(item => item.PreviousSpec is { } previous
                ? item with { PreviousSpec = previous with { SpecHash = $"recorded-{item.WorkloadId}" } }
                : item)],
        };

        await rig.TickRollbackAsync(plan);

        var alpha = rig.Certifier.Requests.Single(r => r.InstanceId == Item(plan, "alpha").Reference.InstanceId);
        alpha.ExpectedSpecHash.Should().Be("recorded-alpha");
        alpha.ToVersion.Should().Be(From);
        alpha.CurrentExtensions.Should().BeSameAs(AlphaPrevious);
        alpha.CandidateExtensions.Should().BeSameAs(AlphaPrevious,
            "a rollback certifies against the old pair on both sides");

        rig.Certifier.Requests.Should().NotContain(r => r.InstanceId == Item(plan, "gamma").Reference.InstanceId,
            "an added workload is torn down, never certified on the way back");
    }

    [Fact]
    public async Task The_certifier_is_asked_on_every_tick_for_every_mode()
    {
        var rig = new Rig();
        var planned = await rig.PlanAsync(BothSides());
        var plan = planned with
        {
            Workloads = [.. planned.Workloads.Select(item => item.WorkloadId == "gamma"
                ? item with { Certification = WorkloadHealthGateCertification.AppliedSpec }
                : item)],
        };
        plan.Workloads.Select(item => item.Certification).Should().BeEquivalentTo(
            [
                WorkloadHealthGateCertification.HostEvidence,
                WorkloadHealthGateCertification.ProviderReady,
                WorkloadHealthGateCertification.AppliedSpec,
            ],
            "the three certifications, one per workload");

        foreach (var workload in new[] { "alpha", "beta", "gamma" })
        {
            rig.Provider.StatusByWorkload[workload] = new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Ready,
                ProviderReady = true,
            };
        }

        var context = await StageContextFactory.BuildAsync(plan);
        (await rig.GateStage.UpAsync(context, CancellationToken.None)).Kind
            .Should().Be(Reeve.Pipeline.StageOutcomeKind.Working, "the host-evidence workload has no candidate yet");
        await rig.GateStage.UpAsync(context, CancellationToken.None);

        rig.Certifier.Requests.GroupBy(r => r.InstanceId).Select(asked => asked.Count())
            .Should().Equal([2, 2, 2], "two ticks, three workloads, each asked on each tick");
    }

    [Fact]
    public async Task With_no_certifier_every_mode_certifies_on_provider_ready_and_readiness_passes_it()
    {
        var rig = new Rig(withCertifier: false);
        foreach (var workload in new[] { "alpha", "beta", "gamma" })
        {
            rig.Provider.StatusByWorkload[workload] = new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Ready,
                ProviderReady = true,
            };
        }

        var plan = await rig.PlanAsync(BothSides());
        var context = await StageContextFactory.BuildAsync(plan);

        plan.Workloads.Select(item => item.Certification)
            .Should().OnlyContain(certification => certification == WorkloadHealthGateCertification.ProviderReady);
        (await rig.GateStage.UpAsync(context, CancellationToken.None)).Kind
            .Should().Be(Reeve.Pipeline.StageOutcomeKind.Done);
        rig.Certifier.Requests.Should().BeEmpty();
    }

    private sealed class Rig
    {
        public RecordingRolloutCertifier Certifier { get; } = new(SessionMode);
        public RecordingProvider Provider { get; } = new(KubernetesProvider.Id, new CallLog());
        public HealthGateStage GateStage { get; }
        public SidecarStage SidecarStage { get; }

        private readonly IRolloutCertifier[] certifiers;

        public Rig(bool withCertifier = true)
        {
            certifiers = withCertifier ? [Certifier] : [];
            foreach (var workload in new[] { "alpha", "beta", "gamma" })
            {
                Provider.StatusByWorkload[workload] = new WorkloadStatus { Phase = WorkloadProviderPhase.Pending, ProviderReady = false };
            }

            var registry = new FakeProviderRegistry(Provider);
            var clock = new MutableClock(At.AddMinutes(1));
            var options = new WorkloadUpdateOrchestratorOptions();
            var gate = new WorkloadUpdateHealthGateAdapter(certifiers, registry, clock);
            GateStage = new HealthGateStage(gate, clock, options);
            SidecarStage = new SidecarStage(registry, gate, clock, options,
                NSubstitute.Substitute.For<Reeve.Workloads.Abstractions.IWorkloadInstanceRepository>(),
                new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());
        }

        public Task<WorkloadUpdatePlan> PlanAsync(FakeManifests manifests, string? installedVersion = From)
        {
            var modules = new InMemoryWorkloadGroupRepository();
            modules.Seed(GroupId, FakeManifests.SourceKey, installedVersion);
            var instances = new FakeInstances();
            instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), GroupInstanceId, GroupId, "alpha", At));
            instances.Instances.Add(WorkloadInstance.Create(Guid.NewGuid(), GroupInstanceId, GroupId, "beta", At));

            IWorkloadUpdatePlanner planner = new GroupManifestUpdatePlanner(
                manifests,
                modules,
                instances,
                new DefaultSettings(KubernetesProvider.Id),
                new NoIdentity(),
                new WorkloadProviderRegistry([]),
                new WorkloadResourceNamerRegistry([]),
                new FakeHostBootstrapSource(),
                Options.Create(new ProductImageOptions()),
                NullLogger<GroupManifestUpdatePlanner>.Instance,
                certifiers,
                new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider()), new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver());
            return planner.BuildPlanAsync(GroupInstanceId, To, IntentId);
        }

        public async Task TickGateAsync(WorkloadUpdatePlan plan, Action<WorkloadUpdateIntent>? arrange = null)
        {
            var context = await StageContextFactory.BuildAsync(plan, arrange);
            await GateStage.UpAsync(context, CancellationToken.None);
        }

        public async Task TickRollbackAsync(WorkloadUpdatePlan plan)
        {
            var context = await StageContextFactory.BuildCompensatingAsync(plan);
            await SidecarStage.DownAsync(context, CancellationToken.None);
        }
    }

    private static WorkloadUpdatePlanItem Item(WorkloadUpdatePlan plan, string workloadId)
        => plan.Workloads.Single(item => item.WorkloadId == workloadId);

    private static FakeManifests BothSides()
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(
            new DeploymentManifest
            {
                SchemaVersion = 1,
                Workloads =
                [
                    Workload("alpha", SessionMode, AlphaPrevious),
                    Workload("beta", WorkloadHealthMode.Http, BetaPrevious),
                ],
            },
            Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(Candidate(), Refused: false);
        return manifests;
    }

    private static DeploymentManifest Candidate() => new()
    {
        SchemaVersion = 1,
        Workloads =
        [
            Workload("alpha", SessionMode, AlphaCandidate),
            Workload("beta", WorkloadHealthMode.Http, BetaCandidate),
            Workload("gamma", WorkloadHealthMode.Container, GammaCandidate),
        ],
    };

    private static WorkloadManifest Workload(string id, WorkloadHealthMode mode, WorkloadExtensions extensions) => new()
    {
        Id = id,
        DisplayName = id,
        Image = new WorkloadImage { Ref = "registry.example/acme", Digest = new string('a', 64) },
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = mode },
        Extensions = extensions,
    };

    private static WorkloadExtensions Extensions(string side) => new()
    {
        ByNamespace = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["acme"] = JsonDocument.Parse($$"""{"side":"{{side}}"}""").RootElement.Clone(),
        },
    };
}
