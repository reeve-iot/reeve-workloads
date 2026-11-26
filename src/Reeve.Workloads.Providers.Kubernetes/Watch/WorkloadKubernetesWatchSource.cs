using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Core.Watch;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Providers.Kubernetes.Watch;

internal sealed class WorkloadKubernetesWatchSource : IWorkloadWatchSource
{
    internal static readonly TimeSpan InitialReconnectBackoff = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan MaxReconnectBackoff = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan FingerprintPollInterval = TimeSpan.FromSeconds(30);

    private static readonly string ManagedWorkloadSelector = WorkloadResourceLabels.InstanceLabelKey;

    private readonly IKubernetesClientFactory clientFactory;
    private readonly IKubernetesObjectClient objectClient;
    private readonly Func<CancellationToken, Task<string?>> resolveNamespaceAsync;
    private readonly ILogger<WorkloadKubernetesWatchSource> logger;
    private readonly Func<TimeSpan, CancellationToken, Task> backoffDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> pollDelay;

    private readonly object gate = new();
    private CancellationTokenSource? cts;
    private Task? loop;
    private bool started;

    public WorkloadKubernetesWatchSource(
        IKubernetesClientFactory clientFactory,
        IKubernetesObjectClient objectClient,
        IServiceScopeFactory scopeFactory,
        KubernetesNamespaceResolver namespaceResolver,
        IOptions<WorkloadWatchOptions> watchOptions,
        ILogger<WorkloadKubernetesWatchSource> logger)
        : this(
            clientFactory,
            objectClient,
            ct => ResolveNamespaceFromSettingsAsync(scopeFactory, namespaceResolver, ct),
            (watchOptions ?? throw new ArgumentNullException(nameof(watchOptions))).Value,
            logger,
            backoffDelay: null,
            pollDelay: null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(namespaceResolver);
    }

    internal WorkloadKubernetesWatchSource(
        IKubernetesClientFactory clientFactory,
        IKubernetesObjectClient objectClient,
        Func<CancellationToken, Task<string?>> resolveNamespaceAsync,
        WorkloadWatchOptions watchOptions,
        ILogger<WorkloadKubernetesWatchSource> logger,
        Func<TimeSpan, CancellationToken, Task>? backoffDelay,
        Func<TimeSpan, CancellationToken, Task>? pollDelay)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.objectClient = objectClient ?? throw new ArgumentNullException(nameof(objectClient));
        this.resolveNamespaceAsync = resolveNamespaceAsync ?? throw new ArgumentNullException(nameof(resolveNamespaceAsync));
        _ = watchOptions ?? throw new ArgumentNullException(nameof(watchOptions));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.backoffDelay = backoffDelay ?? ((ts, ct) => Task.Delay(ts, ct));
        this.pollDelay = pollDelay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    public WorkloadProviderId Provider => KubernetesProvider.Id;

    public Task StartAsync(IWorkloadWatchSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (gate)
        {
            if (started)
            {
                return Task.CompletedTask;
            }

            started = true;
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = cts.Token;

            loop = Task.Run(() => RunAsync(sink, token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? toAwait;
        CancellationTokenSource? toCancel;
        lock (gate)
        {
            if (!started)
            {
                return;
            }

            started = false;
            toCancel = cts;
            cts = null;
            toAwait = loop;
            loop = null;
        }

        if (toCancel is not null)
        {
            await toCancel.CancelAsync().ConfigureAwait(false);
        }

        if (toAwait is not null)
        {
            try
            {
                await toAwait.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Kubernetes workload watch loop threw on shutdown (ignored).");
            }
        }

        toCancel?.Dispose();
    }

    private async Task RunAsync(IWorkloadWatchSink sink, CancellationToken stopToken)
    {
        var backoff = InitialReconnectBackoff;

        while (!stopToken.IsCancellationRequested)
        {
            SessionResult result;
            try
            {
                result = await RunSessionAsync(sink, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Kubernetes workload watch session threw unexpectedly; degrading to poll.");
                result = new SessionResult(SessionOutcome.Fault, Connected: false);
            }

            switch (result.Outcome)
            {
                case SessionOutcome.Stopped:
                    return;

                case SessionOutcome.Resync:
                    backoff = InitialReconnectBackoff;
                    continue;

                case SessionOutcome.FingerprintChanged:
                    backoff = InitialReconnectBackoff;
                    continue;

                case SessionOutcome.Fault:
                default:
                    if (result.Connected)
                    {
                        backoff = InitialReconnectBackoff;
                    }

                    sink.ReportHealth(Provider, WorkloadWatchHealth.Degraded);
                    await SafeDelayAsync(backoffDelay, backoff, stopToken).ConfigureAwait(false);
                    backoff = NextBackoff(backoff);
                    continue;
            }
        }
    }

    private async Task<SessionResult> RunSessionAsync(IWorkloadWatchSink sink, CancellationToken stopToken)
    {
        string? @namespace;
        try
        {
            @namespace = await resolveNamespaceAsync(stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            return new SessionResult(SessionOutcome.Stopped, Connected: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve the workloads namespace for the Kubernetes watch; degrading to poll.");
            return new SessionResult(SessionOutcome.Fault, Connected: false);
        }

        if (string.IsNullOrWhiteSpace(@namespace))
        {
            logger.LogWarning("Workloads namespace not configured; the Kubernetes watch degrades to poll until it is set.");
            return new SessionResult(SessionOutcome.Fault, Connected: false);
        }

        IKubernetes client;
        try
        {
            client = await clientFactory.CreateClientAsync(stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            return new SessionResult(SessionOutcome.Stopped, Connected: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not build the Kubernetes API client for the workload watch; degrading to poll.");
            return new SessionResult(SessionOutcome.Fault, Connected: false);
        }

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);

        var watchdog = MonitorConnectionAsync(client, @namespace!, sessionCts, stopToken);

        var connected = false;

        try
        {
            await foreach (var ev in objectClient
                .WatchWorkloadsAsync(client, @namespace!, ManagedWorkloadSelector, sessionCts.Token)
                .ConfigureAwait(false))
            {
                switch (ev.Type)
                {
                    case WorkloadWatchEventType.Synced:
                        connected = true;
                        sink.ReportHealth(Provider, WorkloadWatchHealth.Connected);
                        break;

                    case WorkloadWatchEventType.InstanceChanged:
                        if (ev.InstanceId is Guid instanceId)
                        {
                            sink.InstanceDirty(instanceId);
                        }

                        break;
                }
            }

            return new SessionResult(SessionOutcome.Fault, connected);
        }
        catch (WorkloadWatchResourceVersionTooOldException)
        {
            logger.LogInformation("Kubernetes workload watch resourceVersion expired (410); re-listing and reconciling all, then resuming.");
            sink.AllDirty();
            return new SessionResult(SessionOutcome.Resync, connected);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            return new SessionResult(SessionOutcome.Stopped, connected);
        }
        catch (OperationCanceledException) when (sessionCts.IsCancellationRequested)
        {
            return new SessionResult(SessionOutcome.FingerprintChanged, connected);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kubernetes workload watch stream faulted; degrading to poll and reconnecting.");
            return new SessionResult(SessionOutcome.Fault, connected);
        }
        finally
        {
            if (!sessionCts.IsCancellationRequested)
            {
                sessionCts.Cancel();
            }

            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task MonitorConnectionAsync(
        IKubernetes current,
        string currentNamespace,
        CancellationTokenSource sessionCts,
        CancellationToken stopToken)
    {
        try
        {
            while (!sessionCts.IsCancellationRequested && !stopToken.IsCancellationRequested)
            {
                await pollDelay(FingerprintPollInterval, sessionCts.Token).ConfigureAwait(false);
                if (sessionCts.IsCancellationRequested || stopToken.IsCancellationRequested)
                {
                    return;
                }

                IKubernetes latest;
                string? latestNamespace;
                try
                {
                    latest = await clientFactory.CreateClientAsync(sessionCts.Token).ConfigureAwait(false);
                    latestNamespace = await resolveNamespaceAsync(sessionCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Fingerprint watchdog could not re-resolve the Kubernetes client or namespace; keeping the current watch.");
                    continue;
                }

                if (!ReferenceEquals(latest, current))
                {
                    logger.LogInformation("Kubernetes connection fingerprint changed; restarting the workload watch against the new endpoint.");
                    sessionCts.Cancel();
                    return;
                }

                if (!string.Equals(latestNamespace, currentNamespace, StringComparison.Ordinal))
                {
                    logger.LogInformation("Kubernetes workloads namespace changed; restarting the workload watch in the new namespace.");
                    sessionCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static TimeSpan NextBackoff(TimeSpan current)
    {
        var doubled = TimeSpan.FromTicks(current.Ticks * 2);
        return doubled < MaxReconnectBackoff ? doubled : MaxReconnectBackoff;
    }

    private static async Task SafeDelayAsync(Func<TimeSpan, CancellationToken, Task> delay, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            await delay(duration, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<string?> ResolveNamespaceFromSettingsAsync(
        IServiceScopeFactory scopeFactory, KubernetesNamespaceResolver namespaceResolver, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IWorkloadOrchestratorSecretReader>();
        var settings = await settingsService.GetDecryptedSettingsAsync(ct).ConfigureAwait(false);
        return namespaceResolver.Resolve(settings);
    }

    private readonly record struct SessionResult(SessionOutcome Outcome, bool Connected);

    private enum SessionOutcome
    {
        Stopped,

        Resync,

        FingerprintChanged,

        Fault,
    }
}
