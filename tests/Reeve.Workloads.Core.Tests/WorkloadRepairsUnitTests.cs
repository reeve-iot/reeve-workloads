using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadRepairsUnitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Registers_as_an_engine_unit_with_the_repairs_key()
    {
        var unit = Build(out _, out _);

        unit.Should().BeAssignableTo<IWorkloadEngineUnit>();
        ((IWorkloadEngineUnit)unit).Key.Should().Be(WorkloadRepairsUnit.Key);
        WorkloadRepairsUnit.Key.Should().Be("workload-repairs");
    }

    [Fact]
    public async Task StartAsync_runs_a_scan_pass_that_escalates_a_stuck_instance()
    {
        var unit = Build(out var repository, out var registries);
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "zwave", "wl-a", T0);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, T0);
        instance.TransitionTo(WorkloadInstanceState.Failed, WorkloadStateReason.HealthLost, null, T0);
        repository.Add(instance);

        await unit.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => registries.For(instance.GroupInstanceId).RaisedCodes.Count > 0, TimeSpan.FromSeconds(5));
        }
        finally
        {
            await unit.StopAsync(CancellationToken.None);
        }

        registries.For(instance.GroupInstanceId).RaisedCodes
            .Should().Contain(WorkloadRepairsScanner.StuckCode(instance.WorkloadId));
    }

    [Fact]
    public async Task StopAsync_is_idempotent_and_safe_before_start()
    {
        var unit = Build(out _, out _);

        await unit.StopAsync(CancellationToken.None);
        await unit.StartAsync(CancellationToken.None);
        await unit.StopAsync(CancellationToken.None);
        await unit.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_is_idempotent()
    {
        var unit = Build(out _, out _);

        await unit.StartAsync(CancellationToken.None);
        await unit.StartAsync(CancellationToken.None);
        await unit.StopAsync(CancellationToken.None);
    }

    private static WorkloadRepairsUnit Build(out InMemoryRepository repository, out SpyRegistryFactory registries)
    {
        var clock = new FixedClock(T0.AddMinutes(30));
        repository = new InMemoryRepository();
        registries = new SpyRegistryFactory();

        var services = new ServiceCollection();
        var repo = repository;
        services.AddScoped<IWorkloadInstanceRepository>(_ => repo);
        services.AddScoped<IWorkloadUpdateIntentRepository>(_ => new EmptyIntentRepository());
        var provider = services.BuildServiceProvider();

        var options = new WorkloadRepairsOptions { ScanCadenceSeconds = 1 };
        var scanner = new WorkloadRepairsScanner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            registries,
            Reeve.Workloads.Core.Tests.ProviderRegistryFakes.FirstPartyRegistry(),
            clock,
            Options.Create(options),
            NullLogger<WorkloadRepairsScanner>.Instance);

        return new WorkloadRepairsUnit(scanner, Options.Create(options), NullLogger<WorkloadRepairsUnit>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(20);
        }
        condition().Should().BeTrue("the condition should have been met within the timeout");
    }

    private sealed class InMemoryRepository : IWorkloadInstanceRepository
    {
        private readonly Dictionary<Guid, WorkloadInstance> store = new();
        public void Add(WorkloadInstance instance) => store[instance.Id] = instance;
        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.ToList());
        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(store.TryGetValue(id, out var i) ? i : null);
        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadInstance?>(null);
        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(Array.Empty<WorkloadInstance>());
        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.Where(i => i.State == state).ToList());
        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { Add(instance); return Task.CompletedTask; }
        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { store.Remove(instance.Id); return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(
                store.Values.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count()));
    }

    private sealed class EmptyIntentRepository : IWorkloadUpdateIntentRepository
    {
        public Task<WorkloadUpdateIntent?> GetLatestForInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadUpdateIntent?>(null);
        public Task<WorkloadUpdateIntent?> GetActiveForInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadUpdateIntent?>(null);
        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetOpenIntentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>(Array.Empty<WorkloadUpdateIntent>());
        public Task<WorkloadUpdateIntent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadUpdateIntent?>(null);
        public Task<IReadOnlyList<WorkloadUpdateIntent>> GetRecentForInstanceAsync(Guid pluginInstanceId, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadUpdateIntent>>(Array.Empty<WorkloadUpdateIntent>());
        public Task AddAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class SpyRegistryFactory : IWorkloadDiagnosticsSinkFactory
    {
        private readonly Dictionary<Guid, SpyRegistry> registries = new();
        public IWorkloadDiagnosticsSink Create(Guid instanceId) => For(instanceId);
        public SpyRegistry For(Guid pluginInstanceId)
        {
            lock (registries)
            {
                if (!registries.TryGetValue(pluginInstanceId, out var r))
                {
                    r = new SpyRegistry();
                    registries[pluginInstanceId] = r;
                }
                return r;
            }
        }

        public void Prune(IEnumerable<Guid> liveInstanceIds)
        {
            var live = liveInstanceIds.ToHashSet();
            lock (registries)
            {
                foreach (var id in registries.Keys.ToList())
                {
                    if (!live.Contains(id))
                    {
                        registries.Remove(id);
                    }
                }
            }
        }
    }

    public sealed class SpyRegistry : IWorkloadDiagnosticsSink
    {
        private readonly List<string> raised = new();
        private readonly List<string> cleared = new();

        public IReadOnlyList<string> RaisedCodes { get { lock (raised) { return raised.ToList(); } } }
        public IReadOnlyList<string> ClearedCodes { get { lock (cleared) { return cleared.ToList(); } } }

        public Task RaiseAsync(WorkloadDiagnostic diagnostic, CancellationToken cancellationToken = default)
        {
            lock (raised) { raised.Add(diagnostic.Code); }
            return Task.CompletedTask;
        }

        public Task ClearAsync(string code, CancellationToken cancellationToken = default)
        {
            lock (cleared) { cleared.Add(code); }
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
