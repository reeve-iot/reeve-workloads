using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>One provider's answer to "could this backend use me right now?", consulted when the operator has
/// selected none. Ships WITH the provider package.</summary>
/// <remarks>
/// <para>
/// The chain is: the explicit selection, else the lowest <see cref="Priority"/> among REGISTERED providers
/// whose <see cref="IsAvailableAsync"/> says yes, else the first registered provider. A library that names no provider cannot end its chain on a fixed one, so a build that wants a guaranteed terminal default registers a provider whose probe is always available — which is what <c>Reeve.Workloads.Providers.Manual</c> does.
/// </para>
/// </remarks>
public interface IWorkloadProviderAutoDetect
{
    /// <summary>The provider this probe answers for. Ignored unless that provider is also registered as an
    /// <see cref="IWorkloadProvider"/>: auto-detect never selects something nothing can run.</summary>
    WorkloadProviderId Provider { get; }

    /// <summary>Lower wins. In-cluster Kubernetes is <see cref="InClusterPriority"/> and a docker socket
    /// <see cref="LocalRuntimePriority"/>. Ties break on the provider id, so a
    /// chain's outcome does not depend on DI registration order.</summary>
    int Priority { get; }

    /// <summary>Whether this provider looks usable, given its own (decrypted) settings entry — an
    /// <see cref="JsonValueKind.Undefined"/> element when the operator has configured none.</summary>
    /// <remarks>May do I/O (the Docker provider pings the daemon here). Must not throw for an unreachable runtime:
    /// an unavailable answer lets the chain move on, an exception takes the whole settings read down.</remarks>
    Task<bool> IsAvailableAsync(JsonElement providerSettings, CancellationToken cancellationToken = default);

    /// <summary>The precedence of "this backend is running INSIDE the runtime it would deploy to", which beats
    /// every local-runtime probe: from inside a cluster no docker socket is looked for.</summary>
    public const int InClusterPriority = 100;

    /// <summary>The precedence of "a runtime is reachable from this host" — a daemon socket, a local API.</summary>
    public const int LocalRuntimePriority = 200;

    /// <summary>The precedence of a provider that is always available because it orchestrates nothing. Last by
    /// construction, so it can only ever be the fall-through.</summary>
    public const int FallbackPriority = int.MaxValue;
}
