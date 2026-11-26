using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions.Preview;

/// <summary>Discovers host serial devices on the docker provider through a one-shot helper container. The helper
/// reads the host <c>/dev</c>, which no declared requirement covers, so it does not run until the operator acknowledges.</summary>
public interface IWorkloadDeviceDiscoveryService
{
    /// <summary>FAIL-CLOSED: an unacknowledged call touches no docker at all and returns only the disclosure.
    /// Never throws — every outcome is a typed response with honest <c>ok</c>/<c>ran</c> flags.</summary>
    Task<WorkloadDeviceDiscoveryResponse> DiscoverDevicesAsync(bool acknowledged, CancellationToken cancellationToken = default);
}
