using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Network;

namespace Reeve.Workloads.Core.Network;

/// <summary>Provider dispatch behind <c>GET /api/workloads/network-attachments</c>: resolves the effective
/// provider once and asks exactly one source (no cross-provider I/O). Every degraded case is an empty list + a closed-vocabulary reason, never a 500 — the sources catch their own client faults; caller cancellation still propagates.</summary>
public sealed class NetworkAttachmentEnumerationService(
    IWorkloadOrchestratorSettingsService settingsService,
    IEnumerable<INetworkAttachmentSource> sources) : INetworkAttachmentEnumerationService
{
    private readonly IWorkloadOrchestratorSettingsService settingsService = settingsService;
    private readonly IReadOnlyList<INetworkAttachmentSource> sources = sources.ToList();

    /// <inheritdoc />
    public async Task<WorkloadNetworkAttachmentListResponse> ListAsync(CancellationToken cancellationToken)
    {
        var settingsResponse = await settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
        var provider = settingsResponse.EffectiveProvider;

        var source = sources.FirstOrDefault(s => s.Provider == provider);
        if (source is null)
        {
            return Unavailable(provider, WorkloadNetworkAttachmentUnavailableReasons.NotSupported);
        }

        return await source.ListAsync(settingsResponse.Settings, cancellationToken).ConfigureAwait(false);
    }

    private static WorkloadNetworkAttachmentListResponse Unavailable(WorkloadProviderId provider, string reason) =>
        new()
        {
            Provider = provider,
            Items = Array.Empty<WorkloadNetworkAttachmentDto>(),
            UnavailableReason = reason,
        };
}
