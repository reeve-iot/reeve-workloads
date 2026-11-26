using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Manual;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Seams;

public sealed class ManualLivenessTests
{
    private static readonly WorkloadRef Reference = new()
    {
        InstanceId = new Guid("11111111-2222-3333-4444-555555555555"),
        GroupId = "com.acme.bridge",
        WorkloadId = "bridge",
        Inst8 = "11111111",
        Provider = ManualProvider.Id,
    };

    private sealed class ScopedLiveness(Func<Guid, CancellationToken, ValueTask<bool>> answer) : IWorkloadExternalLiveness
    {
        public ValueTask<bool> IsLiveAsync(Guid instanceId, CancellationToken cancellationToken = default)
            => answer(instanceId, cancellationToken);
    }

    private static (IWorkloadProvider Provider, ServiceProvider Container) Build(
        Func<Guid, CancellationToken, ValueTask<bool>> answer)
    {
        var services = new ServiceCollection();
        services.AddScoped<IWorkloadExternalLiveness>(_ => new ScopedLiveness(answer));
        services.AddSingleton<IWorkloadProvider>(sp => new ManualWorkloadProvider(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ManualWorkloadProvider>.Instance));
        var container = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        return (container.GetRequiredService<IWorkloadProvider>(), container);
    }

    [Fact]
    public async Task Live_is_ready_and_provider_ready()
    {
        var asked = new List<Guid>();
        var (provider, container) = Build((id, _) =>
        {
            asked.Add(id);
            return ValueTask.FromResult(true);
        });
        using var owned = container;

        var status = await provider.GetStatusAsync(Reference);

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
        asked.Should().Equal([Reference.InstanceId], "the port is asked about this instance and nothing else");
    }

    [Fact]
    public async Task Not_live_is_pending_and_not_provider_ready()
    {
        var (provider, container) = Build((_, _) => ValueTask.FromResult(false));
        using var owned = container;

        var status = await provider.GetStatusAsync(Reference);

        status.Phase.Should().Be(WorkloadProviderPhase.Pending,
            "manual mode cannot prove the runtime is absent, so nothing live is still coming up, never NotFound");
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public async Task A_throw_is_unknown_and_never_escapes()
    {
        var (provider, container) = Build((_, _) => throw new InvalidOperationException("liveness store unreadable"));
        using var owned = container;

        var status = await provider.GetStatusAsync(Reference);

        status.Phase.Should().Be(WorkloadProviderPhase.Unknown);
        status.ProviderReady.Should().BeFalse();
    }

    [Fact]
    public async Task The_callers_cancellation_propagates_and_is_not_read_as_a_fault()
    {
        var (provider, container) = Build((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        });
        using var owned = container;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => provider.GetStatusAsync(Reference, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Each_read_resolves_the_port_in_a_fresh_scope()
    {
        var created = 0;
        var services = new ServiceCollection();
        services.AddScoped<IWorkloadExternalLiveness>(_ =>
        {
            created++;
            return new ScopedLiveness((_, _) => ValueTask.FromResult(true));
        });
        services.AddSingleton<IWorkloadProvider>(sp => new ManualWorkloadProvider(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ManualWorkloadProvider>.Instance));
        using var container = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var provider = container.GetRequiredService<IWorkloadProvider>();

        await provider.GetStatusAsync(Reference);
        await provider.GetStatusAsync(Reference);

        created.Should().Be(2, "one port per read, not one per provider");
    }
}
