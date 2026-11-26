using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update.Stages;

internal sealed class SidecarStage(
    IWorkloadProviderRegistry providerRegistry,
    IWorkloadUpdateHealthGate healthGate,
    IWorkloadClock clock,
    WorkloadUpdateOrchestratorOptions options,
    IWorkloadInstanceRepository instanceRepository,
    UnpinnedReferenceRolloutGuard rolloutGuard,
    IWorkloadCredentialPort credentialPort)
    : IStage<WorkloadUpdateStageContext>
{
    public const int EvidenceLogTailLines = 200;

    private readonly IWorkloadProviderRegistry _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
    private readonly IWorkloadUpdateHealthGate _healthGate = healthGate ?? throw new ArgumentNullException(nameof(healthGate));
    private readonly IWorkloadClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly WorkloadUpdateOrchestratorOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IWorkloadInstanceRepository _instanceRepository = instanceRepository ?? throw new ArgumentNullException(nameof(instanceRepository));
    private readonly UnpinnedReferenceRolloutGuard _rolloutGuard = rolloutGuard ?? throw new ArgumentNullException(nameof(rolloutGuard));
    private readonly IWorkloadCredentialPort _credentialPort = credentialPort ?? throw new ArgumentNullException(nameof(credentialPort));

    public string Id => WorkloadUpdateStages.Sidecar;

    public StagePlacement Placement => StagePlacement.After(WorkloadUpdateStages.Params);

    public StageReversibility Reversibility => StageReversibility.Reversible;

    public StageBudget Budget => StageBudget.Of(_options.RollbackTimeout, StageTimeoutPolicy.FailAndCompensate);

    private static IReadOnlySet<string> CandidateNames(
        WorkloadUpdateIntent intent, Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadManifest workload)
        => WorkloadPasswordNames.For(workload, intent.ReadCandidatePasswordParameterNames().GetValueOrDefault(workload.Id));

    private static IReadOnlySet<string> PreviousNames(
        WorkloadUpdateIntent intent, Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadManifest workload)
        => WorkloadPasswordNames.For(workload, intent.ReadPreviousPasswordParameterNames().GetValueOrDefault(workload.Id));

    public async ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var internals = context.RequireInternals();
        var intent = internals.Intent;
        var plan = internals.Plan;
        var now = _clock.UtcNow;

        intent.StampSidecarApplyStart(intent.CreatedAtUtc);

        foreach (var item in plan.Workloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var step = WorkloadUpdateSteps.Find(intent, item.WorkloadId);
            if (step is { State: WorkloadUpdateStepState.GateWaiting } && !string.IsNullOrEmpty(step.AppliedSpecHash))
            {
                continue;
            }

            var provider = _providerRegistry.TryResolve(item.Reference.Provider);
            if (provider is null)
            {
                return StageOutcome.Working(
                    $"no provider registered for {item.Reference.Provider} to apply workload '{item.WorkloadId}'");
            }

            var instance = await _instanceRepository.GetByIdAsync(item.Reference.InstanceId, cancellationToken).ConfigureAwait(false);
            if (instance is { IsHeldByHost: true })
            {
                return StageOutcome.Working($"workload '{item.WorkloadId}' is held by the host; it is applied once the host resumes it");
            }

            if (instance is not null && plan.CandidateManifest.Workloads.FirstOrDefault(w => w.Id == item.WorkloadId) is { } targetWorkload)
            {
                var stored = plan.CandidateStoredParameters.GetValueOrDefault(item.WorkloadId)
                    ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                var resolved = plan.CandidateResolvedParameters.GetValueOrDefault(item.WorkloadId)
                    ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                var refs = plan.CandidateUnpinnedReferences.GetValueOrDefault(item.WorkloadId)
                    ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                var check = _rolloutGuard.Check(instance, refs, resolved,
                    CandidateNames(intent, targetWorkload));
                if (check.ChangedParameter is { } changed)
                {
                    var message = $"Password parameter '{changed}' reference value changed; roll out to apply.";
                    instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, message, now);
                    await _instanceRepository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
                    return StageOutcome.Working(message);
                }
            }

            WorkloadApplyResult applyResult;
            try
            {
                applyResult = await provider
                    .ApplyAsync(item.TargetSpec, progress: null, cancellationToken, forceConflicts: true)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return StageOutcome.Failed($"Apply of workload '{item.WorkloadId}' threw: {ex.Message}");
            }

            if (!applyResult.Succeeded)
            {
                if (IsTransient(applyResult.Reason))
                {
                    return StageOutcome.Working(
                        $"apply of workload '{item.WorkloadId}' was {applyResult.Reason}: {applyResult.Message}");
                }

                return StageOutcome.Failed(
                    $"Apply of workload '{item.WorkloadId}' failed ({applyResult.Reason}): {applyResult.Message}");
            }

            var appliedStep = WorkloadUpdateSteps.Ensure(intent, item.WorkloadId, now);
            if (instance is not null && plan.CandidateManifest.Workloads.FirstOrDefault(w => w.Id == item.WorkloadId) is { } appliedWorkload)
            {
                var stored = plan.CandidateStoredParameters.GetValueOrDefault(item.WorkloadId)
                    ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                var resolved = plan.CandidateResolvedParameters.GetValueOrDefault(item.WorkloadId)
                    ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                var refs = plan.CandidateUnpinnedReferences.GetValueOrDefault(item.WorkloadId)
                    ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                _rolloutGuard.RecordApplied(instance, refs, resolved,
                    CandidateNames(intent, appliedWorkload), now);
                await _instanceRepository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
            }
            appliedStep.SetAppliedSpecHash(applyResult.AppliedSpecHash ?? item.TargetSpec.SpecHash, now);
            appliedStep.Advance(
                WorkloadUpdatePhase.ApplyingSidecar, WorkloadUpdateStepState.GateWaiting, "sidecar applied — awaiting gate", now);
        }

        return StageOutcome.Done;
    }

    public async ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var internals = context.RequireInternals();
        var intent = internals.Intent;
        var plan = internals.Plan;

        if (intent.EvidenceDocument is null)
        {
            intent.CaptureEvidence(await CaptureEvidenceAsync(intent, plan, cancellationToken).ConfigureAwait(false));
        }

        foreach (var item in plan.Workloads.Reverse().OrderBy(item => item.PreviousSpec is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var provider = _providerRegistry.TryResolve(item.Reference.Provider);
            if (provider is null)
            {
                return StageOutcome.Working(
                    $"no provider registered for {item.Reference.Provider} to restore workload '{item.WorkloadId}'");
            }

            try
            {
                if (item.PreviousSpec is { } previous)
                {
                    var instance = await _instanceRepository.GetByIdAsync(item.Reference.InstanceId, cancellationToken).ConfigureAwait(false);
                    if (instance is { IsHeldByHost: true })
                    {
                        return StageOutcome.Working($"workload '{item.WorkloadId}' is held by the host; it is restored once the host resumes it");
                    }

                    var previousWorkload = plan.PreviousManifest.Workloads.FirstOrDefault(w => w.Id == item.WorkloadId);
                    var previousStored = plan.PreviousStoredParameters.GetValueOrDefault(item.WorkloadId)
                        ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                    var previousResolved = plan.PreviousResolvedParameters.GetValueOrDefault(item.WorkloadId)
                        ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                    var previousRefs = plan.PreviousUnpinnedReferences.GetValueOrDefault(item.WorkloadId)
                        ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                    if (instance is not null && previousWorkload is not null)
                    {
                        var check = _rolloutGuard.Check(instance, previousRefs, previousResolved,
                            PreviousNames(intent, previousWorkload));
                        if (check.ChangedParameter is { } changed)
                        {
                            var message = $"Password parameter '{changed}' reference value changed; roll out to apply.";
                            instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, message, _clock.UtcNow);
                            await _instanceRepository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
                            return StageOutcome.Working(message);
                        }
                    }
                    var restore = await provider
                        .ApplyAsync(previous, progress: null, cancellationToken, forceConflicts: true)
                        .ConfigureAwait(false);

                    if (!restore.Succeeded)
                    {
                        if (IsTransient(restore.Reason))
                        {
                            return StageOutcome.Working(
                                $"restore of workload '{item.WorkloadId}' was {restore.Reason}: {restore.Message}");
                        }

                        return StageOutcome.Failed(
                            $"Restore of workload '{item.WorkloadId}' failed ({restore.Reason}): {restore.Message}");
                    }
                    if (instance is not null && previousWorkload is not null)
                    {
                        _rolloutGuard.RecordApplied(instance, previousRefs, previousResolved,
                            PreviousNames(intent, previousWorkload), _clock.UtcNow);
                        await _instanceRepository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await provider.TeardownAsync(item.Reference, retainVolumes: true, cancellationToken).ConfigureAwait(false);
                    await _credentialPort.RevokeAllAsync(item.Reference.InstanceId, actorUserId: null, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return StageOutcome.Working(item.PreviousSpec is null
                    ? $"teardown or credential revocation of added workload '{item.WorkloadId}' threw: {ex.Message}"
                    : $"restore of workload '{item.WorkloadId}' threw: {ex.Message}");
            }
        }

        var waiting = await NotYetHealthyAsync(intent, plan, cancellationToken).ConfigureAwait(false);
        return waiting.Count == 0
            ? StageOutcome.Done
            : StageOutcome.Working($"waiting for the previous version to be healthy again: {string.Join(", ", waiting)}");
    }

    private static bool IsTransient(WorkloadApplyReason reason) => reason == WorkloadApplyReason.ProviderUnreachable;

    private async Task<WorkloadUpdateEvidence> CaptureEvidenceAsync(
        WorkloadUpdateIntent intent, WorkloadUpdatePlan plan, CancellationToken cancellationToken)
    {
        var perWorkload = new List<WorkloadEvidenceCapture>();

        foreach (var item in plan.Workloads)
        {
            var provider = _providerRegistry.TryResolve(item.Reference.Provider);
            if (provider is null)
            {
                perWorkload.Add(new WorkloadEvidenceCapture(
                    item.WorkloadId, [], [], "no provider registered to capture evidence"));
                continue;
            }

            IReadOnlyList<string> logLines = [];
            IReadOnlyList<string> events = [];
            string? note = null;
            try
            {
                var logs = await provider
                    .GetRecentLogsAsync(item.Reference, EvidenceLogTailLines, previousContainer: true, cancellationToken)
                    .ConfigureAwait(false);
                logLines = logs.Lines.Select(line => line.IsRestartMarker ? $"--- {line.Text} ---" : line.Text).ToList();

                var status = await provider.GetStatusAsync(item.Reference, cancellationToken).ConfigureAwait(false);
                events = status.RecentEvents.Select(e => $"[{e.Severity}] {e.Reason}: {e.Message}").ToList();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                note = $"evidence capture faulted: {ex.Message}";
            }

            perWorkload.Add(new WorkloadEvidenceCapture(item.WorkloadId, logLines, events, note));
        }

        return new WorkloadUpdateEvidence(intent.ToVersion, _clock.UtcNow, perWorkload);
    }

    private async Task<IReadOnlyList<string>> NotYetHealthyAsync(
        WorkloadUpdateIntent intent, WorkloadUpdatePlan plan, CancellationToken cancellationToken)
    {
        var waiting = new List<string>();

        foreach (var item in plan.Workloads)
        {
            if (item.PreviousSpec is not { } previous)
            {
                continue;
            }

            var request = new WorkloadHealthGateTickRequest
            {
                Reference = item.Reference,
                InstanceId = item.Reference.InstanceId,
                ExpectedSpecHash = previous.SpecHash,
                ApplyTimestampUtc = WorkloadUpdateOrchestrator.RollbackHealthNoApplyTimeGate,
                ToVersion = intent.FromVersion,
                CurrentExtensions = item.CurrentExtensions,
                CandidateExtensions = item.CurrentExtensions,
                Certification = item.PreviousCertification,
                Timeout = _options.HealthGateTimeout,
                PriorClockState = new(),
            };

            try
            {
                var tick = await _healthGate.EvaluateTickAsync(request, cancellationToken).ConfigureAwait(false);
                if (tick.Verdict != WorkloadHealthGateVerdict.Passed)
                {
                    waiting.Add(item.WorkloadId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                waiting.Add(item.WorkloadId);
            }
        }

        return waiting;
    }
}
