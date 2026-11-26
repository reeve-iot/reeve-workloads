using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Abstractions;

/// <summary>Where a module's deployment declarations come from. The host owns a directory layout, a release
/// table and a version comparator over them; what is BUILT out of the answers is the library's.</summary>
/// <remarks>Two reads, because the callers ask two different questions. The reconcile and edit paths ask "what is
/// available", which is one lookup with two possible answers and a refusal; the installed version is the library's
/// own record, and its manifest is the second read: one EXACT version, from the source the library recorded.</remarks>
public interface IWorkloadGroupManifestSource
{
    /// <summary>Everything the "which manifest is desired" forks need besides the installed version, read once.
    /// Returned as separate fields rather than one answer: the edit form and the apply path resolve them
    /// differently, deliberately.</summary>
    Task<AvailableGroupManifestRead> GetAvailableAsync(
        string groupId, CancellationToken cancellationToken = default);

    /// <summary>The manifest at one EXACT version. The host looks on disk first and then in whatever release
    /// store it has; which of the two answered is not a fact any caller here acts on.</summary>
    /// <param name="sourceKey">The host's own key for where the module is loaded from, as it handed it to
    /// <c>IWorkloadLifecycleService.AdoptGroupAsync</c> or an install; returned unchanged.</param>
    /// <param name="groupId">The module.</param>
    /// <param name="version">The exact version.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<GroupManifestRead> GetAtVersionAsync(
        string sourceKey, string groupId, string version, CancellationToken cancellationToken = default);
}

/// <summary>A manifest read. <see cref="Refused"/> means a declaration EXISTS and this build will not read it,
/// which is never the same fact as none: the fallback a caller reaches for differs, and only one is safe.</summary>
public readonly record struct GroupManifestRead(DeploymentManifest? Manifest, bool Refused)
{
    /// <summary>No declaration for this version. A caller with nothing to compare against may stand something
    /// in — an adopted install has no previous manifest and never did.</summary>
    public static GroupManifestRead Absent => new(null, Refused: false);

    /// <summary>A declaration this build refuses. A caller must fail CLOSED here: standing the candidate in
    /// would let a manifest the operator replaced be applied, or read a moved axis as unchanged.</summary>
    /// <remarks>Named for the state and not for the flag it sets, because <c>Refused</c> is the flag.</remarks>
    public static GroupManifestRead Unreadable => new(null, Refused: true);
}

/// <summary>The answers to "which manifest is available", kept apart. The library picks between them and the
/// manifest at the version it recorded; a host that picked would have to know whether it was answering an edit form
/// or an apply, and it cannot.</summary>
public sealed record AvailableGroupManifestRead
{
    /// <summary>Nothing declared and nothing refused — a module with no deployment at all.</summary>
    public static AvailableGroupManifestRead Nothing { get; } = new();

    /// <summary>The highest deployment-declaring manifest available. Served only where rendering something
    /// beats rendering nothing, which is the parameter-edit form and not the apply path.</summary>
    public DeploymentManifest? HighestAvailable { get; init; }

    /// <summary>The code-factory declaration for a module that ships no manifest of its own. Consulted only
    /// when there are no candidates at all, because a published declaration keeps precedence over it.</summary>
    public DeploymentManifest? Bundled { get; init; }

    /// <summary>A declaration exists that this build REFUSES. Holds the two paths that would otherwise serve
    /// the factory's declaration over one the operator actually published.</summary>
    public bool Refused { get; init; }
}

/// <summary>The manifest source a build gets when the host registers none: every read is absent. Not a
/// default — it is registered nowhere, and exists so a test can compose an engine without one.</summary>
public sealed class NoWorkloadGroupManifestSource : IWorkloadGroupManifestSource
{
    /// <inheritdoc />
    public Task<AvailableGroupManifestRead> GetAvailableAsync(
        string groupId, CancellationToken cancellationToken = default)
        => Task.FromResult(AvailableGroupManifestRead.Nothing);

    /// <inheritdoc />
    public Task<GroupManifestRead> GetAtVersionAsync(
        string sourceKey, string groupId, string version, CancellationToken cancellationToken = default)
        => Task.FromResult(GroupManifestRead.Absent);
}
