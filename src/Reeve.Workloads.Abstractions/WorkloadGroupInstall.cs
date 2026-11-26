using Reeve.Workloads.Abstractions.Domain.Network;

namespace Reeve.Workloads.Abstractions;

/// <summary>A module instance the host has decided to install, with what it judged: the requirements fingerprint it
/// compared with what its operator accepted.</summary>
public sealed record WorkloadGroupInstall
{
    public required Guid GroupInstanceId { get; init; }

    /// <summary>The module, matched trimmed and without case.</summary>
    public required string GroupId { get; init; }

    /// <summary>The host's opaque name for where the module is loaded from, handed back to the manifest source.</summary>
    public required string SourceKey { get; init; }

    /// <summary>The version being installed, or null for a bundled module, which publishes no versions.</summary>
    public string? Version { get; init; }

    /// <summary>The operator's install-time values, as <see cref="IWorkloadRequirementsService.ForInstallAsync"/>
    /// read them. A non-blank <c>existingClaim.&lt;volume&gt;</c> holds the workload until
    /// <see cref="IWorkloadLifecycleService.SetParametersAsync"/> binds it.</summary>
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } = new Dictionary<string, string?>();

    /// <summary>The network configuration accepted for a workload, by workload id; a workload without one is not
    /// checked. Every key must be a declared workload.</summary>
    public IReadOnlyDictionary<string, WorkloadAttachmentPin> AttachmentPins { get; init; } =
        new Dictionary<string, WorkloadAttachmentPin>(StringComparer.Ordinal);

    /// <summary>The fingerprint of the requirements the host judged, from
    /// <see cref="IWorkloadRequirementsService.ForInstallAsync"/> over the same module, source, version and values.</summary>
    public required string RequirementsFingerprint { get; init; }

    /// <summary>Leave the reconciler un-nudged, for a host that records parameters before the first pass.</summary>
    public bool SuppressReconcileNudge { get; init; }
}

/// <summary>What <see cref="IWorkloadLifecycleService.MaterializeAsync"/> did.</summary>
public enum WorkloadMaterializeOutcome
{
    /// <summary>The module row and a workload row per declared workload exist, and the pins are recorded.</summary>
    Materialized = 0,

    /// <summary>The module row records another version and a workload row of the module is left. Moving a version is
    /// an update; nothing was written.</summary>
    VersionMismatch = 1,

    /// <summary>The module row records another source and a workload row of the module is left; nothing was
    /// written.</summary>
    SourceMismatch = 2,

    /// <summary>No deployment is declared at that version, or a pin names a workload that is not declared; nothing
    /// was written.</summary>
    NotDeclared = 3,

    /// <summary>The requirements are not the ones the host judged, or the values cannot be resolved; nothing was
    /// written, and the host decides again.</summary>
    PreconditionFailed = 4,
}

/// <summary>The outcome of a materialize, with why when it refused.</summary>
public sealed record WorkloadMaterializeResult
{
    public required WorkloadMaterializeOutcome Outcome { get; init; }

    /// <summary>Why nothing was written; null when materialized.</summary>
    public string? Message { get; init; }

    public static WorkloadMaterializeResult Materialized { get; } = new() { Outcome = WorkloadMaterializeOutcome.Materialized };

    public static WorkloadMaterializeResult Refused(WorkloadMaterializeOutcome outcome, string message)
        => new() { Outcome = outcome, Message = message };
}
