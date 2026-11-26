using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core.Tests;

internal sealed class FakeWorkloadClusterBus : IWorkloadClusterBus
{
    public readonly List<(string Channel, object? Payload)> Published = new();

    public readonly RecordingReconcileTrigger LocalTrigger = new();

    private readonly Dictionary<string, Dictionary<Guid, Func<object?, CancellationToken, Task>>> handlers = new();

    private readonly Dictionary<string, Dictionary<Guid, Func<CancellationToken, Task>>> reconcilers = new();

    private IWorkloadReconcileNudge? nudge;

    public Task PublishAsync<T>(string channel, T payload, CancellationToken cancellationToken = default)
    {
        Published.Add((channel, payload));
        return Task.CompletedTask;
    }

    public IDisposable Subscribe<T>(
        string channel,
        Func<T, CancellationToken, Task> handler,
        Func<CancellationToken, Task>? onReconcile = null)
    {
        var token = Guid.NewGuid();
        if (!handlers.TryGetValue(channel, out var map))
        {
            map = new Dictionary<Guid, Func<object?, CancellationToken, Task>>();
            handlers[channel] = map;
        }

        map[token] = (payload, ct) => handler((T)payload!, ct);
        if (onReconcile is not null)
        {
            if (!reconcilers.TryGetValue(channel, out var onReconnect))
            {
                onReconnect = new Dictionary<Guid, Func<CancellationToken, Task>>();
                reconcilers[channel] = onReconnect;
            }

            onReconnect[token] = onReconcile;
        }

        return new Sub(this, channel, token);
    }

    public async Task DeliverAsync<T>(string channel, T payload, CancellationToken cancellationToken = default)
    {
        if (!handlers.TryGetValue(channel, out var map))
        {
            return;
        }

        foreach (var h in map.Values.ToList())
        {
            await h(payload, cancellationToken);
        }
    }

    public async Task ReconnectAsync(string channel, CancellationToken cancellationToken = default)
    {
        if (!reconcilers.TryGetValue(channel, out var map))
        {
            return;
        }

        foreach (var reconcile in map.Values.ToList())
        {
            await reconcile(cancellationToken);
        }
    }

    public IWorkloadReconcileNudge Nudge => nudge ??= new WorkloadReconcileNudge(this, LocalTrigger);

    public int HandlerCount(string channel)
        => handlers.TryGetValue(channel, out var map) ? map.Count : 0;

    private sealed class Sub(FakeWorkloadClusterBus owner, string channel, Guid token) : IDisposable
    {
        public void Dispose()
        {
            if (owner.handlers.TryGetValue(channel, out var map))
            {
                map.Remove(token);
            }

            if (owner.reconcilers.TryGetValue(channel, out var onReconnect))
            {
                onReconnect.Remove(token);
            }
        }
    }
}

internal sealed class RecordingReconcileTrigger : IWorkloadReconcileTrigger
{
    public readonly List<(Guid? InstanceId, string Reason)> Requests = new();

    public Task RequestInstanceAsync(Guid instanceId, string reason, CancellationToken cancellationToken = default)
    {
        Requests.Add((instanceId, reason));
        return Task.CompletedTask;
    }

    public Task RequestAllAsync(string reason, CancellationToken cancellationToken = default)
    {
        Requests.Add((null, reason));
        return Task.CompletedTask;
    }
}
