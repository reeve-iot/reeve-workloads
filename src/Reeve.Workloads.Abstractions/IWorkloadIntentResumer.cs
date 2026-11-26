namespace Reeve.Workloads.Abstractions;

/// <summary>Boot-time seam that resumes in-flight update intents. Called before the first drift pass, so
/// drift converges on the durably recorded target instead of "correcting" a mid-update HealthGate back.</summary>
public interface IWorkloadIntentResumer
{
    /// <summary>Resumes open intents so the next drift pass converges on their targets. Idempotent and
    /// best-effort — a fault here must not abort boot.</summary>
    Task ResumeOpenIntentsAsync(CancellationToken cancellationToken = default);
}

/// <summary>No-op <see cref="IWorkloadIntentResumer"/>: there is no update-intent store yet, so there is
/// nothing to resume. Registered by default until a DB-backed resumer replaces it.</summary>
public sealed class NullWorkloadIntentResumer : IWorkloadIntentResumer
{
    public Task ResumeOpenIntentsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
