using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core.Update;

internal sealed class WorkloadUpdateEffects(
    IWorkloadResolvedParameterWriter parameterWriter,
    IWorkloadGroupRepository modules,
    IWorkloadClock clock,
    ILogger<WorkloadUpdateEffects> logger,
    WorkloadParameterSecretCodec parameterSecrets)
    : IWorkloadUpdateEffects
{
    private readonly IWorkloadResolvedParameterWriter _parameterWriter =
        parameterWriter ?? throw new ArgumentNullException(nameof(parameterWriter));
    private readonly IWorkloadGroupRepository _modules = modules ?? throw new ArgumentNullException(nameof(modules));
    private readonly IWorkloadClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILogger<WorkloadUpdateEffects> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly WorkloadParameterSecretCodec _parameterSecrets = parameterSecrets ?? throw new ArgumentNullException(nameof(parameterSecrets));

    public async Task CommitInstalledVersionAsync(string groupId, string targetVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);

        var module = await _modules.GetAsync(groupId, cancellationToken).ConfigureAwait(false);
        if (module is null)
        {
            _logger.LogWarning(
                "CommitInstalledVersion: module '{GroupId}' has no module row; cannot record {Version}.",
                groupId, targetVersion);
            return;
        }

        var from = module.InstalledVersion;
        if (!module.MoveTo(targetVersion, _clock.UtcNow))
        {
            return;
        }

        await _modules.UpdateAsync(module, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Committed installed version {Version} (from {From}) for group '{GroupId}'.",
            targetVersion, from ?? "(none)", module.GroupId);
    }

    public async Task CommitParamsAsync(WorkloadUpdatePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var wanted = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var item in plan.Workloads)
        {
            if (plan.CandidateResolvedParameters.TryGetValue(item.WorkloadId, out var resolved))
            {
                var workload = plan.CandidateManifest.Workloads.FirstOrDefault(w => w.Id == item.WorkloadId);
                var stored = plan.CandidateStoredParameters[item.WorkloadId];
                wanted[item.WorkloadId] = workload is null ? stored : _parameterSecrets.Protect(workload, stored);
            }
        }

        await _parameterWriter
            .SetResolvedParametersAsync(plan.GroupInstanceId, wanted, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RestorePreviousParamsAsync(WorkloadUpdateIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (intent.ReadPreviousParameters() is { } previousParams)
        {
            var passwordNames = intent.ReadPreviousPasswordParameterNames();
            var protectedParams = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
            foreach (var (workloadId, values) in previousParams)
            {
                var hasNames = passwordNames.TryGetValue(workloadId, out var configuredNames)
                    && configuredNames is not null;
                var names = hasNames ? configuredNames! : Array.Empty<string>();
                protectedParams[workloadId] = _parameterSecrets.ProtectNamedParameters(
                    values, hasNames ? names : Array.Empty<string>());
            }
            await _parameterWriter
                .SetResolvedParametersAsync(intent.GroupInstanceId, protectedParams, _clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
