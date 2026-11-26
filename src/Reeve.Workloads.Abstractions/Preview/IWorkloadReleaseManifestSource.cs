namespace Reeve.Workloads.Abstractions.Preview;

/// <summary>Read-only lookup of a release's manifest for the install-preview service. A seam that keeps the
/// preview service free of EF coupling. The registry host is <c>null</c> for non-OCI providers.</summary>
public interface IWorkloadReleaseManifestSource
{
    /// <summary>Returns <c>null</c> when no non-yanked release matches; never throws for a missing
    /// release.</summary>
    Task<WorkloadReleaseManifest?> TryGetAsync(Guid repositoryId, string version, CancellationToken cancellationToken = default);
}

/// <summary>A resolved release manifest: the raw JSON and the artifact's own registry host, null when the
/// provider has no container registry.</summary>
public sealed record WorkloadReleaseManifest
{
    /// <summary>The raw <c>reeve-plugin.json</c> text of the release.</summary>
    public required string ManifestJson { get; init; }

    /// <summary>Compared against each sidecar image's registry host to raise the cross-registry flag. Null
    /// for non-OCI providers.</summary>
    public string? PluginArtifactRegistryHost { get; init; }
}
