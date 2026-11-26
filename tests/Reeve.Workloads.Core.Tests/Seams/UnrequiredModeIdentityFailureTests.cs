using FluentAssertions;
using NSubstitute;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.HealthGate;
using Reeve.Workloads.Core.Tests.Update;
using Reeve.Workloads.Core.Tests.Update.Stages;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Seams;

public sealed class UnrequiredModeIdentityFailureTests
{
    private static readonly DateTimeOffset LongAfterApply = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class WrongIdentityCertifier : IRolloutCertifier
    {
        public IReadOnlySet<WorkloadHealthMode> RequiredModes { get; } = new HashSet<WorkloadHealthMode>();

        public int Asked { get; private set; }

        public Task<RolloutEvidence> CertifyAsync(
            RolloutCertificationRequest request, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            Asked++;
            return Task.FromResult(RolloutEvidence.From(new RolloutCandidate
            {
                ObservedSinceUtc = LongAfterApply,
                IdentityFailure = "SpecHashMismatch",
                Detail = "reported 'spec-a', applied 'spec-b'",
            }));
        }
    }

    private static WorkloadUpdateHealthGateAdapter Adapter(IRolloutCertifier certifier)
    {
        var provider = Substitute.For<IWorkloadProvider>();
        provider.GetStatusAsync(Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
            .Returns(new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true });
        var registry = Substitute.For<IWorkloadProviderRegistry>();
        registry.TryResolve(Arg.Any<WorkloadProviderId>()).Returns(provider);
        return new WorkloadUpdateHealthGateAdapter([certifier], registry, new MutableClock(LongAfterApply.AddMinutes(1)));
    }

    [Fact]
    public async Task Under_provider_ready_a_wrong_identity_fails_the_gate_with_the_hosts_code()
    {
        var certifier = new WrongIdentityCertifier();

        var tick = await Adapter(certifier).EvaluateTickAsync(new WorkloadHealthGateTickRequest
        {
            Reference = WorkloadUpdatePlanBuilder.RefFor("alpha", WorkloadUpdatePlanBuilder.InstanceGuid),
            InstanceId = WorkloadUpdatePlanBuilder.InstanceGuid,
            ExpectedSpecHash = "spec-b",
            ApplyTimestampUtc = LongAfterApply.AddMinutes(-5),
            CurrentExtensions = WorkloadExtensions.None,
            CandidateExtensions = WorkloadExtensions.None,
            Timeout = TimeSpan.FromMinutes(5),
            Certification = WorkloadHealthGateCertification.ProviderReady,
        });

        certifier.Asked.Should().Be(1, "evidence is gathered whether or not the mode requires it");
        tick.Verdict.Should().Be(WorkloadHealthGateVerdict.Failed,
            "the provider is Ready, which alone would pass this mode; the wrong identity is what fails it");
        tick.FailureReason.Should().Be("SpecHashMismatch");
    }

    [Fact]
    public async Task The_step_message_names_the_hosts_code_in_parentheses()
    {
        var plan = StageContextFactory.DefaultPlan();
        plan = plan with
        {
            Workloads = [.. plan.Workloads.Select(item => item with
            {
                Certification = WorkloadHealthGateCertification.ProviderReady,
            })],
        };
        var context = await StageContextFactory.BuildAsync(plan);
        var stage = new HealthGateStage(
            Adapter(new WrongIdentityCertifier()),
            new MutableClock(LongAfterApply.AddMinutes(1)),
            new WorkloadUpdateOrchestratorOptions { HealthGateTimeout = TimeSpan.FromMinutes(5) });

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Failed);
        outcome.Detail.Should().Contain("(SpecHashMismatch)");
    }
}
