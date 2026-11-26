using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Abstractions.Domain.Entities;

/// <summary>Persisted lifecycle state of a workload instance, as its string name (never an ordinal). Transition
/// rules live in <see cref="WorkloadInstanceStateMachine"/>; aggregation ranking in <see cref="Reeve.Workloads.Abstractions.Domain.WorkloadHealthAggregation"/>.</summary>
public enum WorkloadInstanceState
{
    /// <summary>Installed; required install-time parameters are not yet resolved. Mutations that add a new
    /// required parameter on update also land here (re-configuration).</summary>
    PendingConfiguration = 1,

    /// <summary>The provider is applying the desired spec; not yet gated healthy.</summary>
    Deploying = 2,

    /// <summary>Provider-ready and a qualifying fresh health signal is observed.</summary>
    Healthy = 3,

    /// <summary>Running but a health/host prerequisite is failing (e.g. bluetoothd absent) — recoverable.</summary>
    Degraded = 4,

    /// <summary>Deploy/health-gate failed. NOT terminal: re-evaluated at every resync — Failed→Healthy is legal
    /// when the provider reports ready and a qualifying fresh health message is observed.</summary>
    Failed = 5,

    /// <summary>The wire-reported protocol range does not overlap the loaded module's.</summary>
    VersionSkew = 6,

    /// <summary>Manual provider with a live health signal — Reeve observes but does not manage the runtime.</summary>
    ExternallyManaged = 7,

    /// <summary>An operator pause or the host's suspend. k8s service/single-node ⇒ replicas 0; per-node ⇒ no-match nodeSelector.
    /// Excluded from aggregate health — a paused optional workload never masks a Failed critical one.</summary>
    Suspended = 8,

    /// <summary>Resources are being torn down by an uninstall. Row deletion follows.</summary>
    Terminating = 9,

    /// <summary>Provider unreachable (docker socket lost, API server unreachable). Last-known state shown greyed;
    /// NO mutations while Unknown (see <see cref="WorkloadInstance.CanMutate"/>).</summary>
    Unknown = 10,

    /// <summary>k8s foreign resources detected by label — awaiting operator-confirmed adoption.</summary>
    AdoptionPending = 11,

    /// <summary>Durable marker that a managed→manual ownership switch is IN PROGRESS. Entered only from
    /// Healthy/Degraded; resolves to <see cref="ExternallyManaged"/> or unwinds back. Forbids config mutations while set.</summary>
    Switching = 12
}

/// <summary>The volume-retention choice applied when a workload instance is torn down, persisted as its string
/// name. Defaults to retaining: the operator must opt IN to a destructive purge.</summary>
public enum WorkloadRetention
{
    /// <summary>Keep persistent volumes on teardown (the default) — retained indefinitely, restore-on-reinstall.</summary>
    RetainAll = 0,

    /// <summary>Delete persistent volumes on teardown — an explicit, operator-chosen destructive purge.</summary>
    PurgeAll = 1
}

/// <summary>Machine-readable reason for a state transition, persisted as its string name. Drives Repairs text
/// and diagnostics — <see cref="WorkloadInstance.StateMessage"/> is the free-text companion.</summary>
public enum WorkloadStateReason
{
    /// <summary>Provider is applying the spec (→ Deploying). With Degraded: the deploy did not become ready within
    /// <c>Workloads:Reconciler:DeployStalledAfterSeconds</c>, and the state message carries the provider's reason.</summary>
    Provisioning = 0,

    /// <summary>A qualifying fresh health signal was observed (→ Healthy).</summary>
    HealthObserved = 1,

    /// <summary>A previously-healthy instance lost its health signal / a probe is failing (→ Degraded/Failed).</summary>
    HealthLost = 2,

    /// <summary>The runtime provider became unreachable (→ Unknown).</summary>
    ProviderUnreachable = 3,

    /// <summary>Operator paused the instance (→ Suspended).</summary>
    OperatorPaused = 6,

    /// <summary>Operator resumed a paused instance (Suspended → Deploying/Healthy).</summary>
    OperatorResumed = 7,

    /// <summary>The version running is behind the installed module (→ VersionSkew).</summary>
    RunningVersionBehind = 8,

    /// <summary>A manual-provider instance shows a live external health signal (→ ExternallyManaged).</summary>
    ExternalHealthObserved = 10,

    /// <summary>Required configuration is missing — a new required parameter appeared, or install-time params are
    /// unresolved (→ PendingConfiguration).</summary>
    ConfigRequired = 12,

    /// <summary>An operator-triggered or auto redeploy is starting (Degraded/Failed/Suspended → Deploying).</summary>
    Redeploying = 13,

    /// <summary>Uninstall is tearing the instance down (→ Terminating).</summary>
    Uninstalling = 14,

    /// <summary>k8s foreign resources were detected by label (→ AdoptionPending).</summary>
    ForeignResourcesDetected = 15,

    /// <summary>An adopted resource set was accepted and is now being applied under Reeve ownership
    /// (AdoptionPending → Deploying).</summary>
    AdoptionAccepted = 16,

    /// <summary>The desired-spec compute threw an UNEXPECTED (non-config) fault (→ Failed).</summary>
    DesiredSpecComputeFailed = 17,

    /// <summary>The provider REJECTED the desired spec, and retrying it unchanged cannot help (→ Failed).</summary>
    SpecRejected = 19,

    /// <summary>The host suspended every workload of the module instance (→ Suspended). Held as an operator pause is
    /// held, and lifted only by the host's resume.</summary>
    SuspendedByHost = 20,

    /// <summary>The host resumed a module instance it had suspended (Suspended → Deploying, or back to
    /// ExternallyManaged for a row the host held while the operator owned its runtime).</summary>
    ResumedByHost = 21,

    /// <summary>The host suspended an ExternallyManaged instance (→ Suspended). Held as <see cref="SuspendedByHost"/>
    /// is, but its runtime stays the operator's: never scaled down, and the host's resume returns it to ExternallyManaged.</summary>
    SuspendedByHostExternallyManaged = 22
}

/// <summary>A materialization of one managed workload for one plugin instance. <see cref="Id"/> IS the instance
/// identity; transitions go through <see cref="TransitionTo"/>, and an unknown state forbids mutations.</summary>
public sealed class WorkloadInstance
{
    /// <summary>Max persisted length of <see cref="StateMessage"/> (spec cap — long provider events are truncated).</summary>
    public const int MaxStateMessageLength = 2000;

    private static readonly IReadOnlyDictionary<string, string?> EmptyParameters =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    private WorkloadInstance()
    {
    }

    private WorkloadInstance(
        Guid id,
        Guid pluginInstanceId,
        string pluginTypeId,
        string workloadId,
        bool observeOnly,
        DateTimeOffset createdAtUtc)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Instance id is required.", nameof(id)) : id;
        GroupInstanceId = pluginInstanceId == Guid.Empty
            ? throw new ArgumentException("Plugin instance id is required.", nameof(pluginInstanceId))
            : pluginInstanceId;
        GroupId = NormalizeRequired(pluginTypeId, nameof(pluginTypeId));
        WorkloadId = NormalizeRequired(workloadId, nameof(workloadId));
        ObserveOnly = observeOnly;

        State = WorkloadInstanceState.PendingConfiguration;
        StateReason = WorkloadStateReason.ConfigRequired;
        StateMessage = null;
        RestartCount = 0;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        LastTransitionAtUtc = createdAtUtc;
    }

    /// <summary>Factory for a freshly-created instance. Starts waiting for its configuration, the head of the happy
    /// path; the provider is null until the first deploy.</summary>
    public static WorkloadInstance Create(
        Guid id,
        Guid pluginInstanceId,
        string pluginTypeId,
        string workloadId,
        DateTimeOffset createdAtUtc,
        bool observeOnly = false)
        => new(id, pluginInstanceId, pluginTypeId, workloadId, observeOnly, createdAtUtc);

    /// <summary>The canonical instance identity — the <c>instanceId</c> bootstrap value, and the id the host's
    /// seams and credential port are keyed on.</summary>
    public Guid Id { get; private set; }

    /// <summary>The owning plugin instance — the first part of the business key.</summary>
    public Guid GroupInstanceId { get; private set; }

    /// <summary>The plugin type id, used in resource names.</summary>
    public string GroupId { get; private set; } = null!;

    /// <summary>The manifest workload id: the second business-key part, also used in resource names.</summary>
    public string WorkloadId { get; private set; } = null!;

    /// <summary>First 8 lowercase hex chars of <see cref="Id"/> — the resource-name discriminator.</summary>
    public string Inst8 => ComputeInst8(Id);

    /// <summary>The runtime provider, or <c>null</c> until the instance first deploys.</summary>
    public WorkloadProviderId? Provider { get; private set; }

    public WorkloadInstanceState State { get; private set; }

    /// <summary>Machine-readable reason for the current state (drives Repairs and diagnostics).</summary>
    public WorkloadStateReason StateReason { get; private set; }

    /// <summary>Human-readable free-text companion to <see cref="StateReason"/> (capped at <see cref="MaxStateMessageLength"/>).</summary>
    public string? StateMessage { get; private set; }

    /// <summary>SHA-256 of the desired (rendered) spec, or <c>null</c> before first render.</summary>
    public string? DesiredSpecHash { get; private set; }

    /// <summary>SHA-256 of the spec last successfully applied to the provider, or <c>null</c>.</summary>
    public string? AppliedSpecHash { get; private set; }

    /// <summary>The persisted pending scan-run marker, or null when resting. A gating axis DELIBERATELY SEPARATE
    /// from the applied spec hash, so plain spec drift creates ZERO scan jobs. It survives leader failover.</summary>
    public string? PendingScanRunId { get; private set; }

    /// <summary>True when a scan run is pending (the marker holds a run-id). EF-ignored (derived).</summary>
    public bool HasPendingScanRun => PendingScanRunId is not null;

    /// <summary>The nodes the last completed run failed on. Null when that run was clean.</summary>
    public IReadOnlyList<string>? LastScanFailedNodes { get; private set; }

    /// <summary>Transient provisioning progress, or null when not provisioning. Cosmetic and hash-EXCLUDED: it
    /// never touches the state, and every transition clears it, so a stale value cannot outlive its deploy.</summary>
    public int? ProvisioningPercent { get; private set; }

    /// <summary>Per-workload observe-only flag — the reconciler skips drift correction for it.</summary>
    public bool ObserveOnly { get; private set; }

    /// <summary>Provider-reported restart count, which drives crash-loop detection and repairs.</summary>
    public int RestartCount { get; private set; }

    /// <summary>A monotonic operator-RESTART marker, threaded into the desired spec as a hash-EXCLUDED nonce so a
    /// restart re-renders a DIFFERENT pod template and actually rolls, without poisoning drift comparison.</summary>
    public long RestartGeneration { get; private set; }

    /// <summary>The volume-retention choice applied when this instance is torn down, defaulting to retain. The
    /// uninstall flow stamps the operator's choice; the leader-only teardown reads it.</summary>
    public WorkloadRetention Retention { get; private set; } = WorkloadRetention.RetainAll;

    /// <summary>The resolved install-time parameters, as the typed document that now stores them.</summary>
    public WorkloadStringMapDocument? ParametersDocument { get; private set; }

    /// <summary>The retained-volume snapshot, as the typed document that now stores it.</summary>
    public RetainedVolumesSnapshotDocument? RetainedVolumesSnapshotDocument { get; private set; }

    /// <summary>The network configuration the host accepted for this instance, or null when none is pinned. Not part
    /// of the desired spec, so pinning never moves a hash and never restarts the workload.</summary>
    public WorkloadAttachmentPinDocument? AttachmentPinDocument { get; private set; }

    /// <summary>Protected HMAC key and applied/approved unpinned-reference fingerprints used to require an
    /// explicit operator rollout when a provider changes a password reference.</summary>
    public string? ReferenceRolloutState { get; private set; }

    public void SetReferenceRolloutState(string? state, DateTimeOffset nowUtc)
    {
        ReferenceRolloutState = state;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Projects reference identities into the spec-hash input, honoring a persisted literal-equivalent
    /// marker only for the exact stored and candidate binding. Stale marker cleanup happens during a state write.</summary>
    public IReadOnlyDictionary<string, string?> ParameterReferencesForSpecHash(
        IReadOnlyDictionary<string, string?> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        if (string.IsNullOrWhiteSpace(ReferenceRolloutState)) return references;
        JsonObject? root;
        try { root = JsonNode.Parse(ReferenceRolloutState) as JsonObject; }
        catch (JsonException) { return references; }
        if (root?[ReferenceHashPinRules.HashPinnedBindingsJsonName] is not JsonObject pins || pins.Count == 0) return references;

        var projected = references.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var storedParameters = GetResolvedParameters();
        foreach (var (name, markerNode) in pins)
        {
            ReferenceHashPin? marker;
            try
            {
                marker = markerNode?.Deserialize(WorkloadEntityWebJsonContext.Default.ReferenceHashPin);
            }
            catch (JsonException) { marker = null; }
            storedParameters.TryGetValue(name, out var stored);
            if (ReferenceHashPinRules.Evaluate(marker, stored, stored) == ReferenceHashPinRules.Applicability.Invalid)
            {
                continue;
            }
            references.TryGetValue(name, out var candidate);
            if (ReferenceHashPinRules.Evaluate(marker, stored, candidate) != ReferenceHashPinRules.Applicability.Invalid)
                projected.Remove(name);
        }
        return projected;
    }

    /// <summary>True when this instance's RESTORE binding was REJECTED fail-closed at install time, so it must
    /// never deploy fresh. There is deliberately NO re-supply path: only an uninstall clears it.</summary>
    public bool RestoreBindingUnresolved { get; private set; }

    /// <summary>When the instance last moved into Healthy, or refreshed its Healthy reason or message, or <c>null</c>. A
    /// pass that finds it still Healthy with nothing new to say does not move it.</summary>
    public DateTimeOffset? LastHealthyAtUtc { get; private set; }

    /// <summary>When the state last changed — the repairs stuck-in-a-bad-state clock reads this.</summary>
    public DateTimeOffset LastTransitionAtUtc { get; private set; }

    /// <summary>When the current deploy started: the last move into Deploying other than a status read. The stalled-deploy
    /// clock reads it, and <see cref="LastTransitionAtUtc"/> when it is null (a row a 15.1 build inserted).</summary>
    public DateTimeOffset? DeployStartedAtUtc { get; private set; }

    /// <summary>When the provider last REJECTED this instance's desired spec, or <c>null</c> when the last apply
    /// attempt was not a rejection (never applied, or applied successfully since). The re-apply cool-off is timed
    /// from here.
    ///
    /// It cannot be timed from <see cref="LastTransitionAtUtc"/>, the second and every
    /// later rejection is a Failed→Failed SELF-transition, and a self-transition does not move that clock by design
    /// — the repairs scanner anchors its stuck-detection on it, so a workload rejected for an hour must keep
    /// looking an hour old. Timed from there the cool-off would arm once, expire 30s after the FIRST rejection and never re-arm, so the reconciler would re-send an identical already-rejected spec on every pass.
    ///
    /// Nor can the gate ask whether the STATE is Failed/SpecRejected: PendingConfiguration
    /// is a state the engine applies from, and PendingConfiguration→Failed is not a legal transition, so a
    /// rejection there leaves the state where it was and that gate would never match.</summary>
    public DateTimeOffset? LastRejectedApplyAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Optimistic-concurrency token. NOT bumped by the mutators: the store bumps it for every row it
    /// actually writes, so a mutator added later cannot forget it and quietly reopen the lost-update window.</summary>
    public int Version { get; private set; }

    /// <summary>True when configuration mutations (parameter edits, redeploys, secret rotation) are
    /// permitted.</summary>
    public bool CanMutate =>
        State is not WorkloadInstanceState.Unknown
            and not WorkloadInstanceState.Terminating
            and not WorkloadInstanceState.Switching;

    /// <summary>True while the reconciler must OBSERVE-but-never-TOUCH this instance's runtime: mid-switch, disowned or
    /// manual, or held by the host while it was either of the last two. Whether a day-2 edit, operation or update may
    /// change the instance is <see cref="IsOwnershipReleased"/>, which a live manual workload is not.</summary>
    public bool IsReconcileFrozen =>
        State is WorkloadInstanceState.Switching or WorkloadInstanceState.ExternallyManaged || IsHeldExternallyManaged;

    /// <summary>True when Reeve has released ownership of this instance, so a day-2 edit, operation or update must
    /// refuse it until it is re-adopted: mid-switch, disowned (ExternallyManaged on a provider that materializes, or
    /// on none bound), or held by the host while ExternallyManaged. A live workload of a provider that materializes
    /// nothing (manual) is ExternallyManaged because Reeve observes it, not because ownership was released, so it is
    /// not.</summary>
    /// <param name="providers">Describes the instance's bound provider.</param>
    public bool IsOwnershipReleased(IWorkloadProviderRegistry providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return State == WorkloadInstanceState.Switching
            || IsHeldExternallyManaged
            || (State == WorkloadInstanceState.ExternallyManaged
                && (Provider is not { } provider || providers.Describe(provider).Materializes));
    }

    /// <summary>True while the host holds the instance suspended. Only the host's resume or a teardown moves it;
    /// <see cref="TransitionTo"/> throws on any other move, so a day-2 command refuses a held row first.</summary>
    public bool IsHeldByHost =>
        State == WorkloadInstanceState.Suspended
        && StateReason is WorkloadStateReason.SuspendedByHost or WorkloadStateReason.SuspendedByHostExternallyManaged;

    private bool IsHeldExternallyManaged =>
        State == WorkloadInstanceState.Suspended && StateReason == WorkloadStateReason.SuspendedByHostExternallyManaged;

    /// <summary>The host withdrew the instance mid-switch; the switch, or the boot unwind, holds it when it ends.</summary>
    public bool IsHostHoldPending =>
        State == WorkloadInstanceState.Switching && StateReason == WorkloadStateReason.SuspendedByHost;

    internal bool IsLegalTransition(WorkloadInstanceState target, WorkloadStateReason reason)
    {
        if (IsHeldByHost)
        {
            return target switch
            {
                WorkloadInstanceState.Suspended => reason == StateReason,
                WorkloadInstanceState.Deploying => reason == WorkloadStateReason.ResumedByHost && !IsHeldExternallyManaged,
                WorkloadInstanceState.ExternallyManaged => reason == WorkloadStateReason.ResumedByHost && IsHeldExternallyManaged,
                WorkloadInstanceState.Terminating => true,
                _ => false,
            };
        }

        if (reason == WorkloadStateReason.SuspendedByHostExternallyManaged
            && (target != WorkloadInstanceState.Suspended || State != WorkloadInstanceState.ExternallyManaged))
        {
            return false;
        }

        return target == State || WorkloadInstanceStateMachine.IsValidTransition(State, target);
    }

    /// <summary>Transitions if the validity table allows it and the host's hold stays; an illegal move throws. A
    /// self-transition refreshes reason and message; any move into Deploying stamps <see cref="DeployStartedAtUtc"/>.</summary>
    public void TransitionTo(
        WorkloadInstanceState target,
        WorkloadStateReason reason,
        string? message,
        DateTimeOffset nowUtc)
    {
        Transition(target, reason, message, nowUtc);
        if (target == WorkloadInstanceState.Deploying)
        {
            DeployStartedAtUtc = nowUtc;
        }
    }

    /// <summary>Starts the redeploy an operator change (parameters, limits, placement, volume size) asks for, once the
    /// caller has stored the change and cleared the applied hash. A paused (Suspended) instance keeps its state, reason
    /// and message, so the change is applied when it is resumed; any other state that may reach Deploying moves there
    /// with <see cref="WorkloadStateReason.Redeploying"/>.</summary>
    public void BeginOperatorRedeploy(string message, DateTimeOffset nowUtc)
    {
        if (State == WorkloadInstanceState.Suspended
            || !(State == WorkloadInstanceState.Deploying
                || WorkloadInstanceStateMachine.IsValidTransition(State, WorkloadInstanceState.Deploying)))
        {
            return;
        }

        TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Redeploying, message, nowUtc);
    }

    internal void ObserveTransitionTo(
        WorkloadInstanceState target,
        WorkloadStateReason reason,
        string? message,
        DateTimeOffset nowUtc)
        => Transition(target, reason, message, nowUtc);

    internal void StartDeployClock(DateTimeOffset nowUtc)
    {
        DeployStartedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    private void Transition(
        WorkloadInstanceState target,
        WorkloadStateReason reason,
        string? message,
        DateTimeOffset nowUtc)
    {
        var isSelfTransition = target == State;
        if (!IsLegalTransition(target, reason))
        {
            throw new WorkloadStateTransitionException(State, target);
        }

        if (isSelfTransition
            && reason == StateReason
            && ProvisioningPercent is null
            && string.Equals(Truncate(message), StateMessage, StringComparison.Ordinal))
        {
            return;
        }

        if (!isSelfTransition)
        {
            State = target;
            LastTransitionAtUtc = nowUtc;
        }

        StateReason = reason;
        StateMessage = Truncate(message);
        ProvisioningPercent = null;
        if (target == WorkloadInstanceState.Healthy)
        {
            LastHealthyAtUtc = nowUtc;
        }

        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Stamps the moment the provider rejected the desired spec, which is what the re-apply cool-off is
    /// measured from. Called on EVERY rejection, including the ones that are a self-transition and therefore leave
    /// <see cref="LastTransitionAtUtc"/> untouched — that is the whole point of the field. Records no state change
    /// of its own; the caller still performs the transition into Failed/SpecRejected.</summary>
    public void MarkSpecRejected(DateTimeOffset nowUtc)
    {
        LastRejectedApplyAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Clears the rejection stamp because the provider ACCEPTED an apply. Without this the stamp would
    /// mean "was rejected at some point" and go on throttling applies long after the conflict cleared; with it the
    /// stamp means "the last apply attempt was rejected", which is the only thing worth throttling on.</summary>
    public void MarkSpecAccepted(DateTimeOffset nowUtc)
    {
        if (LastRejectedApplyAtUtc is null)
        {
            return;
        }

        LastRejectedApplyAtUtc = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Records the desired (rendered) spec hash. Does not itself transition state.</summary>
    public void SetDesiredSpecHash(string? desiredSpecHash, DateTimeOffset nowUtc)
    {
        var normalized = string.IsNullOrWhiteSpace(desiredSpecHash) ? null : desiredSpecHash.Trim();
        if (string.Equals(normalized, DesiredSpecHash, StringComparison.Ordinal))
        {
            return;
        }

        DesiredSpecHash = normalized;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Records the spec hash last successfully applied to the provider. Does not itself transition state.</summary>
    public void SetAppliedSpecHash(string? appliedSpecHash, DateTimeOffset nowUtc)
    {
        var normalized = string.IsNullOrWhiteSpace(appliedSpecHash) ? null : appliedSpecHash.Trim();
        if (string.Equals(normalized, AppliedSpecHash, StringComparison.Ordinal))
        {
            return;
        }

        AppliedSpecHash = normalized;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Records the pending scan-run marker with the minted run id. The single scalar means at most one
    /// run is ever pending, and a re-begin simply replaces the id. Does not itself transition state.</summary>
    public void BeginScanRun(string runId, DateTimeOffset nowUtc)
    {
        PendingScanRunId = NormalizeRequired(runId, nameof(runId));
        LastScanFailedNodes = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Ends the pending run, recording the nodes whose Job failed. A null/empty set is a clean run and
    /// records nothing, so "every node scanned" stays distinguishable from "some node got no result".</summary>
    public void CompleteScanRun(IReadOnlyList<string>? failedNodeNames, DateTimeOffset nowUtc)
    {
        var failed = failedNodeNames?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToList();

        LastScanFailedNodes = failed is null || failed.Count == 0 ? null : failed;
        PendingScanRunId = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>The nodes the last completed run failed on, or empty when the run was clean. Nothing here has to decode anything.</summary>
    public IReadOnlyList<string> GetLastScanFailedNodes()
        => LastScanFailedNodes ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>Clears the pending scan-run marker back to the resting state. Does not itself transition state.</summary>
    public void ClearScanRun(DateTimeOffset nowUtc)
    {
        PendingScanRunId = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Records transient provisioning progress. It writes ONLY the percentage and message — never the
    /// state or reason — so a busy pull cannot poison the machine, and the next transition clears it.</summary>
    public void SetProvisioningProgress(int? percent, string? message, DateTimeOffset nowUtc)
    {
        ProvisioningPercent = percent is { } value ? Math.Clamp(value, 0, 100) : null;
        StateMessage = Truncate(message);
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Sets the runtime provider (first deploy binds it). Does not itself transition state.</summary>
    public void SetProvider(WorkloadProviderId provider, DateTimeOffset nowUtc)
    {
        Provider = provider;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Sets the observe-only flag (reconciler kill-switch). Does not itself transition state.</summary>
    public void SetObserveOnly(bool observeOnly, DateTimeOffset nowUtc)
    {
        ObserveOnly = observeOnly;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Sets the provider-reported restart count, the crash-loop detection input.</summary>
    public void SetRestartCount(int restartCount, DateTimeOffset nowUtc)
    {
        RestartCount = restartCount < 0 ? 0 : restartCount;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Bumps the operator-RESTART marker so the next pass re-renders a DIFFERENT pod template and the
    /// deployment actually rolls. The nonce is hash-EXCLUDED, so it does not change drift comparison.</summary>
    public void MarkRestart(DateTimeOffset nowUtc)
    {
        RestartGeneration++;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Sets the volume-retention choice applied when this instance is torn down, stamped by the uninstall
    /// flow alongside the terminating transition. Does not itself transition state.</summary>
    public void SetRetention(WorkloadRetention retention, DateTimeOffset nowUtc)
    {
        Retention = retention;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Sets the durable restore-binding-unresolved hold, which the apply gate honours to keep the instance
    /// parked rather than deploying fresh. Does not itself transition state.</summary>
    public void SetRestoreBindingUnresolved(bool unresolved, DateTimeOffset nowUtc)
    {
        RestoreBindingUnresolved = unresolved;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>The resolved install-time parameter values. Returns an EMPTY map when nothing was recorded or the
    /// document cannot be read — an unresolved parameter is a re-configuration reason, not a crash.</summary>
    public IReadOnlyDictionary<string, string?> GetResolvedParameters()
        => WorkloadDocumentRead.Read(ParametersDocument, WorkloadDocumentMapper.FromDocument, () => EmptyParameters);

    /// <summary>Records the resolved install-time parameter values. A null or empty map clears the stored JSON;
    /// otherwise it serializes to a stable object of name to string-or-null.</summary>
    public void SetResolvedParameters(IReadOnlyDictionary<string, string?>? resolvedParameters, DateTimeOffset nowUtc)
    {
        if (resolvedParameters is null || resolvedParameters.Count == 0)
        {
            ParametersDocument = null;
        }
        else
        {
            var ordered = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            foreach (var kv in resolvedParameters)
            {
                ordered[kv.Key] = kv.Value;
            }

            ParametersDocument = WorkloadDocumentMapper.StringMap(ordered);
        }

        UpdatedAtUtc = nowUtc;
    }

    /// <summary>True when this instance has EVER been deployed. Anchored SOLELY on a bound provider, which is set
    /// once and never cleared: also requiring the applied hash would read a restarted instance as never-deployed.</summary>
    public bool WasDeployed => Provider is not null;

    /// <summary>The retained-volume metadata snapshot. Empty when none was taken or the document cannot be read,
    /// so the teardown records nothing rather than crashing.</summary>
    public IReadOnlyList<RetainedVolumeSnapshotItem> GetRetainedVolumesSnapshot()
        => WorkloadDocumentRead.Read(
            RetainedVolumesSnapshotDocument,
            WorkloadDocumentMapper.FromDocument,
            Array.Empty<RetainedVolumeSnapshotItem>);

    /// <summary>Captures the retained-volume metadata snapshot at uninstall time; a null or empty list clears it.
    /// The leader-only teardown reads it to write the durable rows after the deployment context is gone.</summary>
    public void SnapshotRetainedVolumes(IReadOnlyList<RetainedVolumeSnapshotItem>? volumes, DateTimeOffset nowUtc)
    {
        RetainedVolumesSnapshotDocument = volumes is null || volumes.Count == 0
            ? null
            : WorkloadDocumentMapper.RetainedVolumes(volumes);
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>The pinned network configuration. Absent means nothing is pinned and the engine checks nothing;
    /// Unreadable means a pin IS recorded and cannot be read, which a caller must treat as not matching.</summary>
    public WorkloadDocumentReading<WorkloadAttachmentPin> ReadAttachmentPin()
        => WorkloadDocumentRead.Decide(AttachmentPinDocument, WorkloadDocumentMapper.FromDocument);

    /// <summary>Pins the network configuration the host accepted, replacing any pin; null clears it. A pin needs at
    /// least one attachment name, since an empty one could only ever match nothing.</summary>
    public void PinAttachment(WorkloadAttachmentPin? pin, DateTimeOffset nowUtc)
    {
        if (pin is not null)
        {
            if (pin.AttachmentNames is not { Count: > 0 } names || names.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("A pin names at least one attachment, and no blank one.", nameof(pin));
            }
        }

        AttachmentPinDocument = pin is null ? null : WorkloadDocumentMapper.AttachmentPin(pin);
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Computes the 8-char lowercase-hex instance discriminator from an instance id.</summary>
    public static string ComputeInst8(Guid id) => id.ToString("N").Substring(0, 8).ToLowerInvariant();

    private static string? Truncate(string? message) =>
        message is { Length: > MaxStateMessageLength } ? message[..MaxStateMessageLength] : message;

    private static string NormalizeRequired(string value, string paramName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", paramName)
            : value.Trim();
}

/// <summary>One entry in an instance's retained-volume snapshot: a self-contained copy of the metadata the durable
/// row needs. The resource name is the PROVIDER-SPECIFIC physical name, resolved at snapshot time.</summary>
public sealed record RetainedVolumeSnapshotItem
{
    /// <summary>The manifest volume id (e.g. <c>state</c>).</summary>
    public required string VolumeId { get; init; }

    /// <summary>The provider PVC / named-volume name the data physically lives under.</summary>
    public required string ResourceName { get; init; }

    /// <summary>The volume-naming SCHEME the resource name follows, recorded so a later restore resolves the REAL
    /// physical name. Defaults to the kubernetes scheme, which is what an old snapshot deserializes to.</summary>
    public RetainedVolumeSchemeId VolumeNamingScheme { get; init; } = RetainedVolumeSchemeId.Kubernetes;

    /// <summary>Requested/allocated size, e.g. <c>"512Mi"</c>.</summary>
    public required string Size { get; init; }

    /// <summary>The volume's criticality.</summary>
    public required VolumeCriticality Criticality { get; init; }

    /// <summary>The author's free-text volume description, or null.</summary>
    public string? Description { get; init; }
}

/// <summary>Thrown when a transition is asked for a state move the machine forbids.</summary>
public sealed class WorkloadStateTransitionException : InvalidOperationException
{
    public WorkloadStateTransitionException(WorkloadInstanceState from, WorkloadInstanceState to)
        : base($"Illegal workload-instance state transition: {from} → {to}.")
    {
        From = from;
        To = to;
    }

    public WorkloadInstanceState From { get; }

    public WorkloadInstanceState To { get; }
}

/// <summary>The pure transition-validity table for the workload-instance state machine. Static and side-effect-free
/// so it is exhaustively unit-testable; self-transitions are handled by the entity, not this table.</summary>
public static class WorkloadInstanceStateMachine
{
    private static readonly IReadOnlyDictionary<WorkloadInstanceState, IReadOnlySet<WorkloadInstanceState>> Allowed =
        new Dictionary<WorkloadInstanceState, IReadOnlySet<WorkloadInstanceState>>
        {
            [WorkloadInstanceState.PendingConfiguration] = Set(
                WorkloadInstanceState.Deploying,
                WorkloadInstanceState.AdoptionPending,
                WorkloadInstanceState.Suspended,
                WorkloadInstanceState.Terminating,
                WorkloadInstanceState.Unknown),

            [WorkloadInstanceState.Deploying] = Set(
                WorkloadInstanceState.Healthy,
                WorkloadInstanceState.Degraded,
                WorkloadInstanceState.Failed,
                WorkloadInstanceState.PendingConfiguration,
                WorkloadInstanceState.ExternallyManaged,
                WorkloadInstanceState.VersionSkew,
                WorkloadInstanceState.Suspended,
                WorkloadInstanceState.Terminating,
                WorkloadInstanceState.Unknown),

            [WorkloadInstanceState.Healthy] = Set(
                WorkloadInstanceState.Degraded,
                WorkloadInstanceState.Failed,
                WorkloadInstanceState.VersionSkew,
                WorkloadInstanceState.Deploying,
                WorkloadInstanceState.PendingConfiguration,
                WorkloadInstanceState.ExternallyManaged,
                WorkloadInstanceState.Switching,
                WorkloadInstanceState.Suspended,
                WorkloadInstanceState.Terminating,
                WorkloadInstanceState.Unknown),

            [WorkloadInstanceState.Degraded] = Set(
                WorkloadInstanceState.Healthy,
                WorkloadInstanceState.Failed,
                WorkloadInstanceState.VersionSkew,
                WorkloadInstanceState.Deploying,
                WorkloadInstanceState.PendingConfiguration,
                WorkloadInstanceState.Switching,
                WorkloadInstanceState.Suspended,
                WorkloadInstanceState.Terminating,
                WorkloadInstanceState.Unknown),

            [WorkloadInstanceState.Failed] = Set(
                WorkloadInstanceState.Healthy,
                WorkloadInstanceState.Degraded,
                WorkloadInstanceState.Deploying,
                WorkloadInstanceState.VersionSkew,
                WorkloadInstanceState.Suspended,
                WorkloadInstanceState.Terminating,
                WorkloadInstanceState.Unknown),

            [WorkloadInstanceState.VersionSkew] = Set(
                WorkloadInstanceState.Healthy,
                WorkloadInstanceState.Degraded,
                WorkloadInstanceState.Failed,
                WorkloadInstanceState.Deploying,
                WorkloadInstanceState.ExternallyManaged,
                WorkloadInstanceState.Suspended,
                WorkloadInstanceState.Terminating,
                WorkloadInstanceState.Unknown),

            [WorkloadInstanceState.ExternallyManaged] = Set(
                WorkloadInstanceState.Healthy,
                WorkloadInstanceState.Degraded,
                WorkloadInstanceState.VersionSkew,
                WorkloadInstanceState.Deploying,
                WorkloadInstanceState.Suspended,
                WorkloadInstanceState.Terminating,
                WorkloadInstanceState.Unknown),

            [WorkloadInstanceState.Suspended] = Set(
                WorkloadInstanceState.Deploying,
                WorkloadInstanceState.Healthy,
                WorkloadInstanceState.Terminating,
                WorkloadInstanceState.Unknown),

            [WorkloadInstanceState.Terminating] = Set(
                WorkloadInstanceState.Unknown),

            [WorkloadInstanceState.Unknown] = Set(
                WorkloadInstanceState.Healthy,
                WorkloadInstanceState.Degraded,
                WorkloadInstanceState.Failed,
                WorkloadInstanceState.VersionSkew,
                WorkloadInstanceState.ExternallyManaged,
                WorkloadInstanceState.Deploying,
                WorkloadInstanceState.Suspended,
                WorkloadInstanceState.Terminating),

            [WorkloadInstanceState.AdoptionPending] = Set(
                WorkloadInstanceState.Deploying,
                WorkloadInstanceState.Suspended,
                WorkloadInstanceState.Terminating,
                WorkloadInstanceState.Unknown),

            [WorkloadInstanceState.Switching] = Set(
                WorkloadInstanceState.ExternallyManaged,
                WorkloadInstanceState.Healthy,
                WorkloadInstanceState.Degraded,
                WorkloadInstanceState.Terminating)
        };

    /// <summary>True when moving between the two states is permitted. A self-transition is treated as valid here:
    /// at the entity level it is a reason and message refresh.</summary>
    public static bool IsValidTransition(WorkloadInstanceState from, WorkloadInstanceState to)
    {
        if (from == to)
        {
            return true;
        }

        return Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
    }

    /// <summary>The set of states reachable in one step from <paramref name="from"/> (excluding itself).</summary>
    public static IReadOnlySet<WorkloadInstanceState> AllowedTargets(WorkloadInstanceState from)
        => Allowed.TryGetValue(from, out var targets) ? targets : Set();

    private static IReadOnlySet<WorkloadInstanceState> Set(params WorkloadInstanceState[] states)
        => new HashSet<WorkloadInstanceState>(states);
}
