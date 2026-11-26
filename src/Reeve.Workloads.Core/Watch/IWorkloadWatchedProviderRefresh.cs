using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Core.Watch;

/// <summary>Lets the reconciler keep the leader's watch on the providers live instances are bound to without
/// waiting for a leader restart or a settings change: a provider nothing live is bound to any more stops being
/// watched, and one an instance is newly bound to starts being watched.</summary>
internal interface IWorkloadWatchedProviderRefresh
{
    /// <summary>Re-resolves the watched providers when <paramref name="provider"/> has a source; otherwise does nothing.</summary>
    Task RefreshAfterTeardownAttemptAsync(WorkloadProviderId provider, CancellationToken cancellationToken);

    /// <summary>Re-resolves the watched providers when an instance is bound to <paramref name="provider"/>, which has
    /// a source, but the watched set lacks it or could not be resolved; otherwise does nothing.</summary>
    Task RefreshIfUnwatchedAsync(WorkloadProviderId provider, CancellationToken cancellationToken);

    /// <summary>Re-resolves the watched providers after the orchestrator setting changed on any replica.</summary>
    Task RefreshAfterSettingsChangeAsync(CancellationToken cancellationToken);
}
