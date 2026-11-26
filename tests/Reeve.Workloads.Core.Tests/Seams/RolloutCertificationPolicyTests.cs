using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.HealthGate;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Seams;

public sealed class RolloutCertificationPolicyTests
{
    private static readonly WorkloadHealthMode HostMode = new("adapter-session");

    private sealed class DeclaringCertifier(params WorkloadHealthMode[] required) : IRolloutCertifier
    {
        public IReadOnlySet<WorkloadHealthMode> RequiredModes { get; } = new HashSet<WorkloadHealthMode>(required);

        public Task<RolloutEvidence> CertifyAsync(
            RolloutCertificationRequest request, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the policy never asks for evidence");
    }

    private static WorkloadManifest Workload(WorkloadHealthMode mode, WorkloadKind kind = WorkloadKind.Service) => new()
    {
        Id = "controller",
        DisplayName = "Controller",
        Kind = kind,
        Image = new WorkloadImage { Ref = "acme/controller", Tag = "1.0.0" },
        Health = new WorkloadHealth { Mode = mode },
    };

    [Fact]
    public void A_required_mode_is_certified_by_host_evidence()
    {
        var policy = new RolloutCertificationPolicy(new DeclaringCertifier(HostMode));

        policy.CertificationFor(Workload(HostMode)).Should().Be(WorkloadHealthGateCertification.HostEvidence);
    }

    [Theory]
    [InlineData("acme-heartbeat")]
    [InlineData("http")]
    [InlineData("container")]
    public void An_unrequired_mode_is_certified_by_provider_ready(string mode)
    {
        var policy = new RolloutCertificationPolicy(new DeclaringCertifier(HostMode));

        policy.CertificationFor(Workload(new WorkloadHealthMode(mode)))
            .Should().Be(WorkloadHealthGateCertification.ProviderReady);
    }

    [Fact]
    public void Scan_per_node_is_certified_by_the_applied_spec_even_when_its_mode_is_required()
    {
        var policy = new RolloutCertificationPolicy(new DeclaringCertifier(HostMode));

        policy.CertificationFor(Workload(HostMode, WorkloadKind.ScanPerNode))
            .Should().Be(WorkloadHealthGateCertification.AppliedSpec);
    }

    [Theory]
    [InlineData("adapter-session")]
    [InlineData("acme-heartbeat")]
    [InlineData("http")]
    public void With_no_certifier_every_mode_is_certified_by_provider_ready(string mode)
    {
        var policy = new RolloutCertificationPolicy(certifier: null);

        policy.RequiredModes.Should().BeEmpty();
        policy.CertificationFor(Workload(new WorkloadHealthMode(mode)))
            .Should().Be(WorkloadHealthGateCertification.ProviderReady);
    }

    [Fact]
    public void Built_from_the_registrations_it_reads_the_one_certifier_and_refuses_two()
    {
        new RolloutCertificationPolicy([]).RequiredModes.Should().BeEmpty();
        new RolloutCertificationPolicy([new DeclaringCertifier(HostMode)]).RequiredModes
            .Should().BeEquivalentTo([HostMode]);

        var two = () => new RolloutCertificationPolicy([new DeclaringCertifier(HostMode), new DeclaringCertifier()]);
        two.Should().Throw<InvalidOperationException>().WithMessage("*2 implementations of IRolloutCertifier*");
    }
}
