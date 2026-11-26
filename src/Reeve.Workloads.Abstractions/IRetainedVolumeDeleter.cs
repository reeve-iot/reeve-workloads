using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions;

/// <summary>Provider-agnostic seam that PHYSICALLY frees a retained volume's storage; the service only ever
/// MARKS the row purged. Retention outlives the instance, so the delete is addressed by recorded resource name.</summary>
public interface IRetainedVolumeDeleter
{
    /// <summary>Frees the storage backing <paramref name="retainedVolume"/>. Idempotent; returns <c>false</c> (never
    /// throws) when the provider is unreachable, so the caller keeps the row ACTIVE for a retry.</summary>
    Task<bool> TryDeletePhysicalVolumeAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default);
}

/// <summary>A provider's deleter for the retained volumes of ONE naming scheme. The engine registers a deleter that
/// dispatches each purge to the registered scheme deleter owning the row's <see cref="RetainedVolume.VolumeNamingScheme"/>,
/// and refuses (fail-closed) a row whose scheme no registered provider owns.</summary>
public interface IRetainedVolumeSchemeDeleter : IRetainedVolumeDeleter
{
    /// <summary>The naming scheme of the retained volumes this deleter can free.</summary>
    RetainedVolumeSchemeId NamingScheme { get; }
}

/// <summary>A deleter for a build with no physical-volume backend; the engine registers a scheme dispatcher instead. It frees NOTHING, so it FAILS
/// CLOSED with <c>false</c> rather than marking a row purged while a live volume is stranded.</summary>
public sealed class NullRetainedVolumeDeleter : IRetainedVolumeDeleter
{
    public Task<bool> TryDeletePhysicalVolumeAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
