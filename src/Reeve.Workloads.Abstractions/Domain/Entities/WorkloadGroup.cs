namespace Reeve.Workloads.Abstractions.Domain.Entities;

/// <summary>One module this installation deploys workloads for: which version is installed and where the host loads
/// it from. The library's own record, so the version an update commits is the one every reconcile reads.</summary>
/// <remarks>One row per module id, matched trimmed and without case, as module ids are everywhere. The unique index
/// over <c>lower(module_id)</c> cannot be expressed in the model, so the migration owns it.</remarks>
public sealed class WorkloadGroup
{
    /// <summary>The longest module id, source key or version a row stores.</summary>
    public const int MaxLength = 200;

    private WorkloadGroup()
    {
    }

    /// <summary>Records a module. <paramref name="installedVersion"/> blank means none is known: a bundled module, or
    /// an image adopted from before anything tracked versions.</summary>
    public static WorkloadGroup Record(
        string groupId, string sourceKey, string? installedVersion, DateTimeOffset atUtc)
        => new()
        {
            GroupId = Required(groupId, nameof(groupId)),
            SourceKey = Required(sourceKey, nameof(sourceKey)),
            InstalledVersion = string.IsNullOrWhiteSpace(installedVersion)
                ? null
                : Required(installedVersion, nameof(installedVersion)),
            CreatedAtUtc = atUtc,
            UpdatedAtUtc = atUtc,
        };

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>The module, trimmed as recorded.</summary>
    public string GroupId { get; private set; } = string.Empty;

    /// <summary>The host's opaque name for where the module is loaded from, handed back to it unchanged.</summary>
    public string SourceKey { get; private set; } = string.Empty;

    /// <summary>The installed version, or null when none is known.</summary>
    public string? InstalledVersion { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Concurrency token, advanced by the context on every save that writes the row.</summary>
    public int Version { get; private set; }

    /// <summary>Records <paramref name="version"/> as installed. False, and nothing written, when it already is: an
    /// update's commit is repeated on a re-tick or a failover.</summary>
    public bool MoveTo(string version, DateTimeOffset atUtc)
    {
        var target = Required(version, nameof(version));
        if (string.Equals(InstalledVersion, target, StringComparison.Ordinal))
        {
            return false;
        }

        InstalledVersion = target;
        UpdatedAtUtc = atUtc;
        return true;
    }

    /// <summary>Records the module installed again from <paramref name="sourceKey"/> at <paramref name="installedVersion"/>.
    /// Only for a module no workload row is left of: under a live row, moving a version is an update.</summary>
    public void Reinstall(string sourceKey, string? installedVersion, DateTimeOffset atUtc)
    {
        var source = Required(sourceKey, nameof(sourceKey));
        InstalledVersion = string.IsNullOrWhiteSpace(installedVersion)
            ? null
            : Required(installedVersion, nameof(installedVersion));
        SourceKey = source;
        UpdatedAtUtc = atUtc;
    }

    /// <summary>Whether this row is the module <paramref name="groupId"/> names, trimmed and without case.</summary>
    public bool IsGroup(string? groupId)
        => string.Equals(GroupId, groupId?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Required(string? value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var trimmed = value.Trim();
        return trimmed.Length > MaxLength
            ? throw new ArgumentException($"At most {MaxLength} characters.", name)
            : trimmed;
    }
}
