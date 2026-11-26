using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>Indexes DI-registered <see cref="IWorkloadProvider"/> by id. <see cref="TryResolve"/> may
/// legitimately return <c>null</c> when none is registered — the reconciler must tolerate that gracefully.</summary>
public interface IWorkloadProviderRegistry
{
    /// <summary>Returns the provider for <paramref name="provider"/>, or <c>null</c> when none is registered.</summary>
    IWorkloadProvider? TryResolve(WorkloadProviderId provider);

    /// <summary>Throws <see cref="WorkloadProviderNotRegisteredException"/> when none is registered.
    /// Use only where a provider is a hard precondition; the reconcile loop should prefer <see cref="TryResolve"/> instead.</summary>
    IWorkloadProvider Resolve(WorkloadProviderId provider);

    /// <summary>The provider ids that have a registered implementation in this build.</summary>
    IReadOnlyCollection<WorkloadProviderId> RegisteredProviders { get; }

    /// <summary>Every registered provider's declared traits, in registration order.</summary>
    IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors { get; }

    /// <summary>The traits to treat <paramref name="provider"/> as having.</summary>
    /// <remarks>
    /// Total by design, because a stored row can name a provider this build does not have — a workload created
    /// under docker, read back by a Kubernetes-only deployment. Such an id is described by the descriptor
    /// DEFAULTS — the traits the library assumed everywhere before it asked. Nothing can act on that instance in
    /// this build anyway, and a first-party id is treated no differently from a third party's.
    /// </remarks>
    WorkloadProviderDescriptor Describe(WorkloadProviderId provider);
}

/// <summary>Thrown by <see cref="IWorkloadProviderRegistry.Resolve"/> when no implementation is registered for an id.</summary>
public sealed class WorkloadProviderNotRegisteredException : InvalidOperationException
{
    public WorkloadProviderNotRegisteredException(WorkloadProviderId provider)
        : base($"No IWorkloadProvider is registered for '{provider}' in this build.")
    {
        Provider = provider;
    }

    public WorkloadProviderId Provider { get; }
}
