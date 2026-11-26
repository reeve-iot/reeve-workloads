using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Abstractions;

/// <summary>Everything the reconcile engine needs to compute an instance's desired spec, as a pure DATA carrier.
/// A <c>null</c> from the provider (not this record) means the workload is no longer declared.</summary>
public sealed record WorkloadReconcileInputs
{
    /// <summary>The declared manifest workload for the instance's <c>(pluginType, workloadId)</c>.</summary>
    public required WorkloadManifest WorkloadManifest { get; init; }

    /// <summary>The resolved install-time parameter values — the instance's <c>ParametersJson</c>, decoded.</summary>
    public required IReadOnlyDictionary<string, string?> ResolvedParameters { get; init; }

    /// <summary>Original password references used only as desired-spec hash inputs.</summary>
    public IReadOnlyDictionary<string, string?> ParameterReferencesForHash { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>Unpinned password reference values keyed by parameter name, as identified by the resolver.
    /// Used only to prevent provider-side rotation from applying without explicit operator approval.</summary>
    public IReadOnlyDictionary<string, string?> UnpinnedParameterReferences { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>The current orchestrator settings: mirrors, node-state root, reconciliation pause and so on.</summary>
    public required WorkloadOrchestratorSettings Settings { get; init; }

    /// <summary>The backend's own product-image configuration, resolving a bundled workload's <c>image.component</c>
    /// at render. Defaults to an empty catalog, so a <c>component</c> then fail-closes.</summary>
    public ProductImageOptions ProductImages { get; init; } = new();

    /// <summary>The resolved bootstrap context: the library's own values, what the host's bootstrap source
    /// composed, and the sibling hosts.</summary>
    public required WorkloadBootstrapContext Bootstrap { get; init; }

    /// <summary>The provider this instance reconciles on, as its DESCRIPTOR: the provider the instance is bound to,
    /// or the installation's effective provider (explicit setting or auto-detect) while it is not bound yet.
    /// Everything downstream asks what the provider does, and only persistence wants the bare id.</summary>
    public required WorkloadProviderDescriptor EffectiveProvider { get; init; }

    /// <summary>The naming seam of the provider in <see cref="EffectiveProvider"/> (the bound one once the instance is bound). Carried beside the descriptor because everything that
    /// computes a spec needs both, and resolving it downstream would mean a second place deciding which namer
    /// a provider gets.</summary>
    public required IWorkloadResourceNamer Namer { get; init; }

    /// <summary>The installation label value fed to the desired spec's label set. Empty when unknown.</summary>
    public string InstallationId { get; init; } = string.Empty;

    /// <summary>The plugin's currently-installed MODULE version, or <c>null</c> when unknown. Detects the
    /// new-module-vs-old-sidecar skew a DB restore can leave behind, against the reported sidecar version.</summary>
    public string? InstalledGroupVersion { get; init; }
}
