using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Reeve.Workloads.Providers.Docker.Orphans;

/// <summary>The orphan surface a host consumes: find workload containers this installation owns but has no row
/// for, and remove the ones the operator names. This is an interface rather than the concrete service because the provider's own seams are internal, and a public constructor taking
/// <c>IDockerClientFactory</c> would drag two of them into the published surface.</summary>
public interface IDockerWorkloadOrphanService
{
    /// <summary>Lists the orphans. THROWS <see cref="WorkloadOrphanProviderUnreachableException"/> rather than
    /// answering an empty list when the daemon is unreachable — a false "nothing to adopt" is worse than an error.</summary>
    Task<IReadOnlyList<WorkloadOrphan>> DetectAsync(CancellationToken cancellationToken = default);

    /// <summary>Force-removes every container for <paramref name="instanceId"/>, after re-verifying it is still an
    /// orphan. A container carrying a FOREIGN installation label is refused with
    /// <see cref="WorkloadOrphanForeignInstallationException"/> unless <paramref name="acknowledgeForeignInstallation"/>
    /// says the operator has decided which situation it is. Answers the number of containers removed.</summary>
    Task<int> PurgeAsync(
        Guid instanceId,
        bool acknowledgeForeignInstallation = false,
        CancellationToken cancellationToken = default);
}
