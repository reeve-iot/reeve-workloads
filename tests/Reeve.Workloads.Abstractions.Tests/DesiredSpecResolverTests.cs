using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class DesiredSpecResolverTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static WorkloadDesiredSpec Spec(string hash) => new()
    {
        GroupInstanceId = Guid.NewGuid(),
        GroupId = "com.reeve.x",
        WorkloadId = "w",
        InstanceId = InstanceId,
        Inst8 = "11111111",
        Provider = StandInProviders.KubernetesId,
        ResourceName = "reeve-wl-x-w",
        SecretName = "reeve-wl-x-w",
        Labels = new Dictionary<string, string>(),
        Image = "ghcr.io/x@sha256:" + new string('a', 64),
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = hash,
    };

    private sealed class StubIntentProvider : IActiveWorkloadIntentProvider
    {
        private readonly WorkloadDesiredSpec? _target;
        public StubIntentProvider(WorkloadDesiredSpec? target) => _target = target;
        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid pluginInstanceId, string workloadId, CancellationToken ct)
            => Task.FromResult(_target);
    }

    [Fact]
    public async Task No_active_intent_computes_fresh()
    {
        var resolver = new DesiredSpecResolver(new StubIntentProvider(target: null));
        var fresh = Spec("fresh");

        var result = await resolver.ResolveAsync(InstanceId, "w", () => fresh, CancellationToken.None);

        result.Should().BeSameAs(fresh);
    }

    [Fact]
    public async Task An_active_intent_target_wins_over_fresh_computation()
    {
        var target = Spec("intent-target");
        var resolver = new DesiredSpecResolver(new StubIntentProvider(target));

        var result = await resolver.ResolveAsync(InstanceId, "w", () => Spec("fresh"), CancellationToken.None);

        result.Should().BeSameAs(target);
        result.SpecHash.Should().Be("intent-target");
    }

    [Fact]
    public async Task The_fresh_delegate_is_not_invoked_when_an_intent_is_active()
    {
        var target = Spec("intent-target");
        var resolver = new DesiredSpecResolver(new StubIntentProvider(target));
        var freshCalls = 0;

        await resolver.ResolveAsync(InstanceId, "w", () => { freshCalls++; return Spec("fresh"); }, CancellationToken.None);

        freshCalls.Should().Be(0, "the in-flight target short-circuits — the reconciler must not pay the fresh-compute cost mid-update");
    }

    [Fact]
    public async Task The_null_default_provider_always_takes_the_fresh_path()
    {
        var resolver = new DesiredSpecResolver(new NullActiveWorkloadIntentProvider());
        var fresh = Spec("fresh");

        var result = await resolver.ResolveAsync(InstanceId, "w", () => fresh, CancellationToken.None);

        result.Should().BeSameAs(fresh);
    }

    [Fact]
    public async Task NullActiveWorkloadIntentProvider_returns_null()
    {
        var provider = new NullActiveWorkloadIntentProvider();
        var target = await provider.TryGetActiveTargetSpecAsync(Guid.NewGuid(), "w", CancellationToken.None);
        target.Should().BeNull();
    }
}
