namespace Reeve.Workloads.Abstractions;

/// <summary>How loudly a workload diagnostic reads. Declared here rather than reused from the host's own
/// severity enum, so the library never depends on which diagnostics product a host happens to ship.</summary>
public enum WorkloadDiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Critical = 3,
}

/// <summary>One thing the workload machinery has to say about an instance. <see cref="Code"/> is the identity:
/// a re-raise of the same code refreshes the existing item rather than stacking a second one.</summary>
public sealed record WorkloadDiagnostic
{
    public required string Code { get; init; }

    public required WorkloadDiagnosticSeverity Severity { get; init; }

    public required string Message { get; init; }

    /// <summary>Operator-facing remedy, in Markdown. The host sanitises it before rendering.</summary>
    public string? FixHint { get; init; }

    /// <summary>An automated repair the host can offer for this diagnostic, if it has one.</summary>
    public string? RepairFlowId { get; init; }

    /// <summary>Structured detail for the host to render or index. Hosts may cap the serialised size.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>The diagnostics of ONE workload instance: raised and cleared by code, so the machinery states the
/// current condition and the host decides what an operator sees.</summary>
public interface IWorkloadDiagnosticsSink
{
    Task RaiseAsync(WorkloadDiagnostic diagnostic, CancellationToken cancellationToken = default);

    /// <summary>Clears an open diagnostic; a no-op when none is open, never a throw.</summary>
    Task ClearAsync(string code, CancellationToken cancellationToken = default);
}

/// <summary>Binds a sink to an instance. <see cref="Prune"/> exists because the scanners are singletons that
/// would otherwise hold a sink per instance ever seen, across uninstalls, for the life of the process.</summary>
public interface IWorkloadDiagnosticsSinkFactory
{
    IWorkloadDiagnosticsSink Create(Guid instanceId);

    void Prune(IEnumerable<Guid> liveInstanceIds);
}

/// <summary>A diagnostic belonging to no instance — the sources are gone, which is the point of the notice.
/// Separate from the sink because it is broadcast, not raised: there is no row for anyone to clear.</summary>
public interface IWorkloadDiagnosticsAnnouncer
{
    Task AnnounceAsync(WorkloadDiagnostic diagnostic, CancellationToken cancellationToken = default);
}

/// <summary>The shipped no-op sink factory: a host with no diagnostics surface registers this and the
/// machinery still states its conditions, to nobody.</summary>
public sealed class NoWorkloadDiagnosticsSinkFactory : IWorkloadDiagnosticsSinkFactory
{
    private static readonly IWorkloadDiagnosticsSink Sink = new NoSink();

    /// <inheritdoc />
    public IWorkloadDiagnosticsSink Create(Guid instanceId) => Sink;

    /// <inheritdoc />
    public void Prune(IEnumerable<Guid> liveInstanceIds)
    {
    }

    private sealed class NoSink : IWorkloadDiagnosticsSink
    {
        public Task RaiseAsync(WorkloadDiagnostic diagnostic, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ClearAsync(string code, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>The shipped no-op announcer.</summary>
public sealed class NoWorkloadDiagnosticsAnnouncer : IWorkloadDiagnosticsAnnouncer
{
    /// <inheritdoc />
    public Task AnnounceAsync(WorkloadDiagnostic diagnostic, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
