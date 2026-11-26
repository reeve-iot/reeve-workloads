using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Core;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Hosting;

public sealed class SingleReplicaLeaderElectionTests
{
    [Fact]
    public async Task It_always_wins()
    {
        var election = new SingleReplicaLeaderElection(NullLogger<SingleReplicaLeaderElection>.Instance);

        await using var leadership = await election.TryAcquireAsync(CancellationToken.None);

        leadership.Should().NotBeNull();
        leadership!.Lost.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task It_never_loses_until_it_is_disposed()
    {
        var election = new SingleReplicaLeaderElection(NullLogger<SingleReplicaLeaderElection>.Instance);
        var leadership = await election.TryAcquireAsync(CancellationToken.None);

        await leadership!.DisposeAsync();

        leadership.Lost.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task It_says_out_loud_that_a_second_replica_would_run_a_second_engine()
    {
        var logger = new RecordingLogger<SingleReplicaLeaderElection>();
        var election = new SingleReplicaLeaderElection(logger);

        await using var leadership = await election.TryAcquireAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle()
            .Which.Text.Should().Contain("second replica");
    }

    [Fact]
    public async Task It_announces_the_leadership_once_however_often_it_is_acquired()
    {
        var logger = new RecordingLogger<SingleReplicaLeaderElection>();
        var election = new SingleReplicaLeaderElection(logger);

        await (await election.TryAcquireAsync(CancellationToken.None))!.DisposeAsync();
        await (await election.TryAcquireAsync(CancellationToken.None))!.DisposeAsync();

        logger.Entries.Should().ContainSingle();
    }
}
