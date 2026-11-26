using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;

namespace Reeve.Workloads.Abstractions;

/// <summary>Turns a module instance's <c>deployment</c> manifest into <see cref="WorkloadInstance"/> rows, suspends
/// and resumes them for the host, and tears them down with volume retention. Every state-advancing call nudges the reconciler; actual teardown runs leader-only via <see cref="CompleteTeardownAsync"/>.</summary>
/// <remarks>Whether a workload may run is the host's decision, made before it calls here: the library records what
/// the host decided and holds what it suspends, and judges nothing about privileges.</remarks>
public interface IWorkloadLifecycleService
{
    /// <summary>Installs a module instance: records the module row on its first install, creates a
    /// <see cref="WorkloadInstance"/> row per declared workload (idempotent), records the attachment pins and nudges
    /// the reconciler. Everything is checked before anything is written.</summary>
    /// <remarks><para>A module row recording another version or source is refused while any workload row of the
    /// module is left, tearing down included. With none left the module is reinstalled: its row takes the install's
    /// source and version, as when the last instance was deleted at one version and installed again at another.</para>
    /// <para><see cref="WorkloadGroupInstall.SuppressReconcileNudge"/> and a pre-set
    /// <see cref="WorkloadInstance.RestoreBindingUnresolved"/> hold exist so a restore's <c>existingClaim</c> can bind
    /// before the reconciler deploys a fresh volume it could never later correct.</para></remarks>
    Task<WorkloadMaterializeResult> MaterializeAsync(
        WorkloadGroupInstall install, CancellationToken cancellationToken = default);

    /// <summary>Suspends every workload of a module instance: each row that is not tearing down is held
    /// <see cref="WorkloadInstanceState.Suspended"/> / <see cref="WorkloadStateReason.SuspendedByHost"/>, its
    /// runtime is suspended on the provider and its credential revoked. The reconciler applies nothing to it until
    /// <see cref="ResumeAsync"/>, and each of its passes over the held row revokes and suspends again. An
    /// ExternallyManaged row is held with <see cref="WorkloadStateReason.SuspendedByHostExternallyManaged"/> and its
    /// runtime, the operator's, is never suspended; a Switching row is revoked and held when the switch ends. Idempotent.</summary>
    /// <remarks>This is how a host withdraws a workload it no longer accepts without tearing it down; an operator's
    /// pause is overridden. An operator command or day-2 edit that would move a held row is refused; see
    /// <see cref="WorkloadInstance.IsHeldByHost"/>.</remarks>
    Task SuspendAsync(Guid groupInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Resumes every workload of a module instance the host holds — the ones <see cref="SuspendAsync"/>
    /// suspended — so the next pass re-applies it; a row held while ExternallyManaged goes back to ExternallyManaged
    /// with nothing applied. Idempotent.</summary>
    Task ResumeAsync(Guid groupInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Records a module installed before this library recorded versions, so its workloads reconcile against
    /// the version the host has installed. A host calls it for every installed module at boot, before the reconciler's
    /// first pass; until a module has a row its workloads hold, never applied against a guessed version.</summary>
    /// <remarks>Idempotent, and never a way to move a row: a row with another version or source is left alone and
    /// reported, because moving a version is an update and goes through the update initiator.</remarks>
    /// <param name="groupId">The module, matched trimmed and without case.</param>
    /// <param name="sourceKey">The host's opaque name for where the module is loaded from, at most 200 characters,
    /// handed back unchanged to the manifest source and to the host's update stages.</param>
    /// <param name="installedVersion">The installed version, or null when none is known.</param>
    /// <param name="cancellationToken">Cancels the read and the write.</param>
    Task<WorkloadGroupAdoptOutcome> AdoptGroupAsync(
        string groupId,
        string sourceKey,
        string? installedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Records operator-supplied parameter values and, once all REQUIRED ones are present,
    /// advances the instance so the reconciler applies it. The FIRST legitimate writer that may clear <see cref="WorkloadInstance.RestoreBindingUnresolved"/> — a failed bind leaves the hold set, so a rejected restore never deploys fresh.</summary>
    Task SetParametersAsync(
        Guid workloadInstanceId,
        IReadOnlyDictionary<string, string?> resolvedParameters,
        CancellationToken cancellationToken = default);

    /// <summary>Pins the network configuration the host accepted for one workload, replacing any pin, and nudges the
    /// reconciler. Before applying an operator-network spec the engine compares the pin with the spec's mode and
    /// ordered attachment names and with the digest the provider observes, and parks the apply when they differ.
    /// A workload with no pin is applied without that comparison.</summary>
    /// <remarks>The pin is not part of the desired spec: pinning never restarts a running workload, it only decides
    /// whether the next apply may proceed.</remarks>
    /// <param name="workloadInstanceId">The workload instance.</param>
    /// <param name="pin">The accepted configuration, with the digest as the provider composes it; null clears the
    /// pin. A pin with no attachment name, a blank name or a blank digest is refused.</param>
    /// <param name="cancellationToken">Cancels the read, the write and the nudge.</param>
    /// <returns>False, and nothing written, when there is no such instance.</returns>
    Task<bool> PinAttachmentAsync(
        Guid workloadInstanceId,
        WorkloadAttachmentPin? pin,
        CancellationToken cancellationToken = default);

    /// <summary>Marks every instance of <paramref name="pluginInstanceId"/> <see cref="WorkloadInstanceState.Terminating"/>
    /// and persists <paramref name="retention"/>; only flips rows and nudges — actual teardown is leader-only via <see cref="CompleteTeardownAsync"/>. Idempotent.</summary>
    /// <remarks>A row flipped to Terminating is revoked only once that write is saved. A disowned row (ExternallyManaged,
    /// or held by the host while it was) is revoked before its record is deleted, and its runtime is left to the operator.
    /// A <see cref="WorkloadInstanceState.Switching"/> row is torn down like a managed one: its runtime stays Reeve's
    /// until the switch commits, and a switch that commits first makes it a disowned row. A write that loses a race is
    /// re-read and re-applied, up to three tries.</remarks>
    /// <exception cref="InvalidOperationException">A disowned row's credential could not be revoked. That row is kept,
    /// the other rows are flipped, and the uninstall is to be retried.</exception>
    Task BeginUninstallAsync(
        Guid pluginInstanceId,
        WorkloadRetention retention,
        CancellationToken cancellationToken = default);

    /// <summary>Completes teardown of a <see cref="WorkloadInstanceState.Terminating"/> instance (leader-only).
    /// It revokes the credential first. On success (or no provider/runtime) the row is deleted; if the revoke or the provider throws, the row stays Terminating and this returns <c>false</c> so the reconciler retries — a row whose teardown failed is NEVER deleted. Idempotent.</summary>
    Task<bool> CompleteTeardownAsync(WorkloadInstance instance, CancellationToken cancellationToken = default);

    /// <summary>Forgets a workload whose teardown cannot complete: deletes its row without touching its provider, and
    /// returns what may be left on the environment, which is then the operator's to remove. The host authorizes the
    /// caller, confirms with the operator and audits before it calls.</summary>
    /// <remarks><para>Only a <see cref="WorkloadInstanceState.Terminating"/> row may be forgotten, and only once it has
    /// been Terminating longer than <c>Workloads:Repairs:StuckWarningAfterSeconds</c> (when the <c>.stuck</c> Repairs
    /// issue is raised), with no update of its module in progress. Any other state answers
    /// <see cref="WorkloadForgetOutcome.NotTearingDown"/>: a live row is uninstalled first, and a
    /// <see cref="WorkloadInstanceState.Switching"/> row is torn down by that uninstall. An update in progress answers
    /// <see cref="WorkloadForgetOutcome.UpdateInProgress"/> until it ends, by itself or through
    /// <see cref="Update.IWorkloadUpdateAbandonService"/>; an ended update, rolled back or failed, does not block.</para>
    /// <para>The credentials are revoked once, best-effort; a failure is reported, not retried. Restore claims the
    /// workload held are released and its open Repairs issues cleared. No retained-volume record is written or
    /// changed: the volumes the uninstall meant to retain are reported, because a leftover runtime may still mount
    /// them. An audit entry <c>workloads.forget</c> is written. A write that loses a race is re-read and judged
    /// again, up to three tries. A repeated call answers <see cref="WorkloadForgetOutcome.NotFound"/> and does
    /// nothing.</para></remarks>
    /// <param name="workloadInstanceId">The workload instance.</param>
    /// <param name="actorUserId">The operator, recorded on the audit entry.</param>
    /// <param name="cancellationToken">Cancels the reads and the delete.</param>
    Task<WorkloadForgetResult> ForgetAsync(
        Guid workloadInstanceId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IWorkloadLifecycleService.AdoptGroupAsync"/> did.</summary>
public enum WorkloadGroupAdoptOutcome
{
    /// <summary>The module had no row, and now has one with the given values.</summary>
    Created = 0,

    /// <summary>The module's row already holds the given values; nothing was written.</summary>
    Unchanged = 1,

    /// <summary>The module's row holds another version; nothing was written.</summary>
    VersionMismatch = 2,

    /// <summary>The module's row holds the same version from another source; nothing was written.</summary>
    SourceMismatch = 3,
}
