using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update;

internal interface IWorkloadUpdatePlanner
{
    Task<WorkloadUpdatePlan> BuildPlanAsync(
        Guid pluginInstanceId,
        string candidateVersion,
        Guid? intentId,
        IReadOnlyDictionary<string, string?>? operatorParameters = null,
        IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds = null,
        WorkloadUpdatePlanOrigin? origin = null,
        CancellationToken cancellationToken = default);
}

internal sealed record WorkloadUpdatePlanOrigin(string? FromVersion);
