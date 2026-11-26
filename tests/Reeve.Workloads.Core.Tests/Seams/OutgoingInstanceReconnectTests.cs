using FluentAssertions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.HealthGate;
using Reeve.Workloads.Core.Tests.Update;
using Reeve.Workloads.Core.Tests.Update.Stages;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Seams;

public sealed class OutgoingInstanceReconnectTests
{
    private const string OutgoingHash = "sha256:outgoing";
    private const string ExpectedHash = "sha256:expected";
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_outgoing_workload_reconnecting_after_the_anchor_keeps_the_gate_waiting_and_the_new_one_passes_it()
    {
        var rig = new Rig(previousHash: OutgoingHash);
        var context = await rig.ContextAsync();

        rig.Session = new Session(OutgoingHash, rig.Anchor + TimeSpan.FromSeconds(4));
        var waiting = await rig.Stage.UpAsync(context, CancellationToken.None);
        waiting.Kind.Should().Be(StageOutcomeKind.Working,
            "the old pod reconnecting to another replica is not a verdict on the new one");

        rig.Provider.StatusByWorkload["alpha"] = new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true };
        rig.Session = new Session(ExpectedHash, rig.Anchor + TimeSpan.FromSeconds(12));
        (await rig.Stage.UpAsync(context, CancellationToken.None)).Kind.Should().Be(StageOutcomeKind.Done);
    }

    [Fact]
    public async Task A_hash_that_is_neither_the_outgoing_nor_the_expected_one_still_fails_the_gate()
    {
        var rig = new Rig(previousHash: OutgoingHash);
        var context = await rig.ContextAsync();

        rig.Session = new Session(OutgoingHash, rig.Anchor + TimeSpan.FromSeconds(4));
        (await rig.Stage.UpAsync(context, CancellationToken.None)).Kind.Should().Be(StageOutcomeKind.Working);

        rig.Session = new Session("sha256:something-else", rig.Anchor + TimeSpan.FromSeconds(9));
        var outcome = await rig.Stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Failed);
        outcome.Detail.Should().Contain("SpecHashMismatch");
    }

    [Fact]
    public async Task With_no_previous_spec_a_session_with_another_hash_fails_the_gate_as_before()
    {
        var rig = new Rig(previousHash: null);
        var context = await rig.ContextAsync();

        rig.Session = new Session(OutgoingHash, rig.Anchor + TimeSpan.FromSeconds(4));
        var outcome = await rig.Stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Failed);
        outcome.Detail.Should().Contain("SpecHashMismatch");
        rig.Certifier.Requests.Should().OnlyContain(request => request.OutgoingSpecHash == null);
    }

    [Fact]
    public async Task An_outgoing_workload_that_keeps_answering_runs_into_the_gate_timeout()
    {
        var rig = new Rig(previousHash: OutgoingHash);
        var context = await rig.ContextAsync();
        rig.Session = new Session(OutgoingHash, rig.Anchor + TimeSpan.FromSeconds(4));

        (await rig.Stage.UpAsync(context, CancellationToken.None)).Kind.Should().Be(StageOutcomeKind.Working);
        rig.Clock.Advance(rig.Options.HealthGateTimeout + TimeSpan.FromSeconds(1));
        var outcome = await rig.Stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Failed);
        outcome.Detail.Should().Contain("Timeout").And.NotContain("SpecHashMismatch")
            .And.Contain("only the outgoing workload answered");
    }

    [Fact]
    public async Task A_ready_provider_does_not_pass_the_gate_while_only_the_outgoing_workload_answers()
    {
        var rig = new Rig(previousHash: OutgoingHash);
        var context = await rig.ContextAsync();
        rig.Provider.StatusByWorkload["alpha"] = new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true };
        rig.Session = new Session(OutgoingHash, rig.Anchor + TimeSpan.FromSeconds(4));

        (await rig.Stage.UpAsync(context, CancellationToken.None)).Kind.Should().Be(StageOutcomeKind.Working);
        (await rig.Stage.UpAsync(context, CancellationToken.None)).Kind.Should().Be(StageOutcomeKind.Working);
    }

    [Fact]
    public async Task A_candidate_that_is_outgoing_and_carries_an_identity_failure_keeps_the_gate_waiting()
    {
        var rig = new Rig(previousHash: OutgoingHash);
        var context = await rig.ContextAsync();
        rig.Provider.StatusByWorkload["alpha"] = new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true };
        rig.Certifier.Answer = (_, _) => RolloutEvidence.From(new RolloutCandidate
        {
            ObservedSinceUtc = rig.Anchor + TimeSpan.FromSeconds(4),
            IsOutgoing = true,
            IdentityFailure = "SpecHashMismatch",
        });

        (await rig.Stage.UpAsync(context, CancellationToken.None)).Kind.Should().Be(StageOutcomeKind.Working);
    }

    [Fact]
    public async Task The_stage_hands_the_gate_the_outgoing_hash_on_the_forward_walk_only()
    {
        var rig = new Rig(previousHash: OutgoingHash);
        var context = await rig.ContextAsync();

        await rig.Stage.UpAsync(context, CancellationToken.None);

        rig.Certifier.Requests.Should().ContainSingle().Which.OutgoingSpecHash.Should().Be(OutgoingHash);
    }

    [Fact]
    public async Task An_update_that_leaves_the_spec_hash_unchanged_names_no_outgoing_hash()
    {
        var rig = new Rig(previousHash: ExpectedHash);
        var context = await rig.ContextAsync();

        await rig.Stage.UpAsync(context, CancellationToken.None);

        rig.Certifier.Requests.Should().ContainSingle().Which.OutgoingSpecHash.Should().BeNull();
    }

    [Fact]
    public async Task A_rollback_names_no_outgoing_hash()
    {
        var rig = new Rig(previousHash: OutgoingHash);
        var context = await StageContextFactory.BuildCompensatingAsync(rig.Plan);

        await rig.Sidecar.DownAsync(context, CancellationToken.None);

        rig.Certifier.Requests.Should().ContainSingle().Which.OutgoingSpecHash.Should().BeNull();
    }

    private sealed record Session(string SpecHash, DateTimeOffset ConnectedAtUtc);

    private sealed class Rig
    {
        public RecordingRolloutCertifier Certifier { get; } = new(RecordingRolloutCertifier.SessionMode);
        public MutableClock Clock { get; } = new(Start);
        public WorkloadUpdateOrchestratorOptions Options { get; } = new();
        public WorkloadUpdatePlan Plan { get; }
        public HealthGateStage Stage { get; }
        public SidecarStage Sidecar { get; }
        public RecordingProvider Provider { get; } = new(KubernetesProvider.Id, new CallLog());
        public Session? Session { get; set; }
        public DateTimeOffset Anchor { get; private set; }

        public Rig(string? previousHash)
        {
            Plan = WorkloadUpdatePlanBuilder.Plan(
                WorkloadUpdatePlanBuilder.InstanceGuid,
                [WorkloadUpdatePlanBuilder.Item("alpha", WorkloadUpdatePlanBuilder.InstanceGuid, ExpectedHash, previousHash)]);

            Certifier.Answer = (request, _) => Session is not { } session
                ? RolloutEvidence.NoCandidate
                : RolloutEvidence.From(new RolloutCandidate
                {
                    ObservedSinceUtc = session.ConnectedAtUtc,
                    IsOutgoing = request.OutgoingSpecHash is not null && session.SpecHash == request.OutgoingSpecHash,
                    IdentityFailure = session.SpecHash == request.ExpectedSpecHash
                                      || session.SpecHash == request.OutgoingSpecHash
                        ? null
                        : "SpecHashMismatch",
                });

            Provider.StatusByWorkload["alpha"] = new WorkloadStatus { Phase = WorkloadProviderPhase.Running, ProviderReady = false };
            var registry = new FakeProviderRegistry(Provider);
            var gate = new WorkloadUpdateHealthGateAdapter([Certifier], registry, Clock);
            Stage = new HealthGateStage(gate, Clock, Options);
            Sidecar = new SidecarStage(registry, gate, Clock, Options,
                NSubstitute.Substitute.For<Reeve.Workloads.Abstractions.IWorkloadInstanceRepository>(),
                new UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());
        }

        public async Task<WorkloadUpdateStageContext> ContextAsync()
        {
            var context = await StageContextFactory.BuildAsync(Plan, intent => Anchor = intent.SidecarAppliedAtUtc ?? intent.PhaseEnteredAtUtc);

            Clock.UtcNow = Anchor + TimeSpan.FromSeconds(20);
            return context;
        }
    }
}
