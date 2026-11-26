using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Core.Watch;

internal sealed class WorkloadWatchUnit : IWorkloadEngineUnit, IWorkloadWatchSink, IWorkloadWatchedProviderRefresh
{
    public const string Key = "workload-watcher";

    private const string InstanceReason = WorkloadReconcileReasons.Watch;
    private const string AllReason = WorkloadReconcileReasons.WatchResync;

    private readonly IWorkloadReconcileTrigger trigger;
    private readonly IReadOnlyList<IWorkloadWatchSource> sources;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly WorkloadReconcilerOptions reconcilerOptions;
    private readonly WorkloadWatchOptions watchOptions;
    private readonly ILogger<WorkloadWatchUnit> logger;
    private readonly ILogger<WorkloadWatchCoalescer> coalescerLogger;

    private readonly Func<TimeSpan, CancellationToken, Task>? debounceDelay;

    private volatile WorkloadWatchCoalescer? coalescer;
    private CancellationTokenSource? cts;
    private readonly List<IWorkloadWatchSource> startedSources = new();

    private bool started;

    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private volatile IReadOnlySet<WorkloadProviderId>? watchedProviders;

    public WorkloadWatchUnit(
        IWorkloadReconcileTrigger trigger,
        IEnumerable<IWorkloadWatchSource> sources,
        IServiceScopeFactory scopeFactory,
        IOptions<WorkloadReconcilerOptions> reconcilerOptions,
        IOptions<WorkloadWatchOptions> watchOptions,
        ILogger<WorkloadWatchUnit> logger,
        ILogger<WorkloadWatchCoalescer> coalescerLogger)
        : this(trigger, sources, scopeFactory, reconcilerOptions, watchOptions, logger, coalescerLogger, debounceDelay: null)
    {
    }

    internal WorkloadWatchUnit(
        IWorkloadReconcileTrigger trigger,
        IEnumerable<IWorkloadWatchSource> sources,
        IServiceScopeFactory scopeFactory,
        IOptions<WorkloadReconcilerOptions> reconcilerOptions,
        IOptions<WorkloadWatchOptions> watchOptions,
        ILogger<WorkloadWatchUnit> logger,
        ILogger<WorkloadWatchCoalescer> coalescerLogger,
        Func<TimeSpan, CancellationToken, Task>? debounceDelay)
    {
        this.trigger = trigger;
        this.sources = sources as IReadOnlyList<IWorkloadWatchSource> ?? sources.ToList();
        this.scopeFactory = scopeFactory;
        this.reconcilerOptions = reconcilerOptions.Value;
        this.watchOptions = watchOptions.Value;
        this.logger = logger;
        this.coalescerLogger = coalescerLogger;
        this.debounceDelay = debounceDelay;
    }

    string IWorkloadEngineUnit.Key => Key;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    internal Task RestartIfProviderMovedAsync(CancellationToken cancellationToken)
        => RestartIfWatchedSetMovedAsync(_ => true, cancellationToken);

    public Task RefreshAfterSettingsChangeAsync(CancellationToken cancellationToken)
        => RestartIfProviderMovedAsync(cancellationToken);

    public Task RefreshAfterTeardownAttemptAsync(WorkloadProviderId provider, CancellationToken cancellationToken)
        => RestartIfWatchedSetMovedAsync(
            watched => watched is not null && WithSource(watched).Contains(provider), cancellationToken);

    public Task RefreshIfUnwatchedAsync(WorkloadProviderId provider, CancellationToken cancellationToken)
    {
        if (!reconcilerOptions.WatchEnabled || watchedProviders?.Contains(provider) == true || !HasSource(provider))
        {
            return Task.CompletedTask;
        }

        return RestartIfWatchedSetMovedAsync(
            watched => watched is null || !watched.Contains(provider), cancellationToken);
    }

    private bool HasSource(WorkloadProviderId provider) => sources.Any(source => source.Provider == provider);

    private async Task RestartIfWatchedSetMovedAsync(
        Func<IReadOnlySet<WorkloadProviderId>?, bool> shouldResolve, CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!started || cts is null || cts.IsCancellationRequested || !reconcilerOptions.WatchEnabled)
            {
                return;
            }

            if (!shouldResolve(watchedProviders))
            {
                return;
            }

            var token = cts.Token;
            var providers = await ResolveWatchedProvidersAsync(token).ConfigureAwait(false);
            if (providers is null || token.IsCancellationRequested)
            {
                return;
            }

            if (watchedProviders is not null && WithSource(watchedProviders).SetEquals(WithSource(providers)))
            {
                watchedProviders = providers;
                return;
            }

            logger.LogInformation(
                "Workload watch moves from provider(s) {Previous} to {Watched}: the effective provider, or the providers live instances are bound to, now need a different set of watch sources.",
                Describe(watchedProviders), Describe(providers));
            await StopSourcesAsync(CancellationToken.None).ConfigureAwait(false);
            await StartSourcesAsync(providers, token).ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (started)
        {
            return;
        }

        if (!reconcilerOptions.WatchEnabled)
        {
            started = true;
            logger.LogInformation("Workload watcher started INERT (WatchEnabled=false) — poll-only, no watch sources hosted.");
            return;
        }

        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;

        coalescer = new WorkloadWatchCoalescer(
            watchOptions.DebounceWindow,
            reconcileInstance: (id, ct) => trigger.RequestInstanceAsync(id, InstanceReason, ct),
            reconcileAll: ct => trigger.RequestAllAsync(AllReason, ct),
            coalescerLogger,
            debounceDelay,
            token);

        started = true;

        var providers = await ResolveWatchedProvidersAsync(token).ConfigureAwait(false);
        if (token.IsCancellationRequested)
        {
            return;
        }

        await StartSourcesAsync(providers, token).ConfigureAwait(false);

        logger.LogInformation(
            "Workload watcher started (watched provider(s) {Watched}, {SourceCount} source(s) hosted, debounce {Debounce}).",
            Describe(providers), startedSources.Count, watchOptions.DebounceWindow);
    }

    private HashSet<WorkloadProviderId> WithSource(IReadOnlySet<WorkloadProviderId> providers)
        => [.. sources.Select(source => source.Provider).Where(providers.Contains)];

    private static string Describe(IReadOnlySet<WorkloadProviderId>? providers)
        => providers is null ? "unresolved" : providers.Count == 0 ? "none" : string.Join(", ", providers.Order());

    private async Task StartSourcesAsync(IReadOnlySet<WorkloadProviderId>? providers, CancellationToken token)
    {
        watchedProviders = providers;
        foreach (var source in sources)
        {
            if (providers is null || !providers.Contains(source.Provider))
            {
                logger.LogDebug(
                    "Workload watch source for provider {Provider} NOT started (watched provider(s) {Watched}) — poll-only for it.",
                    source.Provider, Describe(providers));
                continue;
            }

            try
            {
                await source.StartAsync(this, token).ConfigureAwait(false);
                startedSources.Add(source);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Workload watch source for provider {Provider} failed to start; the periodic reconcile pass covers it.",
                    source.Provider);
            }
        }
    }

    private async Task StopSourcesAsync(CancellationToken cancellationToken)
    {
        foreach (var source in startedSources)
        {
            try
            {
                await source.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workload watch source for provider {Provider} threw on stop; continuing.", source.Provider);
            }
        }

        startedSources.Clear();
    }

    private async Task<IReadOnlySet<WorkloadProviderId>?> ResolveWatchedProvidersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using var scopeLifetime = scope.ConfigureAwait(false);
            var settings = scope.ServiceProvider.GetRequiredService<IWorkloadOrchestratorSettingsService>();
            var response = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
            var instances = await scope.ServiceProvider.GetRequiredService<IWorkloadInstanceRepository>()
                .ListAllAsync(cancellationToken).ConfigureAwait(false);
            var providers = new HashSet<WorkloadProviderId> { response.EffectiveProvider };
            providers.UnionWith(instances
                .Where(instance => instance.State != WorkloadInstanceState.Terminating)
                .Select(instance => instance.Provider)
                .OfType<WorkloadProviderId>());
            return providers;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Resolving the workload providers to watch (the effective one and those live instances are bound to) failed; the watched set is left unchanged and resolved again on the next settings change or reconcile of a bound instance (the periodic reconcile pass covers the gap).");
            return null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (!started)
        {
            return;
        }

        started = false;
        watchedProviders = null;

        await StopSourcesAsync(cancellationToken).ConfigureAwait(false);

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        var toDispose = coalescer;
        coalescer = null;
        if (toDispose is not null)
        {
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }

        cts?.Dispose();
        cts = null;
    }

    void IWorkloadWatchSink.InstanceDirty(Guid instanceId) => coalescer?.MarkDirty(instanceId);

    void IWorkloadWatchSink.AllDirty() => coalescer?.MarkAllDirty();

    void IWorkloadWatchSink.ReportHealth(WorkloadProviderId provider, WorkloadWatchHealth health)
        => logger.LogDebug("Workload watch for provider {Provider} reports {Health}.", provider, health);

    internal IWorkloadWatchSink Sink => this;

    internal Task WaitForNextDrainAsync(TimeSpan timeout)
    {
        var c = coalescer;
        if (c is null)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler()
        {
            c.CycleDrained -= Handler;
            tcs.TrySetResult();
        }

        c.CycleDrained += Handler;
        return tcs.Task.WaitAsync(timeout);
    }
}
