using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core.Health;
using Reeve.Workloads.Domain;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Seams;

public sealed class HealthContributorDispatchTests
{
    private static readonly WorkloadHealthMode HostMode = new("adapter-session");
    private static readonly WorkloadHealthMode OtherHostMode = new("acme-heartbeat");

    private static readonly WorkloadHealthContribution Distinctive = new()
    {
        Presence = WorkloadPresenceSignal.PresentMismatched,
        RunningVersion = "9.9.9",
        RunningVersionIsOlder = true,
    };

    private sealed class RecordingContributor(
        IReadOnlySet<WorkloadHealthMode> observed, Func<WorkloadHealthContribution>? answer = null)
        : IWorkloadHealthContributor
    {
        public IReadOnlySet<WorkloadHealthMode> ObservedModes { get; } = observed;

        public List<WorkloadHealthObservation> Asked { get; } = [];

        public Task<WorkloadHealthContribution> ObserveAsync(
            WorkloadHealthObservation observation, CancellationToken cancellationToken = default)
        {
            Asked.Add(observation);
            return Task.FromResult((answer ?? (() => Distinctive))());
        }
    }

    private static WorkloadHealthObservation Observation(WorkloadHealthMode mode) => new()
    {
        InstanceId = Guid.Parse("5b0a3f53-7a51-4d5e-9c1f-0a2b3c4d5e6f"),
        HealthMode = mode,
        DesiredSpecHash = "sha256:desired",
        InstalledGroupVersion = "2.0.0",
    };

    [Fact]
    public async Task An_observed_mode_goes_to_the_contributor_that_observes_it()
    {
        var other = new RecordingContributor(new HashSet<WorkloadHealthMode> { OtherHostMode });
        var owner = new RecordingContributor(new HashSet<WorkloadHealthMode> { HostMode });
        var dispatch = new WorkloadHealthContributorDispatch([other, owner]);
        var observation = Observation(HostMode);

        var contribution = await dispatch.ObserveAsync(observation);

        contribution.Should().Be(Distinctive);
        owner.Asked.Should().ContainSingle().Which.Should().BeSameAs(observation, "the inputs pass through unchanged");
        other.Asked.Should().BeEmpty();
    }

    [Theory]
    [InlineData("acme-heartbeat", WorkloadPresenceSignalKind.PresentMatching)]
    [InlineData("http", WorkloadPresenceSignalKind.Absent)]
    [InlineData("container", WorkloadPresenceSignalKind.Absent)]
    public async Task An_unobserved_mode_goes_to_the_provider_ready_contributor(
        string mode, WorkloadPresenceSignalKind expected)
    {
        var owner = new RecordingContributor(new HashSet<WorkloadHealthMode> { HostMode });
        var dispatch = new WorkloadHealthContributorDispatch([owner]);

        var contribution = await dispatch.ObserveAsync(Observation(new WorkloadHealthMode(mode)));

        owner.Asked.Should().BeEmpty("no contributor declared this mode");
        contribution.Presence.Kind.Should().Be(expected);
        contribution.RunningVersion.Should().BeNull();
        contribution.RunningVersionIsOlder.Should().BeFalse();
    }

    [Fact]
    public async Task With_no_contributor_registered_a_host_mode_is_present_and_matching()
    {
        var dispatch = new WorkloadHealthContributorDispatch([]);

        var contribution = await dispatch.ObserveAsync(Observation(HostMode));

        contribution.Presence.Should().Be(WorkloadPresenceSignal.PresentMatching);
    }

    [Fact]
    public async Task A_contributors_read_fault_is_unknown_never_absent()
    {
        var faulting = new RecordingContributor(
            new HashSet<WorkloadHealthMode> { HostMode },
            () => throw new InvalidOperationException("store unreachable"));
        var dispatch = new WorkloadHealthContributorDispatch([faulting]);

        var contribution = await dispatch.ObserveAsync(Observation(HostMode));

        contribution.Presence.Should().Be(WorkloadHealthEvaluator.Unknown);
        contribution.RunningVersion.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_is_not_a_fault()
    {
        var cancelled = new RecordingContributor(
            new HashSet<WorkloadHealthMode> { HostMode },
            () => throw new OperationCanceledException());
        var dispatch = new WorkloadHealthContributorDispatch([cancelled]);

        var act = () => dispatch.ObserveAsync(Observation(HostMode));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
