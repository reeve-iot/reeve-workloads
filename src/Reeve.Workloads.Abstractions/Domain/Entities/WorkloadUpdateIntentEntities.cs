using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Abstractions.Domain.Entities;

internal enum WorkloadUpdatePhase
{
    StagingParams = 0,

    ApplyingSidecar = 1,

    HealthGate = 2,

    SwappingModule = 3,

    Completed = 4,

    RollingBack = 5,

    RolledBack = 6,

    RollbackFailed = 7
}

internal enum WorkloadUpdateStepState
{
    Pending = 0,

    Applying = 1,

    GateWaiting = 2,

    Swapping = 3,

    Done = 4,

    RolledBack = 5,

    Failed = 6
}

/// <summary>How one workload is getting on in its update (<see cref="WorkloadUpdateIntentStep.Progress"/>).</summary>
public enum WorkloadUpdateStepProgress
{
    /// <summary>Its part of the update has not started.</summary>
    Pending = 0,

    /// <summary>Its new spec is being applied, gated, or waited on.</summary>
    Working = 1,

    /// <summary>The update completed for it.</summary>
    Done = 2,

    /// <summary>It was put back to its previous spec.</summary>
    Undone = 3,

    /// <summary>Its apply, gate or rollback failed.</summary>
    Failed = 4,
}

/// <summary>How an update ended (<see cref="WorkloadUpdateIntent.Outcome"/>), or that it has not.</summary>
public enum WorkloadUpdateOutcome
{
    /// <summary>Still walking, in either direction.</summary>
    InProgress = 0,

    /// <summary>Every stage ran forward: the new version is installed and its parameters committed.</summary>
    Completed = 1,

    /// <summary>The walk turned round and undid every stage it had entered: the previous version runs again.</summary>
    RolledBack = 2,

    /// <summary>The walk could not finish: a rollback that failed or that an operator gave up on, or a walk ended where
    /// it stood without one — for example a failure past the point of no return, an abandon the walk could not turn,
    /// or an intent the boot resumer could not resume. Nothing further is done automatically.</summary>
    RollbackFailed = 3,
}

/// <summary>Which way an update's walk is going (<see cref="WorkloadUpdateIntent.Direction"/>).</summary>
public enum WorkloadUpdateDirection
{
    /// <summary>Towards the new version.</summary>
    Forward = 0,

    /// <summary>Undoing what it entered, back to the previous version.</summary>
    RollingBack = 1,
}

/// <summary>What an operator's abandon actually did. The outcome depends on where the intent stood, so the caller
/// (API/UI) can report it honestly.</summary>
public enum WorkloadUpdateAbandonOutcome
{
    /// <summary>The intent moved onto the rollback branch, which will now restore the previous last-known-good.
    /// NOT finished — the intent is still active until it settles.</summary>
    RollbackRequested = 0,

    /// <summary>The intent was already rolling back and the operator gave up on it: terminalized straight to
    /// <see cref="WorkloadUpdatePhase.RollbackFailed"/>. Nothing further will be restored automatically.</summary>
    RollbackAbandoned = 1,

    /// <summary>The walk could neither be rolled back nor carried on by the build that took the abandon — the stage it
    /// is at is one that build does not register, or its row does not say which stages ran — so it was ended where it
    /// stood: <see cref="WorkloadUpdatePhase.RollbackFailed"/>, nothing undone. Never returned by
    /// <see cref="WorkloadUpdateIntent.Abandon"/> itself; the abandon service decides it.</summary>
    EndedWithoutRollback = 2,
}

/// <summary>A durable record orchestrating one atomic plugin and sidecar upgrade. Resumability is durable: a new
/// leader reconstructs the previous spec from this row. At most one active intent per instance, enforced by index.</summary>
public sealed class WorkloadUpdateIntent
{
    /// <summary>Max persisted length of the error text, matching the column, so a long provider event or stack
    /// trace is truncated rather than overflowing the write.</summary>
    public const int MaxErrorLength = 4000;

    /// <summary>Max persisted length of <see cref="WalkFault"/>, matching the column.</summary>
    public const int MaxWalkFaultLength = 2000;

    private readonly List<WorkloadUpdateIntentStep> _steps = new();

    private WorkloadUpdateIntent()
    {
    }

    private WorkloadUpdateIntent(
        Guid id,
        Guid pluginInstanceId,
        string? fromVersion,
        string toVersion,
        DateTimeOffset createdAtUtc)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Intent id is required.", nameof(id)) : id;
        GroupInstanceId = pluginInstanceId == Guid.Empty
            ? throw new ArgumentException("Plugin instance id is required.", nameof(pluginInstanceId))
            : pluginInstanceId;
        ToVersion = string.IsNullOrWhiteSpace(toVersion)
            ? throw new ArgumentException("ToVersion is required.", nameof(toVersion))
            : toVersion.Trim();
        FromVersion = string.IsNullOrWhiteSpace(fromVersion) ? null : fromVersion.Trim();

        Phase = WorkloadUpdatePhase.StagingParams;
        CreatedAtUtc = createdAtUtc;
        PhaseEnteredAtUtc = createdAtUtc;
    }

    /// <summary>Factory for a freshly-created intent.</summary>
    public static WorkloadUpdateIntent Create(
        Guid id,
        Guid pluginInstanceId,
        string? fromVersion,
        string toVersion,
        DateTimeOffset createdAtUtc)
        => new(id, pluginInstanceId, fromVersion, toVersion, createdAtUtc);

    /// <summary>Factory for one member of an ordered update cohort.</summary>
    public static WorkloadUpdateIntent Create(
        Guid id,
        Guid groupInstanceId,
        string? fromVersion,
        string toVersion,
        DateTimeOffset createdAtUtc,
        WorkloadUpdateCohortPlace cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        if (cohort.CohortId == Guid.Empty)
        {
            throw new ArgumentException("Cohort id is required.", nameof(cohort));
        }

        if (cohort.Size <= 0 || cohort.Ordinal < 0 || cohort.Ordinal >= cohort.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(cohort), "Cohort ordinal must be within its positive size.");
        }

        var intent = new WorkloadUpdateIntent(id, groupInstanceId, fromVersion, toVersion, createdAtUtc)
        {
            CohortId = cohort.CohortId,
            CohortOrdinal = cohort.Ordinal,
            CohortSize = cohort.Size,
        };
        return intent;
    }

    public Guid Id { get; private set; }

    /// <summary>The plugin instance whose atomic upgrade this intent orchestrates (the active-intent uniqueness key).</summary>
    public Guid GroupInstanceId { get; private set; }

    /// <summary>The cohort shared by this intent and its siblings, or null for a single intent.</summary>
    public Guid? CohortId { get; private set; }

    /// <summary>This member's zero-based place in the cohort's gating order.</summary>
    public int? CohortOrdinal { get; private set; }

    /// <summary>The number of members staged into this cohort.</summary>
    public int? CohortSize { get; private set; }

    /// <summary>The version being upgraded FROM, or <c>null</c> (e.g. away from an adopted pre-Workloads image).</summary>
    public string? FromVersion { get; private set; }

    /// <summary>The version being upgraded TO (required).</summary>
    public string ToVersion { get; private set; } = null!;

    internal WorkloadUpdatePhase Phase { get; private set; }

    /// <summary>A human/machine-readable error for a failed/rolled-back/rollback-failed intent; capped + truncated.</summary>
    public string? Error { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Set exactly on the terminal transitions (<see cref="Complete"/>, → RolledBack, → RollbackFailed).</summary>
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>When the CURRENT phase was entered, re-stamped on every transition. Anchors the park-age and
    /// health-gate clocks.</summary>
    public DateTimeOffset PhaseEnteredAtUtc { get; private set; }

    /// <summary>Stamped at the START of the sidecar apply — the gate anchor: only a candidate observed AFTER this
    /// instant counts as evidence. Anchoring at start avoids racing a sidecar that comes up during the rollout.</summary>
    public DateTimeOffset? SidecarAppliedAtUtc { get; private set; }

    public Documents.WorkloadSpecMapDocument? TargetSpecsDocument { get; private set; }

    public Documents.WorkloadSpecMapDocument? PreviousSpecsDocument { get; private set; }

    public Documents.WorkloadParameterMapDocument? PreviousParametersDocument { get; private set; }

    public Documents.WorkloadStringMapDocument? PreviousPasswordParameterNamesDocument { get; private set; }

    public Documents.WorkloadStringMapDocument? CandidatePasswordParameterNamesDocument { get; private set; }

    public Documents.WorkloadStringMapDocument? OperatorParametersDocument { get; private set; }

    public IReadOnlyList<string>? AdoptedResourceWorkloadIds { get; private set; }

    public Documents.WorkloadStringMapDocument? FromDigestsDocument { get; private set; }

    public Documents.WorkloadStringMapDocument? ToDigestsDocument { get; private set; }

    public Documents.WorkloadUpdateEvidenceDocument? EvidenceDocument { get; private set; }

    /// <summary>The update walk's journal: which stages were entered, in which direction, and how each call went.
    /// Written empty when the update is staged, so a row without one is not walked. Read it with
    /// <see cref="ReadStageJournal"/>, which decides rather than degrades.</summary>
    public Documents.PipelineJournalDocument? StageJournalDocument { get; private set; }

    /// <summary>Why the walk's latest attempt on this open intent got no answer from a stage: the plan could not be
    /// built, or the tick threw. Null while the walk is moving; a tick that gets a stage's answer clears it, and a
    /// terminal transition clears it. Capped at <see cref="MaxWalkFaultLength"/>.</summary>
    public string? WalkFault { get; private set; }

    /// <summary>When the walk's current run of failed attempts began: set by the first failed attempt after a tick
    /// that got a stage's answer, kept by the ones after it, and cleared with <see cref="WalkFault"/>.</summary>
    public DateTimeOffset? WalkFaultSinceUtc { get; private set; }

    /// <summary>Optimistic-concurrency version token, incremented on every phase transition + snapshot/evidence
    /// mutation.</summary>
    public int Version { get; private set; }

    /// <summary>The per-workload progress rows. Read-only to callers; mutated via <see cref="AddStep"/>.</summary>
    public IReadOnlyCollection<WorkloadUpdateIntentStep> Steps => _steps;

    /// <summary>True once the intent has reached any terminal phase — no further advance is legal.</summary>
    public bool IsTerminal => Phase is WorkloadUpdatePhase.Completed
        or WorkloadUpdatePhase.RolledBack
        or WorkloadUpdatePhase.RollbackFailed;

    /// <summary>True while the intent is non-terminal — the state the filtered unique index reads.</summary>
    public bool IsActive => !IsTerminal;

    /// <summary>How the update ended, or that it has not.</summary>
    public WorkloadUpdateOutcome Outcome => Phase switch
    {
        WorkloadUpdatePhase.Completed => WorkloadUpdateOutcome.Completed,
        WorkloadUpdatePhase.RolledBack => WorkloadUpdateOutcome.RolledBack,
        WorkloadUpdatePhase.RollbackFailed => WorkloadUpdateOutcome.RollbackFailed,
        _ => WorkloadUpdateOutcome.InProgress,
    };

    /// <summary>Which way the walk was asked to go. An abandon sets it at once, before the walk has turned round, so
    /// an operator who pressed it sees the rollback asked for. On a failed ending the journal settles it: forward means
    /// the walk never turned — it failed past its point of no return, or was ended by an abandon or at boot before it
    /// turned — so nothing was rolled back; a row with no readable journal (none stored, or one that
    /// cannot be read) reads as a rollback, even when an abandon ended it without one.
    /// It does not say whether the walk is undoing anything yet: the journal does.</summary>
    public WorkloadUpdateDirection Direction => Phase switch
    {
        WorkloadUpdatePhase.RollingBack or WorkloadUpdatePhase.RolledBack => WorkloadUpdateDirection.RollingBack,
        WorkloadUpdatePhase.RollbackFailed when ReadStageJournal() is
            { Verdict: Documents.WorkloadDocumentVerdict.Value, Value.Direction: Reeve.Pipeline.PipelineDirection.Forward }
            => WorkloadUpdateDirection.Forward,
        WorkloadUpdatePhase.RollbackFailed => WorkloadUpdateDirection.RollingBack,
        _ => WorkloadUpdateDirection.Forward,
    };

    /// <summary>The id of the stage the stored journal last entered, or null before it entered any. A stage is entered on the row before it is first called, so it
    /// may not have been called yet. The update responses read the row as the walk does instead, and differ from this
    /// for a row nothing accounts for, for which they report none.</summary>
    public string? CurrentStage => ReadStageJournal() is
        { Verdict: Documents.WorkloadDocumentVerdict.Value, Value.Entries: [.., var last] }
            ? last.StageId
            : null;

    internal void AdvanceTo(WorkloadUpdatePhase target, DateTimeOffset nowUtc)
        => Transition(target, nowUtc);

    /// <summary>Completes the intent, legal only from the swapping phase, and stamps the completion time. The
    /// parameters were committed by <c>stage.commit-params</c>, the walk's last stage, before this.</summary>
    public void Complete(DateTimeOffset nowUtc)
        => Transition(WorkloadUpdatePhase.Completed, nowUtc);

    /// <summary>Fails an active in-flight intent into <see cref="WorkloadUpdatePhase.RollingBack"/>.</summary>
    public void Fail(string error, DateTimeOffset nowUtc)
    {
        Transition(WorkloadUpdatePhase.RollingBack, nowUtc);
        Error = Truncate(error);
    }

    /// <summary>Marks the terminal rollback-failed phase and records the truncated error. Legal from rolling back —
    /// a rollback that could not complete — and from the swapping phase, for a walk that failed past its point of no
    /// return and so was never turned round.</summary>
    public void MarkRollbackFailed(string error, DateTimeOffset nowUtc)
    {
        Transition(WorkloadUpdatePhase.RollbackFailed, nowUtc);
        Error = Truncate(error);
    }

    internal void MarkSwappingModuleUnresumable(string error, DateTimeOffset nowUtc)
    {
        if (Phase != WorkloadUpdatePhase.SwappingModule)
        {
            throw new WorkloadUpdateIntentTransitionException(Phase, WorkloadUpdatePhase.RollbackFailed,
                "MarkSwappingModuleUnresumable is legal only from SwappingModule.");
        }

        Transition(WorkloadUpdatePhase.RollbackFailed, nowUtc);
        Error = Truncate(error);
    }

    /// <summary>The OPERATOR ESCAPE from an update that cannot advance: it composes edges the machine already
    /// declares. It does not decide whether the walk may still be unwound — a stage it entered may forbid that, and
    /// only the pipeline knows which; the abandon service asks it first.</summary>
    public WorkloadUpdateAbandonOutcome Abandon(string reason, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("An abandon reason is required.", nameof(reason));
        }

        switch (Phase)
        {
            case WorkloadUpdatePhase.StagingParams:
            case WorkloadUpdatePhase.ApplyingSidecar:
            case WorkloadUpdatePhase.HealthGate:
            case WorkloadUpdatePhase.SwappingModule:
                Fail(reason, nowUtc);
                return WorkloadUpdateAbandonOutcome.RollbackRequested;

            case WorkloadUpdatePhase.RollingBack:
                MarkRollbackFailed(reason, nowUtc);
                return WorkloadUpdateAbandonOutcome.RollbackAbandoned;

            default:
                throw new WorkloadUpdateIntentTransitionException(Phase, WorkloadUpdatePhase.RollingBack,
                    $"The intent has already settled in {Phase} — there is nothing to abandon.");
        }
    }

    /// <summary>Persists the staging snapshots and rendered specs. Usable ONLY while staging, since snapshots are
    /// committed once, before the sidecar apply. Does not itself advance the phase.</summary>
    public void StageSnapshots(WorkloadUpdateSnapshots snapshots, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        if (Phase != WorkloadUpdatePhase.StagingParams)
        {
            throw new WorkloadUpdateIntentTransitionException(Phase, Phase,
                "Snapshots may be staged only while the intent is in StagingParams.");
        }

        TargetSpecsDocument = snapshots.TargetSpecs is { } target
            ? Documents.WorkloadDocumentMapper.SpecMap(target) : null;
        PreviousSpecsDocument = snapshots.PreviousSpecs is { } previousSpecs
            ? Documents.WorkloadDocumentMapper.SpecMap(previousSpecs) : null;
        PreviousParametersDocument = snapshots.PreviousParameters is { } previousParameters
            ? Documents.WorkloadDocumentMapper.ParameterMap(previousParameters) : null;
        PreviousPasswordParameterNamesDocument = Documents.WorkloadDocumentMapper.StringMap(
            snapshots.PreviousPasswordParameterNames.ToDictionary(
                pair => pair.Key,
                pair => (string?)JsonSerializer.Serialize(pair.Value, WorkloadEntityJsonContext.Default.IReadOnlyListString),
                StringComparer.Ordinal));
        CandidatePasswordParameterNamesDocument = Documents.WorkloadDocumentMapper.StringMap(
            snapshots.CandidatePasswordParameterNames.ToDictionary(
                pair => pair.Key,
                pair => (string?)JsonSerializer.Serialize(pair.Value, WorkloadEntityJsonContext.Default.IReadOnlyListString),
                StringComparer.Ordinal));
        OperatorParametersDocument = snapshots.OperatorParameters is { } operatorParameters
            ? Documents.WorkloadDocumentMapper.StringMap(operatorParameters) : null;
        FromDigestsDocument = snapshots.FromDigests is { } fromDigests
            ? Documents.WorkloadDocumentMapper.StringMap(fromDigests) : null;
        ToDigestsDocument = snapshots.ToDigests is { } toDigests
            ? Documents.WorkloadDocumentMapper.StringMap(toDigests) : null;
        AdoptedResourceWorkloadIds = snapshots.AdoptedResourceWorkloadIds?.ToList();
        _ = nowUtc;
        Version++;
    }

    internal void StageTargetSpecs(
        IReadOnlyDictionary<string, DesiredSpec.WorkloadDesiredSpec> targetSpecs,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(targetSpecs);
        if (Phase != WorkloadUpdatePhase.StagingParams)
        {
            throw new WorkloadUpdateIntentTransitionException(Phase, Phase,
                "Target specs may be staged only while the intent is in StagingParams.");
        }

        if (TargetSpecsDocument is not null)
        {
            return;
        }

        TargetSpecsDocument = Documents.WorkloadDocumentMapper.SpecMap(targetSpecs);
        _ = nowUtc;
        Version++;
    }

    public Documents.WorkloadDocumentReading<IReadOnlyDictionary<string, DesiredSpec.WorkloadDesiredSpec>> ReadTargetSpecs()
        => Documents.WorkloadDocumentRead.Decide(
            TargetSpecsDocument, Documents.WorkloadDocumentMapper.FromDocument);

    public Documents.WorkloadDocumentReading<IReadOnlyDictionary<string, DesiredSpec.WorkloadDesiredSpec>> ReadPreviousSpecs()
        => Documents.WorkloadDocumentRead.Decide(
            PreviousSpecsDocument, Documents.WorkloadDocumentMapper.FromDocument);

    /// <summary>The walk's journal. ABSENT on a row the walk never ticked, which is a fresh start; UNREADABLE is a
    /// verdict against the intent, never an empty journal — replaying forwards from an empty one over a walk that
    /// was part-way through compensation would redo work it had undone.</summary>
    public Documents.WorkloadDocumentReading<Reeve.Pipeline.PipelineJournal> ReadStageJournal()
        => Documents.WorkloadDocumentRead.Decide(
            StageJournalDocument, Documents.WorkloadDocumentMapper.FromDocument);

    /// <summary>The operator's submitted parameters, or null when none were recorded.</summary>
    public IReadOnlyDictionary<string, string?>? ReadOperatorParameters()
        => Documents.WorkloadDocumentRead.Read(
            OperatorParametersDocument,
            document => (IReadOnlyDictionary<string, string?>?)Documents.WorkloadDocumentMapper.FromDocument(document),
            () => null);

    /// <summary>The previous-parameters snapshot this intent rolls back to.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>? ReadPreviousParameters()
        => Documents.WorkloadDocumentRead.Read(
            PreviousParametersDocument,
            document => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>?)
                Documents.WorkloadDocumentMapper.FromDocument(document),
            () => null);

    /// <summary>Password names from the previous workload schemas, needed to protect rollback writes.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ReadPreviousPasswordParameterNames()
        => Documents.WorkloadDocumentRead.Read(
            PreviousPasswordParameterNamesDocument,
            document => (IReadOnlyDictionary<string, IReadOnlyList<string>>)Documents.WorkloadDocumentMapper
                .FromDocument(document).ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)(string.IsNullOrEmpty(pair.Value)
                        ? Array.Empty<string>()
                        : JsonSerializer.Deserialize(pair.Value!, WorkloadEntityJsonContext.Default.StringArray) ?? Array.Empty<string>()),
                    StringComparer.Ordinal),
            () => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    /// <summary>Password names declared by the candidate schema for active intent replay.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ReadCandidatePasswordParameterNames()
        => Documents.WorkloadDocumentRead.Read(
            CandidatePasswordParameterNamesDocument,
            document => (IReadOnlyDictionary<string, IReadOnlyList<string>>)Documents.WorkloadDocumentMapper
                .FromDocument(document).ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)(string.IsNullOrEmpty(pair.Value)
                        ? Array.Empty<string>()
                        : JsonSerializer.Deserialize(pair.Value!, WorkloadEntityJsonContext.Default.StringArray) ?? Array.Empty<string>()),
                    StringComparer.Ordinal),
            () => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    /// <summary>The image digests the update starts from. Degrades to empty, as its column reader did.</summary>
    public IReadOnlyDictionary<string, string?> ReadFromDigests()
        => Documents.WorkloadDocumentRead.Read(
            FromDigestsDocument, Documents.WorkloadDocumentMapper.FromDocument, EmptyDigests);

    /// <summary>The image digests the update moves to. Degrades to empty, as its column reader did.</summary>
    public IReadOnlyDictionary<string, string?> ReadToDigests()
        => Documents.WorkloadDocumentRead.Read(
            ToDigestsDocument, Documents.WorkloadDocumentMapper.FromDocument, EmptyDigests);

    /// <summary>The rollback diagnostics captured before teardown.</summary>
    public Update.WorkloadUpdateEvidence? ReadEvidence()
        => Documents.WorkloadDocumentRead.Read(
            EvidenceDocument,
            document => (Update.WorkloadUpdateEvidence?)Documents.WorkloadDocumentMapper.FromDocument(document),
            () => null);

    private static IReadOnlyDictionary<string, string?> EmptyDigests()
        => new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>The staged adopt decision, or empty. Nothing here has to decode anything.</summary>
    public IReadOnlyCollection<string> GetAdoptedResourceWorkloadIds()
        => AdoptedResourceWorkloadIds ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>Captures rollback diagnostics BEFORE teardown. Usable ONLY in <see
    /// cref="WorkloadUpdatePhase.RollingBack"/> (throws otherwise). Null clears what was captured.</summary>
    public void CaptureEvidence(Update.WorkloadUpdateEvidence? evidence)
    {
        if (Phase != WorkloadUpdatePhase.RollingBack)
        {
            throw new WorkloadUpdateIntentTransitionException(Phase, Phase,
                "Evidence is captured only while rolling back, before teardown.");
        }

        EvidenceDocument = evidence is null ? null : Documents.WorkloadDocumentMapper.Evidence(evidence);
        Version++;
    }

    /// <summary>Records the walk's journal after a tick. The caller saves the row; this moves the concurrency token
    /// so two leaders' ticks cannot both land, the second over the first's journal.</summary>
    public void RecordStageJournal(Reeve.Pipeline.PipelineJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        StageJournalDocument = Documents.WorkloadDocumentMapper.Journal(journal);
        Version++;
    }

    internal bool RecordWalkFault(string fault, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(fault))
        {
            throw new ArgumentException("A walk fault needs a description.", nameof(fault));
        }

        if (IsTerminal)
        {
            throw new WorkloadUpdateIntentTransitionException(Phase, Phase,
                "A walk fault is recorded only on an open intent.");
        }

        var recorded = fault.Length > MaxWalkFaultLength ? fault[..MaxWalkFaultLength] : fault;
        if (WalkFaultSinceUtc is not null && string.Equals(WalkFault, recorded, StringComparison.Ordinal))
        {
            return false;
        }

        WalkFault = recorded;
        WalkFaultSinceUtc ??= nowUtc;
        Version++;
        return true;
    }

    internal void ClearWalkFault()
    {
        if (WalkFaultSinceUtc is null && WalkFault is null)
        {
            return;
        }

        WalkFault = null;
        WalkFaultSinceUtc = null;
        Version++;
    }

    /// <summary>Adds a per-workload step row to the intent for declaration-order progress tracking.</summary>
    public void AddStep(WorkloadUpdateIntentStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.IntentId != Id)
        {
            throw new ArgumentException("Step belongs to a different intent.", nameof(step));
        }

        _steps.Add(step);
    }

    /// <summary>Stamps the HealthGate apply-anchor (<see cref="SidecarAppliedAtUtc"/>).</summary>
    public void StampSidecarApplyStart(DateTimeOffset nowUtc)
    {
        if (Phase == WorkloadUpdatePhase.ApplyingSidecar && SidecarAppliedAtUtc is null)
        {
            SidecarAppliedAtUtc = nowUtc;
        }
    }

    private void Transition(WorkloadUpdatePhase target, DateTimeOffset nowUtc)
    {
        if (!WorkloadUpdateIntentPhaseMachine.IsValidTransition(Phase, target))
        {
            throw new WorkloadUpdateIntentTransitionException(Phase, target);
        }

        if (Phase == WorkloadUpdatePhase.ApplyingSidecar
            && target == WorkloadUpdatePhase.HealthGate
            && SidecarAppliedAtUtc is null)
        {
            SidecarAppliedAtUtc = CreatedAtUtc;
        }

        Phase = target;
        PhaseEnteredAtUtc = nowUtc;
        if (IsTerminal)
        {
            CompletedAtUtc = nowUtc;
            WalkFault = null;
            WalkFaultSinceUtc = null;
            OperatorParametersDocument?.Entries.Clear();
            AdoptedResourceWorkloadIds = null;
            PreviousParametersDocument?.Workloads.Clear();
            PreviousPasswordParameterNamesDocument?.Entries.Clear();
            CandidatePasswordParameterNamesDocument?.Entries.Clear();
        }

        Version++;
    }

    private static string? Truncate(string? error)
        => error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>One per-workload progress row of an update intent, recording where each workload of a multi-workload
/// plugin is within the phase progression, so a resuming leader knows exactly what still needs work.</summary>
public sealed class WorkloadUpdateIntentStep
{
    /// <summary>Persisted length cap of <see cref="Message"/> (matches the <c>varchar(2000)</c> column).</summary>
    public const int MaxMessageLength = 2000;

    private WorkloadUpdateIntentStep()
    {
    }

    private WorkloadUpdateIntentStep(Guid id, Guid intentId, string workloadId, DateTimeOffset createdAtUtc)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Step id is required.", nameof(id)) : id;
        IntentId = intentId == Guid.Empty
            ? throw new ArgumentException("Intent id is required.", nameof(intentId))
            : intentId;
        WorkloadId = string.IsNullOrWhiteSpace(workloadId)
            ? throw new ArgumentException("Workload id is required.", nameof(workloadId))
            : workloadId.Trim();

        Step = WorkloadUpdatePhase.StagingParams;
        State = WorkloadUpdateStepState.Pending;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    /// <summary>Factory for a freshly-created step. Starts <see cref="WorkloadUpdateStepState.Pending"/> at
    /// <see cref="WorkloadUpdatePhase.StagingParams"/> (nothing applied yet for this workload).</summary>
    public static WorkloadUpdateIntentStep Create(Guid id, Guid intentId, string workloadId, DateTimeOffset createdAtUtc)
        => new(id, intentId, workloadId, createdAtUtc);

    public Guid Id { get; private set; }

    /// <summary>FK to the owning <see cref="WorkloadUpdateIntent"/>.</summary>
    public Guid IntentId { get; private set; }

    /// <summary>The manifest workload id this step tracks; unique within the intent.</summary>
    public string WorkloadId { get; private set; } = null!;

    internal WorkloadUpdatePhase Step { get; private set; }

    internal WorkloadUpdateStepState State { get; private set; }

    /// <summary>How this workload is getting on, in the words a host reads.</summary>
    public WorkloadUpdateStepProgress Progress => State switch
    {
        WorkloadUpdateStepState.Pending => WorkloadUpdateStepProgress.Pending,
        WorkloadUpdateStepState.Applying
            or WorkloadUpdateStepState.GateWaiting
            or WorkloadUpdateStepState.Swapping => WorkloadUpdateStepProgress.Working,
        WorkloadUpdateStepState.Done => WorkloadUpdateStepProgress.Done,
        WorkloadUpdateStepState.RolledBack => WorkloadUpdateStepProgress.Undone,
        WorkloadUpdateStepState.Failed => WorkloadUpdateStepProgress.Failed,
        _ => throw new InvalidOperationException($"Step state {State} has no progress; add it to this map."),
    };

    /// <summary>Persisted length cap of <see cref="AppliedSpecHash"/> (matches the <c>varchar(128)</c> column).</summary>
    public const int MaxAppliedSpecHashLength = 128;

    /// <summary>Per-workload free-text diagnostics (capped + truncated), or null.</summary>
    public string? Message { get; private set; }

    /// <summary>The spec hash the provider actually APPLIED for this workload — the hash the gate certifies. Its
    /// own column so it is not overloaded onto the message; an empty value means "not recorded".</summary>
    public string? AppliedSpecHash { get; private set; }

    /// <summary>This step's gate clock, as the typed document that now stores it.</summary>
    public Documents.GateClockStateDocument? GateClockStateDocument { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    internal void Advance(WorkloadUpdatePhase step, WorkloadUpdateStepState state, string? message, DateTimeOffset nowUtc)
    {
        Step = step;
        State = state;
        Message = Truncate(message);
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>The gate clock this step persisted, for a leader that resumes mid-gate. An unreadable document
    /// degrades to a fresh clock, which is what the column codec it replaces returned for an unreadable column.</summary>
    public WorkloadGateClockState ReadPriorClockState()
        => Documents.WorkloadDocumentRead.Read(
            GateClockStateDocument, Documents.WorkloadDocumentMapper.FromDocument, () => new WorkloadGateClockState());

    /// <summary>Persists this workload's gate clock state, or null to clear, so the clock survives a leader
    /// failover mid-gate. Does not itself change the step or state — call <see cref="Advance"/> for that.</summary>
    public void SetGateClockState(WorkloadGateClockState? clock, DateTimeOffset nowUtc)
    {
        GateClockStateDocument = clock is null ? null : Documents.WorkloadDocumentMapper.GateClock(clock);
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Records the spec hash the provider APPLIED, so the gate certifies it after a leader failover. A
    /// blank value clears it back to "not recorded". Does not itself change the step or state.</summary>
    public void SetAppliedSpecHash(string? appliedSpecHash, DateTimeOffset nowUtc)
    {
        AppliedSpecHash = string.IsNullOrWhiteSpace(appliedSpecHash)
            ? null
            : TruncateHash(appliedSpecHash);
        UpdatedAtUtc = nowUtc;
    }

    private static string? Truncate(string? message)
        => message is { Length: > MaxMessageLength } ? message[..MaxMessageLength] : message;

    private static string? TruncateHash(string? hash)
        => hash is { Length: > MaxAppliedSpecHashLength } ? hash[..MaxAppliedSpecHashLength] : hash;
}

/// <summary>Thrown when an intent is asked for a phase move the machine forbids, or a snapshot mutation outside
/// its permitted phase. Fail-closed — the store never silently no-ops an illegal transition.</summary>
public sealed class WorkloadUpdateIntentTransitionException : InvalidOperationException
{
    internal WorkloadUpdateIntentTransitionException(WorkloadUpdatePhase from, WorkloadUpdatePhase to)
        : base($"Illegal workload-update-intent phase transition: {from} → {to}.")
    {
        From = from;
        To = to;
    }

    internal WorkloadUpdateIntentTransitionException(WorkloadUpdatePhase from, WorkloadUpdatePhase to, string message)
        : base(message)
    {
        From = from;
        To = to;
    }

    internal WorkloadUpdatePhase From { get; }

    internal WorkloadUpdatePhase To { get; }
}

internal static class WorkloadUpdateIntentPhaseMachine
{
    private static readonly IReadOnlyDictionary<WorkloadUpdatePhase, IReadOnlySet<WorkloadUpdatePhase>> Allowed =
        new Dictionary<WorkloadUpdatePhase, IReadOnlySet<WorkloadUpdatePhase>>
        {
            [WorkloadUpdatePhase.StagingParams] = Set(
                WorkloadUpdatePhase.ApplyingSidecar,
                WorkloadUpdatePhase.RollingBack),

            [WorkloadUpdatePhase.ApplyingSidecar] = Set(
                WorkloadUpdatePhase.HealthGate,
                WorkloadUpdatePhase.RollingBack),

            [WorkloadUpdatePhase.HealthGate] = Set(
                WorkloadUpdatePhase.SwappingModule,
                WorkloadUpdatePhase.RollingBack),

            [WorkloadUpdatePhase.SwappingModule] = Set(
                WorkloadUpdatePhase.Completed,
                WorkloadUpdatePhase.RollingBack,
                WorkloadUpdatePhase.RollbackFailed),

            [WorkloadUpdatePhase.RollingBack] = Set(
                WorkloadUpdatePhase.RolledBack,
                WorkloadUpdatePhase.RollbackFailed),

            [WorkloadUpdatePhase.Completed] = Set(),
            [WorkloadUpdatePhase.RolledBack] = Set(),
            [WorkloadUpdatePhase.RollbackFailed] = Set()
        };

    public static bool IsValidTransition(WorkloadUpdatePhase from, WorkloadUpdatePhase to)
        => Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlySet<WorkloadUpdatePhase> AllowedTargets(WorkloadUpdatePhase from)
        => Allowed.TryGetValue(from, out var targets) ? targets : Set();

    private static IReadOnlySet<WorkloadUpdatePhase> Set(params WorkloadUpdatePhase[] phases)
        => new HashSet<WorkloadUpdatePhase>(phases);
}
