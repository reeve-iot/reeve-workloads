using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Core.Tests.Update;

public class ProviderReadyHealthContributorTests
{
    private static async Task<WorkloadInstanceState> ReconcileAsync(
        WorkloadHealthMode mode, WorkloadInstanceState currentState = WorkloadInstanceState.Deploying)
    {
        var contribution = await new ProviderReadyHealthContributor().ObserveAsync(new WorkloadHealthObservation
        {
            InstanceId = Guid.NewGuid(),
            HealthMode = mode,
            DesiredSpecHash = "sha256:desired",
        });

        return WorkloadHealthEvaluator.EvaluateTarget(
            mode,
            providerReady: true,
            contribution.Presence,
            desiredSpecHash: "sha256:desired",
            currentState,
            WorkloadStateReason.Provisioning);
    }

    [Theory]
    [InlineData("adapter-session")]
    [InlineData("http")]
    [InlineData("container")]
    [InlineData("")]
    public async Task A_provider_ready_instance_reaches_Healthy_in_every_mode(string modeToken)
    {
        (await ReconcileAsync(new WorkloadHealthMode(modeToken))).Should().Be(WorkloadInstanceState.Healthy);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("container")]
    public async Task No_presence_is_claimed_in_a_mode_that_does_not_ask_for_one(string modeToken)
    {
        var contribution = await new ProviderReadyHealthContributor().ObserveAsync(new WorkloadHealthObservation
        {
            InstanceId = Guid.NewGuid(),
            HealthMode = new WorkloadHealthMode(modeToken),
        });

        contribution.Presence.Kind.Should().Be(WorkloadPresenceSignalKind.Absent);
        contribution.RunningVersion.Should().BeNull();
        contribution.RunningVersionIsOlder.Should().BeFalse(
            "the skew arm reads these on EVERY mode, so a fabricated reading here is one nobody took");
    }

    [Fact]
    public void The_no_evidence_constant_claims_no_presence()
    {
        WorkloadHealthContribution.None.Presence.Kind.Should().Be(WorkloadPresenceSignalKind.Absent);
        WorkloadHealthContribution.None.RunningVersion.Should().BeNull();
        WorkloadHealthContribution.None.RunningVersionIsOlder.Should().BeFalse();
    }

    [Fact]
    public async Task The_no_op_never_invents_a_running_version_or_a_skew()
    {
        var contribution = await new ProviderReadyHealthContributor().ObserveAsync(new WorkloadHealthObservation
        {
            InstanceId = Guid.NewGuid(),
            HealthMode = new WorkloadHealthMode("adapter-session"),
            InstalledGroupVersion = "2.0.0",
        });

        contribution.RunningVersion.Should().BeNull();
        contribution.RunningVersionIsOlder.Should().BeFalse();
    }

    [Fact]
    public async Task A_provider_that_stops_being_ready_still_degrades_a_Healthy_instance()
    {
        var contribution = await new ProviderReadyHealthContributor().ObserveAsync(new WorkloadHealthObservation
        {
            InstanceId = Guid.NewGuid(),
            HealthMode = new WorkloadHealthMode("adapter-session"),
        });

        WorkloadHealthEvaluator.EvaluateTarget(
                new WorkloadHealthMode("adapter-session"),
                providerReady: false,
                contribution.Presence,
                desiredSpecHash: "sha256:desired",
                WorkloadInstanceState.Healthy,
                WorkloadStateReason.HealthObserved)
            .Should().Be(WorkloadInstanceState.Degraded);
    }
}
