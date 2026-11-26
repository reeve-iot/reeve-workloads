using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Testing;

/// <summary>An in-memory fake IWorkloadProvider, letting the reconciler + lifecycle tasks be exercised
/// end-to-end before a real provider exists. Stands in for whichever provider the descriptor it is built with declares. Ships in Reeve.Workloads.Testing rather than in the engine, so the hooks below - SetStatus, SimulateReady, InjectPreflightFailure and the rest - are not part of what a host binds to in production. Registered in no DI container anywhere. Guarded by a single lock so the reconciler may call it concurrently.</summary>
public sealed class InMemoryWorkloadProvider : IWorkloadProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, InstanceRecord> _instances = new();
    private readonly Dictionary<Guid, IReadOnlyList<RetainedVolume>> _retainedVolumes = new();
    private readonly List<PreflightCheck> _injectedPreflightChecks = new();

    /// <summary>The id and traits this fake stands in for.</summary>
    public WorkloadProviderDescriptor Descriptor { get; }

    /// <summary>When true, applying a spec with an adopted claim whose PVC name isn't in
    /// PresentAdoptedClaimNames fails Rejected — mirroring KubernetesWorkloadProvider's fail-closed adopted-claim-absent path, so the reconciler's restore-refusal handling can be exercised without a real cluster. Off by default.</summary>
    public bool FailAbsentAdoptedClaims { get; set; }

    /// <summary>Test hook: when true, a fan-out ApplyAsync that creates a new record leaves it Ready
    /// (armed) but ScanRunTerminal=false — the real status mapper's reading of a freshly-created per-node Job set whose pods aren't scheduled yet. Lets engine tests reproduce a premature same-pass marker-consume. Off by default: a fresh apply reports Running, not yet ready.</summary>
    public bool ScanFreshlyCreatedReadsReadyNotTerminal { get; set; }

    /// <summary>The set of adopted-claim PVC names the fake treats as present when FailAbsentAdoptedClaims
    /// is on; an absent name is refused. Ignored when the flag is off.</summary>
    public HashSet<string> PresentAdoptedClaimNames { get; } = new(StringComparer.Ordinal);

    /// <param name="descriptor">The provider to stand in for, traits included.</param>
    /// <remarks>Required: this
/// package references no provider, and a bare descriptor built from an id alone carries the DEFAULTS — so
    /// standing in for <c>manual</c> that way would claim to materialise a runtime. Pass the real descriptor
    /// (<c>ManualProvider.Descriptor</c>), or build one with the traits the test is about.</remarks>
    public InMemoryWorkloadProvider(WorkloadProviderDescriptor descriptor)
        => Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));

    public Task<WorkloadApplyResult> ApplyAsync(
        WorkloadDesiredSpec spec,
        IProgress<WorkloadApplyProgress>? progress,
        CancellationToken cancellationToken = default,
        bool forceConflicts = false)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_gate)
        {
            if (FailAbsentAdoptedClaims)
            {
                var absent = spec.Volumes
                    .Where(v => v.ExistingClaim && !string.IsNullOrWhiteSpace(v.ResourceName))
                    .Select(v => v.ResourceName.Trim())
                    .FirstOrDefault(name => !PresentAdoptedClaimNames.Contains(name));
                if (absent is not null)
                {
                    return Task.FromResult(WorkloadApplyResult.Failed(
                        WorkloadApplyReason.Rejected,
                        $"adopted claim PVC '{absent}' does not exist (fail-closed, simulated)"));
                }
            }

            if (_instances.TryGetValue(spec.InstanceId, out var existing))
            {
                existing.Spec = spec;
                existing.ApplyCount++;
                existing.Suspended = false;
            }
            else
            {
                _instances[spec.InstanceId] = new InstanceRecord
                {
                    Spec = spec,
                    ApplyCount = 1,
                    Phase = ScanFreshlyCreatedReadsReadyNotTerminal ? WorkloadProviderPhase.Ready : WorkloadProviderPhase.Running,
                    ProviderReady = ScanFreshlyCreatedReadsReadyNotTerminal,
                    ScanRunTerminal = false,
                    RestartCount = 0,
                    ObservedImage = spec.Image,
                };
                _retainedVolumes.Remove(spec.InstanceId);
            }

            return Task.FromResult(WorkloadApplyResult.Applied(spec.SpecHash));
        }
    }

    public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(reference.InstanceId, out var record))
            {
                if (retainVolumes)
                {
                    var retained = ExtractRetainedVolumes(record.Spec);
                    if (retained.Count > 0)
                    {
                        _retainedVolumes[reference.InstanceId] = retained;
                    }
                }

                _instances.Remove(reference.InstanceId);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Marks the instance suspended without removing it — a suspended instance stays tracked
    /// (resources/volumes kept, resumable). Idempotent; a no-op for an untracked instance.</summary>
    public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(reference.InstanceId, out var record))
            {
                record.Suspended = true;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Clears the suspended flag so the instance reports whatever status was driven again.
    /// Idempotent. The reconciler re-applies the desired spec after resuming — this only lifts the suspend mechanism.</summary>
    public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(reference.InstanceId, out var record))
            {
                record.Suspended = false;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>The fleet read, answered under ONE lock. Overridden rather than inherited because the default
    /// descriptor declares <see cref="WorkloadProviderDescriptor.ReadsStatusInBatches"/>, and a double that says
    /// it batches while looping the SPI's default would make a host's test measure the wrong thing.</summary>
    public Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
        IReadOnlyList<WorkloadRef> references, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);

        var statuses = new Dictionary<WorkloadRef, WorkloadStatus>(references.Count);
        foreach (var reference in references)
        {
            statuses[reference] = GetStatusAsync(reference, cancellationToken).GetAwaiter().GetResult();
        }

        return Task.FromResult<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>>(statuses);
    }

    public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_instances.TryGetValue(reference.InstanceId, out var record))
            {
                return Task.FromResult(WorkloadStatus.NotFound($"No fake workload for instance {reference.InstanceId}."));
            }

            if (record.Suspended)
            {
                return Task.FromResult(new WorkloadStatus
                {
                    Phase = WorkloadProviderPhase.Pending,
                    ProviderReady = false,
                    RestartCount = record.RestartCount,
                    Message = record.Message ?? "Suspended — scaled to 0 / no-match nodeSelector (resources retained).",
                    ObservedImage = record.ObservedImage ?? record.Spec.Image,
                    RecentEvents = record.Events.ToList(),
                });
            }

            return Task.FromResult(new WorkloadStatus
            {
                Phase = record.Phase,
                ProviderReady = record.ProviderReady,
                ScanRunTerminal = record.ScanRunTerminal,
                ScanFailedNodeNames = record.ScanFailedNodeNames.ToList(),
                RestartCount = record.RestartCount,
                Message = record.Message,
                ObservedImage = record.ObservedImage ?? record.Spec.Image,
                RecentEvents = record.Events.ToList(),
            });
        }
    }

    public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (_gate)
        {
            if (_injectedPreflightChecks.Count == 0)
            {
                return Task.FromResult(WorkloadPreflightResult.Ok());
            }

            return Task.FromResult(new WorkloadPreflightResult { Checks = _injectedPreflightChecks.ToList() });
        }
    }

    public Task<WorkloadLogs> GetRecentLogsAsync(
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_instances.TryGetValue(reference.InstanceId, out var record))
            {
                return Task.FromResult(WorkloadLogs.Empty());
            }

            var source = previousContainer ? record.PreviousLogLines : record.CurrentLogLines;
            var lines = new List<WorkloadLogLine>();

            if (previousContainer && source.Count > 0)
            {
                lines.Add(WorkloadLogLine.RestartMarker("--- previous container instance ---"));
            }

            lines.AddRange(source);

            var truncated = false;
            if (tailLines > 0 && lines.Count > tailLines)
            {
                truncated = true;
                lines = lines.Skip(lines.Count - tailLines).ToList();
            }

            return Task.FromResult(new WorkloadLogs { Lines = lines, Truncated = truncated });
        }
    }

    /// <inheritdoc /><remarks>The fake's existence read: every name is reported as existing except those marked with <see cref="SetNetworkAttachmentMissing"/>.
    /// <see cref="AttachmentExistenceOverride"/> replaces it to model a faulting or non-applicable read. No I/O, starts nothing.</remarks>
    public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        lock (_gate)
        {
            AttachmentExistenceCallCount++;
        }

        if (AttachmentExistenceOverride is { } custom)
        {
            return Task.FromResult(custom(attachments));
        }

        string[] missing;
        lock (_gate)
        {
            missing = attachments.Where(_missingNetworkAttachments.Contains).ToArray();
        }

        return Task.FromResult(WorkloadAttachmentExistence.Read(missing));
    }

    /// <summary>Replaces the default existence read — used to model a faulting read (an Unreadable result) or a provider that cannot be asked.</summary>
    public Func<IReadOnlyList<string>, WorkloadAttachmentExistence>? AttachmentExistenceOverride { get; set; }

    /// <summary>How many times the apply-time existence read ran. The Manual arm asserts this stays
    /// at zero (no apply-time read at all); the converged arm asserts exactly one read per pass.</summary>
    public int AttachmentExistenceCallCount { get; private set; }

    private readonly HashSet<string> _missingNetworkAttachments = new(StringComparer.Ordinal);

    /// <summary>Models an attachment that no longer exists: the default existence read lists it as missing. <paramref name="missing"/> false restores it.</summary>
    public void SetNetworkAttachmentMissing(string name, bool missing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (missing)
            {
                _missingNetworkAttachments.Add(name);
            }
            else
            {
                _missingNetworkAttachments.Remove(name);
            }
        }
    }

    /// <summary>The number of times <see cref="ApplyAsync"/> ran for an instance (0 if never applied). Idempotence oracle.</summary>
    public int ApplyCountFor(Guid instanceId)
    {
        lock (_gate)
        {
            return _instances.TryGetValue(instanceId, out var record) ? record.ApplyCount : 0;
        }
    }

    /// <summary>The last desired spec applied for an instance, or null if none is live.</summary>
    public WorkloadDesiredSpec? LastAppliedSpec(Guid instanceId)
    {
        lock (_gate)
        {
            return _instances.TryGetValue(instanceId, out var record) ? record.Spec : null;
        }
    }

    /// <summary>True while a live record exists for the instance (false after teardown / before apply).</summary>
    public bool IsTracked(Guid instanceId)
    {
        lock (_gate)
        {
            return _instances.ContainsKey(instanceId);
        }
    }

    /// <summary>True while a tracked instance is suspended — the suspend/resume oracle for reconciler
    /// tests. False for an untracked or non-suspended instance.</summary>
    public bool IsSuspended(Guid instanceId)
    {
        lock (_gate)
        {
            return _instances.TryGetValue(instanceId, out var record) && record.Suspended;
        }
    }

    /// <summary>Drives the status a subsequent GetStatusAsync returns for a live instance. No-op for an
    /// untracked instance.</summary>
    public void SetStatus(
        Guid instanceId,
        WorkloadProviderPhase phase,
        bool providerReady,
        int restartCount = 0,
        string? message = null,
        IReadOnlyList<WorkloadProviderEvent>? events = null,
        bool scanRunTerminal = false,
        IReadOnlyList<string>? scanFailedNodeNames = null)
    {
        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var record))
            {
                return;
            }

            record.Phase = phase;
            record.ProviderReady = providerReady;
            record.ScanRunTerminal = scanRunTerminal;
            record.ScanFailedNodeNames = scanFailedNodeNames?.ToList() ?? new List<string>();
            record.RestartCount = restartCount < 0 ? 0 : restartCount;
            record.Message = message;
            if (events is not null)
            {
                record.Events = events.ToList();
            }
        }
    }

    /// <summary>Flips a live instance to provider-ready Ready — the provider half of reaching Healthy (under a
    /// host mode the host's contribution is the other half). No-op if untracked.</summary>
    public void SimulateReady(Guid instanceId)
        => SetStatus(instanceId, WorkloadProviderPhase.Ready, providerReady: true);

    /// <summary>Appends a provider event to a live instance's recent-events list. No-op if untracked.</summary>
    public void AppendEvent(Guid instanceId, WorkloadProviderEvent providerEvent)
    {
        ArgumentNullException.ThrowIfNull(providerEvent);
        lock (_gate)
        {
            if (_instances.TryGetValue(instanceId, out var record))
            {
                record.Events.Add(providerEvent);
            }
        }
    }

    /// <summary>Injects a preflight check that all subsequent PreflightAsync calls return, so a failing
    /// check with a fix snippet can be asserted. Call once per check to fail.</summary>
    public void InjectPreflightFailure(PreflightCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        lock (_gate)
        {
            _injectedPreflightChecks.Add(check);
        }
    }

    /// <summary>Clears all injected preflight checks (Preflight returns all-clear again).</summary>
    public void ClearPreflightFailures()
    {
        lock (_gate)
        {
            _injectedPreflightChecks.Clear();
        }
    }

    /// <summary>Sets the current-container log lines a subsequent <see cref="GetRecentLogsAsync"/> returns. No-op if untracked.</summary>
    public void SetLogs(Guid instanceId, IReadOnlyList<WorkloadLogLine> currentLines, IReadOnlyList<WorkloadLogLine>? previousLines = null)
    {
        ArgumentNullException.ThrowIfNull(currentLines);
        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var record))
            {
                return;
            }

            record.CurrentLogLines = currentLines.ToList();
            if (previousLines is not null)
            {
                record.PreviousLogLines = previousLines.ToList();
            }
        }
    }

    /// <summary>The retained-volume markers left by a retainVolumes:true teardown, keyed by instance id.
    /// Empty until a retaining teardown of an instance that had critical/persistent volumes.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<RetainedVolume>> RetainedVolumes
    {
        get
        {
            lock (_gate)
            {
                return _retainedVolumes.ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        }
    }

    private static IReadOnlyList<RetainedVolume> ExtractRetainedVolumes(WorkloadDesiredSpec spec)
        => spec.Volumes
            .Select(v => new RetainedVolume
            {
                VolumeId = v.Id,
                ResourceName = v.ResourceName,
                Size = v.Size,
                Criticality = v.Criticality,
            })
            .ToList();

    /// <summary>A retained-volume marker left by a retainVolumes:true teardown of the fake. NESTED on
    /// purpose: as a top-level type it was a second public RetainedVolume in the same package family as the
    /// real Domain/Entities one, and a consumer importing both namespaces had to fully-qualify the entity to
    /// say which it meant. Nested, it reads as what it is - this fake's marker - and cannot collide again.</summary>
    public sealed record RetainedVolume
    {
        public required string VolumeId { get; init; }
        public required string ResourceName { get; init; }
        public required string Size { get; init; }
        public required Reeve.Workloads.Abstractions.Contracts.Deployment.VolumeCriticality Criticality { get; init; }
    }

    private sealed class InstanceRecord
    {
        public required WorkloadDesiredSpec Spec { get; set; }
        public int ApplyCount { get; set; }
        public WorkloadProviderPhase Phase { get; set; }
        public bool ProviderReady { get; set; }

        public bool ScanRunTerminal { get; set; }

        public List<string> ScanFailedNodeNames { get; set; } = new();

        public bool Suspended { get; set; }
        public int RestartCount { get; set; }
        public string? Message { get; set; }
        public string? ObservedImage { get; set; }
        public List<WorkloadProviderEvent> Events { get; set; } = new();
        public List<WorkloadLogLine> CurrentLogLines { get; set; } = new();
        public List<WorkloadLogLine> PreviousLogLines { get; set; } = new();
    }
}
