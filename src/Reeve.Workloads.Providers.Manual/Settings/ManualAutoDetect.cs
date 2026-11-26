using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Manual;

internal sealed class ManualAutoDetect : IWorkloadProviderAutoDetect
{
    public WorkloadProviderId Provider => ManualProvider.Id;

    public int Priority => IWorkloadProviderAutoDetect.FallbackPriority;

    public Task<bool> IsAvailableAsync(JsonElement providerSettings, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
