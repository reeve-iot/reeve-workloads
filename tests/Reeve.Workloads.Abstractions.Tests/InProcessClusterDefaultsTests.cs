using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class InProcessClusterDefaultsTests
{
    [Fact]
    public async Task Publishing_with_no_peers_delivers_nothing_and_never_throws()
    {
        var bus = new InProcessWorkloadClusterBus();
        var delivered = 0;

        using var subscription = bus.Subscribe<string>(
            WorkloadClusterChannels.ReconcileRequest, (_, _) => { delivered++; return Task.CompletedTask; });
        await bus.PublishAsync(WorkloadClusterChannels.ReconcileRequest, "hint");

        delivered.Should().Be(0);
    }

    [Fact]
    public void Disposing_a_subscription_twice_is_harmless()
    {
        var bus = new InProcessWorkloadClusterBus();

        var subscription = bus.Subscribe<string>(
            WorkloadClusterChannels.ReconcileRequest, (_, _) => Task.CompletedTask);

        subscription.Dispose();
        FluentActions.Invoking(subscription.Dispose).Should().NotThrow();
    }

    [Fact]
    public async Task A_lock_is_held_until_disposed_and_a_second_take_fails_meanwhile()
    {
        var locks = new InProcessWorkloadLockProvider();

        var first = await locks.TryAcquireAsync("switch:a");
        first.Should().NotBeNull();
        (await first!.IsValidAsync()).Should().BeTrue();
        (await locks.TryAcquireAsync("switch:a")).Should().BeNull("the caller decides whether losing is a retry");

        await first.DisposeAsync();

        (await first.IsValidAsync()).Should().BeFalse();
        (await locks.TryAcquireAsync("switch:a")).Should().NotBeNull();
    }

    [Fact]
    public async Task Different_keys_do_not_contend()
    {
        var locks = new InProcessWorkloadLockProvider();

        await using var a = await locks.TryAcquireAsync("switch:a");
        await using var b = await locks.TryAcquireAsync("switch:b");

        a.Should().NotBeNull();
        b.Should().NotBeNull();
    }

    [Fact]
    public async Task Releasing_twice_does_not_release_someone_elses_take()
    {
        var locks = new InProcessWorkloadLockProvider();

        var first = await locks.TryAcquireAsync("switch:a");
        await first!.DisposeAsync();
        var second = await locks.TryAcquireAsync("switch:a");
        await first.DisposeAsync();

        (await second!.IsValidAsync()).Should().BeTrue();
        (await locks.TryAcquireAsync("switch:a")).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_takes_of_one_key_produce_exactly_one_winner()
    {
        var locks = new InProcessWorkloadLockProvider();

        var takes = await Task.WhenAll(
            Enumerable.Range(0, 64).Select(_ => locks.TryAcquireAsync("switch:contended")));

        takes.Count(held => held is not null).Should().Be(1);
    }
}
