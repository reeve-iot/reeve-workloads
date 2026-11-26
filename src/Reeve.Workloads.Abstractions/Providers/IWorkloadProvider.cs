using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Network;

namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>The runtime-provider SPI every provider implements so the reconciler/lifecycle stay provider-agnostic.
/// Keys adoption/drift/teardown on the <c>reeve.io/workload-instance</c> label, never reconstructed names; must not throw for a merely-unreachable provider.</summary>
public interface IWorkloadProvider
{
    /// <summary>This provider's id and declared traits. The library asks the DESCRIPTOR what a provider does
    /// and never compares the id against a known list, which is what lets a third party ship one.</summary>
    WorkloadProviderDescriptor Descriptor { get; }

    /// <summary>Shorthand for <see cref="WorkloadProviderDescriptor.Id"/>.</summary>
    WorkloadProviderId Provider => Descriptor.Id;

    /// <summary>Idempotent upsert of runtime resources for <paramref name="spec"/>; a k8s SSA 409 surfaces as
    /// <see cref="WorkloadApplyResult.Conflict"/>. <paramref name="forceConflicts"/> (default false) adopts foreign-owned fields instead of conflicting — pass true only for an authoritative transition (update/rollback).</summary>
    Task<WorkloadApplyResult> ApplyAsync(
        WorkloadDesiredSpec spec,
        IProgress<WorkloadApplyProgress>? progress,
        CancellationToken cancellationToken = default,
        bool forceConflicts = false);

    /// <summary>Deletes runtime resources for <paramref name="reference"/>. When <paramref name="retainVolumes"/> is
    /// true, volumes are KEPT indefinitely (manual purge only); everything else goes. Idempotent: resources that are
    /// already gone count as removed. Throws when the resources could not be removed (provider unreachable, refused,
    /// not configured, timed out), so the caller keeps the instance and retries instead of forgetting a live runtime.</summary>
    Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default);

    /// <summary>Stops execution WITHOUT deleting resources/volumes. k8s scales to 0 replicas; DaemonSet gets a
    /// no-match nodeSelector. Idempotent; MUST NOT throw for a merely-unreachable provider — log and return.</summary>
    Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default);

    /// <summary>Undo Suspend — restore the workload to its rendered running shape. Idempotent, unreachable-tolerant.
    /// The reconciler re-applies the desired spec after resuming, so this only needs to lift the suspend mechanism.</summary>
    Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default);

    /// <summary>Reports provider-level readiness only — under a host health mode the Healthy signal comes from the
    /// host's health contributor. <see cref="WorkloadProviderPhase.NotFound"/> when no object matches; <see cref="WorkloadProviderPhase.Unknown"/> when unreachable.
    /// The reconciler re-applies a deployed workload it reads as NotFound, so NotFound must mean a read that succeeded
    /// and found nothing: a failed, refused or partial read reports Unknown.</summary>
    Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default);

    /// <summary>Reads status for a SET of instances in as few round-trips as the provider can manage — one label
    /// query answers a whole namespace, where the single read above answers one instance. Same never-throw
    /// contract: an unreachable provider reports <see cref="WorkloadProviderPhase.Unknown"/> per reference.</summary>
    /// <remarks>
    /// A default implementation, not a plain method, on purpose: this SPI is public and a third party may already
    /// implement it, so adding a member they must write would break their build on a MINOR release. The default
    /// loops <see cref="GetStatusAsync"/> — correct for everyone, faster for nobody — and a provider that can do
    /// better overrides it AND declares <see cref="WorkloadProviderDescriptor.ReadsStatusInBatches"/>. The
    /// reconciler asks the descriptor, never the type, so it never pays for the looping default.
    /// <para>A reference the provider cannot answer at all is simply ABSENT from the result; the caller falls
    /// back to the single read for it rather than inventing a phase.</para>
    /// </remarks>
    async Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
        IReadOnlyList<WorkloadRef> references,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);

        var statuses = new Dictionary<WorkloadRef, WorkloadStatus>(references.Count);
        foreach (var reference in references)
        {
            statuses[reference] = await GetStatusAsync(reference, cancellationToken).ConfigureAwait(false);
        }

        return statuses;
    }

    /// <summary>Runs the provider's named preflight checks against <paramref name="spec"/> BEFORE deploy
    /// (PSA, ResourceQuota/LimitRange, node-affinity, API-server reachability). Each check carries a severity and an optional fix snippet.</summary>
    Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default);

    /// <summary>Fetches recent logs for <paramref name="reference"/>, capped at <paramref name="tailLines"/>, oldest first, each line
    /// with the runtime's timestamp in <see cref="WorkloadLogLine.AtUtc"/> where the runtime records one. When
    /// <paramref name="previousContainer"/> is true, returns the previous-container tail with restart-boundary markers. Manual mode
    /// returns one guidance line. A live tail polls this with one line more than it shows and passes each result to
    /// <see cref="WorkloadLogTail.LinesAfter"/>.</summary>
    Task<WorkloadLogs> GetRecentLogsAsync(
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        CancellationToken cancellationToken = default);

    /// <summary>ONE read-only, idempotent check of which of <paramref name="attachments"/> exist on the provider — MUST NEVER create,
    /// mutate, delete or schedule anything, and never reads what an attachment contains. Reports Unreadable (retryable) when the listing
    /// faults or is refused, NotApplicable when the provider can't be interrogated (Manual); never throws. A missing attachment goes in
    /// <c>MissingAttachments</c>; the engine parks on it.</summary>
    Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken = default);
}

/// <summary>The outcome of <see cref="IWorkloadProvider.GetAttachmentExistenceAsync"/>. A listing that faulted (Unreadable, retryable)
/// is never mistaken for an attachment that is genuinely absent.</summary>
public sealed record WorkloadAttachmentExistence
{
    /// <summary>What the read produced.</summary>
    public required WorkloadAttachmentExistenceStatus Status { get; init; }

    /// <summary>The requested attachments that do not exist (deleted, or not created yet), in declared order. Meaningful only for
    /// <see cref="WorkloadAttachmentExistenceStatus.Read"/>.</summary>
    public IReadOnlyList<string> MissingAttachments { get; init; } = [];

    /// <summary>An operator-facing explanation, set for every non-<c>Read</c> status; null otherwise.</summary>
    public string? Detail { get; init; }

    /// <summary>A successful read naming the requested attachments that do not exist (empty when all exist).</summary>
    public static WorkloadAttachmentExistence Read(IReadOnlyList<string> missing) => new()
    {
        Status = WorkloadAttachmentExistenceStatus.Read,
        MissingAttachments = missing ?? throw new ArgumentNullException(nameof(missing)),
    };

    /// <summary>The provider could be asked but did not answer — a retryable fault, never a verdict that an attachment is missing.</summary>
    public static WorkloadAttachmentExistence Unreadable(string detail) => new()
    {
        Status = WorkloadAttachmentExistenceStatus.Unreadable,
        Detail = detail,
    };

    /// <summary>There is no network object for Reeve to look up on this provider (Manual).</summary>
    public static WorkloadAttachmentExistence NotApplicable(string detail) => new()
    {
        Status = WorkloadAttachmentExistenceStatus.NotApplicable,
        Detail = detail,
    };

    /// <summary>Value equality, with <see cref="MissingAttachments"/> compared element by element.</summary>
    public bool Equals(WorkloadAttachmentExistence? other)
        => other is not null
           && Status == other.Status
           && string.Equals(Detail, other.Detail, StringComparison.Ordinal)
           && MissingAttachments.SequenceEqual(other.MissingAttachments, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Status);
        hash.Add(Detail, StringComparer.Ordinal);
        foreach (var name in MissingAttachments)
        {
            hash.Add(name, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>The three outcomes of an attachment existence read.</summary>
public enum WorkloadAttachmentExistenceStatus
{
    /// <summary>The attachments were looked up; <see cref="WorkloadAttachmentExistence.MissingAttachments"/> names any that do not exist.</summary>
    Read = 0,

    /// <summary>The read FAULTED (transport, 5xx, 403, daemon restart). The reconciler parks
    /// <c>AttachmentExistenceUnverifiable</c> — retryable, never a silent proceed.</summary>
    Unreadable = 1,

    /// <summary>The provider cannot be interrogated at all (Manual) — there is nothing to look up.</summary>
    NotApplicable = 2,
}
