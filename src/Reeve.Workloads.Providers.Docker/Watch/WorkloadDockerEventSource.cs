using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Core.Watch;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.Watch;

internal interface IDockerEventStream
{
    Task MonitorAsync(string? socketUri, IProgress<Message> events, CancellationToken cancellationToken);
}

internal sealed class WorkloadDockerEventSource : IWorkloadWatchSource
{
    private static readonly TimeSpan BaseReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ConnectSettleInterval = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan SocketPollInterval = TimeSpan.FromSeconds(30);

    private readonly IDockerEventStream _stream;
    private readonly Func<CancellationToken, Task<string?>> _resolveSocketUri;
    private readonly Func<TimeSpan, CancellationToken, Task> _reconnectDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _socketPollDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _connectSettleDelay;
    private readonly ILogger<WorkloadDockerEventSource> _logger;

    private readonly object _gate = new();
    private IWorkloadWatchSink? _sink;
    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private bool _started;

    public WorkloadDockerEventSource(
        IDockerClientFactory clientFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<WorkloadDockerEventSource> logger)
        : this(
            new DockerDaemonEventStream(clientFactory),
            BuildSocketResolver(scopeFactory),
            Task.Delay,
            Task.Delay,
            Task.Delay,
            logger)
    {
    }

    internal WorkloadDockerEventSource(
        IDockerEventStream stream,
        Func<CancellationToken, Task<string?>> resolveSocketUri,
        Func<TimeSpan, CancellationToken, Task> reconnectDelay,
        Func<TimeSpan, CancellationToken, Task> socketPollDelay,
        Func<TimeSpan, CancellationToken, Task> connectSettleDelay,
        ILogger<WorkloadDockerEventSource> logger)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _resolveSocketUri = resolveSocketUri ?? throw new ArgumentNullException(nameof(resolveSocketUri));
        _reconnectDelay = reconnectDelay ?? throw new ArgumentNullException(nameof(reconnectDelay));
        _socketPollDelay = socketPollDelay ?? throw new ArgumentNullException(nameof(socketPollDelay));
        _connectSettleDelay = connectSettleDelay ?? throw new ArgumentNullException(nameof(connectSettleDelay));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WorkloadProviderId Provider => DockerProvider.Id;

    public Task StartAsync(IWorkloadWatchSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_gate)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
            _sink = sink;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            _runLoop = Task.Run(() => RunAsync(sink, token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            loop = _runLoop;
            cts = _cts;
            _runLoop = null;
            _cts = null;
            _sink = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Docker workload events loop completed with an exception on stop.");
            }
        }

        cts?.Dispose();
    }

    private async Task RunAsync(IWorkloadWatchSink sink, CancellationToken stopToken)
    {
        var failedAttempts = 0;

        while (!stopToken.IsCancellationRequested)
        {
            var socketUri = await ResolveSocketUriSafeAsync(stopToken).ConfigureAwait(false);
            var reconnectImmediately = false;
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);

            var connectedFlag = new StrongBox<int>(0);
            void MarkConnected()
            {
                if (Interlocked.Exchange(ref connectedFlag.Value, 1) == 0)
                {
                    sink.ReportHealth(DockerProvider.Id, WorkloadWatchHealth.Connected);
                }
            }

            var progress = new EventRelay(m => { MarkConnected(); HandleEvent(sink, m); });
            var settleWatch = Task.CompletedTask;

            try
            {
                var streamTask = _stream.MonitorAsync(socketUri, progress, attemptCts.Token);

                settleWatch = SettleAsync(MarkConnected, attemptCts.Token);

                while (true)
                {
                    var pollTick = _socketPollDelay(SocketPollInterval, attemptCts.Token);
                    var completed = await Task.WhenAny(streamTask, pollTick).ConfigureAwait(false);
                    if (completed == streamTask)
                    {
                        break;
                    }

                    await pollTick.ConfigureAwait(false);
                    var current = await ResolveSocketUriSafeAsync(attemptCts.Token).ConfigureAwait(false);
                    if (!SocketEquals(current, socketUri))
                    {
                        reconnectImmediately = true;
                        await attemptCts.CancelAsync().ConfigureAwait(false);
                        break;
                    }
                }

                await streamTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                reconnectImmediately = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Docker workload events stream ended unexpectedly; degrading to poll and reconnecting.");
            }
            finally
            {
                if (!attemptCts.IsCancellationRequested)
                {
                    await attemptCts.CancelAsync().ConfigureAwait(false);
                }

                try
                {
                    await settleWatch.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            sink.ReportHealth(DockerProvider.Id, WorkloadWatchHealth.Degraded);

            if (stopToken.IsCancellationRequested)
            {
                break;
            }

            if (Volatile.Read(ref connectedFlag.Value) == 1)
            {
                failedAttempts = 0;
            }

            if (reconnectImmediately)
            {
                continue;
            }

            failedAttempts++;
            try
            {
                await _reconnectDelay(ComputeBackoff(failedAttempts), stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SettleAsync(Action markConnected, CancellationToken attemptToken)
    {
        try
        {
            await _connectSettleDelay(ConnectSettleInterval, attemptToken).ConfigureAwait(false);
            markConnected();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<string?> ResolveSocketUriSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _resolveSocketUri(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the docker socket URI for the events watch; using the platform default.");
            return null;
        }
    }

    private void HandleEvent(IWorkloadWatchSink sink, Message message)
    {
        if (message is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(message.Type) && !message.Type.Equals("container", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IsRelevantAction(message.Action))
        {
            return;
        }

        var attributes = message.Actor?.Attributes;
        if (attributes is null || !attributes.TryGetValue(DockerProviderConstants.InstanceLabelKey, out var raw))
        {
            return;
        }

        if (!Guid.TryParse(raw, out var instanceId))
        {
            return;
        }

        sink.InstanceDirty(instanceId);
    }

    private static bool IsRelevantAction(string? action)
    {
        if (string.IsNullOrEmpty(action))
        {
            return false;
        }

        if (action.StartsWith("health_status", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return action.Equals("die", StringComparison.OrdinalIgnoreCase)
            || action.Equals("stop", StringComparison.OrdinalIgnoreCase)
            || action.Equals("destroy", StringComparison.OrdinalIgnoreCase)
            || action.Equals("oom", StringComparison.OrdinalIgnoreCase)
            || action.Equals("start", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan ComputeBackoff(int failedAttempts)
    {
        var exponent = Math.Min(failedAttempts - 1, 30);
        var ticks = BaseReconnectDelay.Ticks * (1L << exponent);
        return ticks <= 0 || ticks > MaxReconnectDelay.Ticks ? MaxReconnectDelay : TimeSpan.FromTicks(ticks);
    }

    private static bool SocketEquals(string? a, string? b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static string Normalize(string? socketUri)
        => string.IsNullOrWhiteSpace(socketUri) ? string.Empty : socketUri.Trim();

    private static Func<CancellationToken, Task<string?>> BuildSocketResolver(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        return async cancellationToken =>
        {
            using var scope = scopeFactory.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<IWorkloadOrchestratorSettingsService>();
            var response = await settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
            return DockerProviderSettings.From(response.Settings).SocketUri;
        };
    }

    private sealed class EventRelay(Action<Message> onReport) : IProgress<Message>
    {
        public void Report(Message value) => onReport(value);
    }

    private sealed class DockerDaemonEventStream(IDockerClientFactory clientFactory) : IDockerEventStream
    {
        private readonly IDockerClientFactory _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));

        public async Task MonitorAsync(string? socketUri, IProgress<Message> events, CancellationToken cancellationToken)
        {
            var client = _clientFactory.CreateClient(socketUri);

            var parameters = new ContainerEventsParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["type"] = new Dictionary<string, bool> { ["container"] = true },
                    ["label"] = new Dictionary<string, bool> { [DockerProviderConstants.InstanceLabelKey] = true },
                },
            };

            await client.System.MonitorEventsAsync(parameters, events, cancellationToken).ConfigureAwait(false);
        }
    }
}
