using System.Collections.Frozen;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.HealthGate;

internal interface IRolloutCertificationPolicy
{
    IReadOnlySet<WorkloadHealthMode> RequiredModes { get; }

    WorkloadHealthGateCertification CertificationFor(WorkloadManifest workload);
}

internal sealed class RolloutCertificationPolicy : IRolloutCertificationPolicy
{
    public RolloutCertificationPolicy(IEnumerable<IRolloutCertifier> certifiers)
        : this(Single(certifiers))
    {
    }

    internal RolloutCertificationPolicy(IRolloutCertifier? certifier)
        => RequiredModes = certifier?.RequiredModes.ToFrozenSet() ?? FrozenSet<WorkloadHealthMode>.Empty;

    internal static IRolloutCertifier? Single(IEnumerable<IRolloutCertifier> certifiers)
    {
        ArgumentNullException.ThrowIfNull(certifiers);

        var registered = certifiers.ToList();
        return registered.Count switch
        {
            0 => null,
            1 => registered[0],
            _ => throw new InvalidOperationException(
                $"{registered.Count} implementations of {nameof(IRolloutCertifier)} are registered; the health "
                + "gate takes one at most."),
        };
    }

    public IReadOnlySet<WorkloadHealthMode> RequiredModes { get; }

    public WorkloadHealthGateCertification CertificationFor(WorkloadManifest workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        if (workload.Kind == WorkloadKind.ScanPerNode)
        {
            return WorkloadHealthGateCertification.AppliedSpec;
        }

        return RequiredModes.Contains(workload.Health.Mode)
            ? WorkloadHealthGateCertification.HostEvidence
            : WorkloadHealthGateCertification.ProviderReady;
    }
}
