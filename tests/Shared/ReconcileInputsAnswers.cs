using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Tests.Shared;

internal static class ReconcileInputsAnswers
{
    public static Task<WorkloadReconcileInputsResult> Of(WorkloadReconcileInputs? inputs, string? refusal = null)
        => Task.FromResult(refusal is not null
            ? WorkloadReconcileInputsResult.HostRefused(refusal)
            : inputs is null
                ? WorkloadReconcileInputsResult.NotDeclared
                : WorkloadReconcileInputsResult.Declared(inputs));
}
