using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;

namespace Reeve.Workloads.Core;

/// <summary>While an update intent is ACTIVE, the desired spec is the one the intent is converging on rather than a
/// fresh derivation: its recorded target going forward, so the drift pass cannot "correct" a long HealthGate back to
/// the old version, and its recorded previous spec while the walk undoes it, so the drift pass cannot re-apply the
/// version the rollback is removing. Reads only.</summary>
public sealed class WorkloadActiveIntentProvider(
    IWorkloadUpdateIntentRepository intentRepository,
    ILogger<WorkloadActiveIntentProvider>? logger = null)
    : IActiveWorkloadIntentProvider
{
    private readonly IWorkloadUpdateIntentRepository _intentRepository =
        intentRepository ?? throw new ArgumentNullException(nameof(intentRepository));

    private readonly ILogger<WorkloadActiveIntentProvider> _logger =
        logger ?? NullLogger<WorkloadActiveIntentProvider>.Instance;

    public async Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(
        Guid pluginInstanceId,
        string workloadId,
        CancellationToken cancellationToken)
        => (await TryGetActiveReplayAsync(pluginInstanceId, workloadId, cancellationToken).ConfigureAwait(false))?.Spec;

    public async Task<ActiveWorkloadIntentReplay?> TryGetActiveReplayAsync(
        Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadId);
        var intent = await _intentRepository.GetActiveForInstanceAsync(pluginInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (intent is null)
        {
            return null;
        }

        var unwinding = WalksBack(intent);
        var target = unwinding ? intent.ReadPreviousSpecs() : intent.ReadTargetSpecs();
        if (target is not { Verdict: WorkloadDocumentVerdict.Value, Value: { } specs }
            || !specs.TryGetValue(workloadId, out var spec))
        {
            if (target.Verdict == WorkloadDocumentVerdict.Unreadable)
            {
                _logger.LogError(
                    "Active update intent for instance {InstanceId} has a corrupt/undeserializable {Snapshot} spec snapshot ({WorkloadId}); computing the desired spec fresh this pass (the resumer terminalizes the poisoned intent at boot).",
                    pluginInstanceId, unwinding ? "previous" : "target", workloadId);
            }
            return null;
        }

        var submitted = !unwinding
            ? intent.ReadOperatorParameters() ?? new Dictionary<string, string?>()
            : new Dictionary<string, string?>();
        var previousNames = intent.ReadPreviousPasswordParameterNames();
        var candidateNames = intent.ReadCandidatePasswordParameterNames();
        var previousValues = intent.ReadPreviousParameters();
        var stored = unwinding
            ? previousValues?.GetValueOrDefault(workloadId)
            : null;
        return new ActiveWorkloadIntentReplay(
            spec,
            unwinding ? WorkloadUpdateDirection.RollingBack : WorkloadUpdateDirection.Forward,
            submitted)
        {
            StoredParameters = stored,
            PasswordParameterNames = unwinding
                ? previousNames.GetValueOrDefault(workloadId)
                : candidateNames.GetValueOrDefault(workloadId),
            Version = unwinding ? intent.FromVersion : intent.ToVersion,
        };
    }

    private static bool WalksBack(WorkloadUpdateIntent intent) => intent.ReadStageJournal() switch
    {
        { Verdict: WorkloadDocumentVerdict.Value, Value: { } journal } => journal.Direction == Reeve.Pipeline.PipelineDirection.Compensating,
        _ => intent.Phase == WorkloadUpdatePhase.RollingBack,
    };

    public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken)
        => _intentRepository.GetActiveIntentIdAsync(pluginInstanceId, cancellationToken);

}
