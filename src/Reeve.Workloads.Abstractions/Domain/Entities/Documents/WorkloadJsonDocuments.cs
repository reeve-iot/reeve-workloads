namespace Reeve.Workloads.Abstractions.Domain.Entities.Documents;

/// <summary>What every typed JSON document carries about the string column it was built from. The typed documents
/// are written next to those columns until the columns are dropped, and read only once they are proven current.</summary>
/// <remarks>Persistence shapes, not domain types: nominal classes over EF-native primitives, enums as their member
/// names, maps as key-sorted lists. A member added later must be nullable or defaulted, since older rows lack it.</remarks>
public interface IWorkloadJsonDocument
{
    /// <summary>The document's own shape version; 1 for every document written so far.</summary>
    int SchemaVersion { get; }

    /// <summary>The content fingerprint of the string column this document was built from, when it was built.</summary>
    string? SourceFingerprint { get; }

    /// <summary>True when the column could not be read: the document then carries no content, and a reader treats it
    /// exactly as the column's own reader treats corrupt input.</summary>
    bool Unreadable { get; }
}

/// <summary>A string-to-optional-string map, as entries sorted by ordinal key.</summary>
public sealed class WorkloadStringMapDocument : IWorkloadJsonDocument
{
    public int SchemaVersion { get; init; } = 1;

    public string? SourceFingerprint { get; init; }

    public bool Unreadable { get; init; }

    public List<WorkloadStringPairDocument> Entries { get; init; } = [];
}

/// <summary>One map entry.</summary>
public sealed class WorkloadStringPairDocument
{
    public string Key { get; init; } = string.Empty;

    public string? Value { get; init; }
}

/// <summary>The network configuration pinned on an instance. Names stay in declared order.</summary>
public sealed class WorkloadAttachmentPinDocument : IWorkloadJsonDocument
{
    public int SchemaVersion { get; init; } = 1;

    public string? SourceFingerprint { get; init; }

    public bool Unreadable { get; init; }

    public string Mode { get; init; } = string.Empty;

    public List<string> AttachmentNames { get; init; } = [];
}

/// <summary>An instance's retained-volume snapshot, in the order it was taken.</summary>
public sealed class RetainedVolumesSnapshotDocument : IWorkloadJsonDocument
{
    public int SchemaVersion { get; init; } = 1;

    public string? SourceFingerprint { get; init; }

    public bool Unreadable { get; init; }

    public List<RetainedVolumeSnapshotDocument> Volumes { get; init; } = [];
}

/// <summary>One retained volume of the snapshot.</summary>
public sealed class RetainedVolumeSnapshotDocument
{
    public string VolumeId { get; init; } = string.Empty;

    public string ResourceName { get; init; } = string.Empty;

    /// <summary>A <see cref="RetainedVolumeSchemeId.PersistedValue"/>: the enum member name (<c>Kubernetes</c>)
    /// for the two original schemes, and the lowercase id for any other.</summary>
    public string VolumeNamingScheme { get; init; } = string.Empty;

    public string Size { get; init; } = string.Empty;

    /// <summary>A <c>VolumeCriticality</c> member name.</summary>
    public string Criticality { get; init; } = string.Empty;

    public string? Description { get; init; }
}

