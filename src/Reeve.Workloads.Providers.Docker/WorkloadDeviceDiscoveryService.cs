using Docker.DotNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Preview;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class WorkloadDeviceDiscoveryService : IWorkloadDeviceDiscoveryService
{
    public const string DisclosureCode = "deviceDiscovery.hostDev";

    private readonly IWorkloadOrchestratorSettingsService _settingsService;
    private readonly IDockerClientFactory _clientFactory;
    private readonly IDockerDeviceDiscoveryProbe _probe;
    private readonly ILogger<WorkloadDeviceDiscoveryService> _logger;

    public WorkloadDeviceDiscoveryService(
        IWorkloadOrchestratorSettingsService settingsService,
        IDockerClientFactory clientFactory,
        IDockerDeviceDiscoveryProbe probe,
        ILogger<WorkloadDeviceDiscoveryService> logger)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WorkloadDeviceDiscoveryResponse> DiscoverDevicesAsync(bool acknowledged, CancellationToken cancellationToken = default)
    {
        var disclosure = BuildDisclosure();

        if (!acknowledged)
        {
            return new WorkloadDeviceDiscoveryResponse
            {
                Ok = true,
                Acknowledged = false,
                Ran = false,
                Disclosure = disclosure,
                Message = "Acknowledge that the one-shot helper reads the host /dev/serial/by-id directory before running device discovery.",
            };
        }

        WorkloadOrchestratorSettingsResponse settingsResponse;
        try
        {
            settingsResponse = await _settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Device discovery could not read the orchestrator settings.");
            return WorkloadDeviceDiscoveryResponse.Blocked(disclosure, $"The orchestrator settings could not be read: {ex.Message}");
        }

        if (settingsResponse.EffectiveProvider != DockerProvider.Id)
        {
            return WorkloadDeviceDiscoveryResponse.Blocked(
                disclosure,
                $"Host device discovery is only available on the docker provider (the effective provider is '{settingsResponse.EffectiveProvider}').");
        }

        var settings = settingsResponse.Settings;

        var helperImage = DockerDeviceDiscoveryProbe.DefaultHelperImage;

        IDockerClient client;
        try
        {
            client = _clientFactory.CreateClient(DockerProviderSettings.From(settings).SocketUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Device discovery could not build the docker client.");
            return new WorkloadDeviceDiscoveryResponse
            {
                Ok = true,
                Acknowledged = true,
                Ran = false,
                Disclosure = disclosure,
                Message = "Could not reach the docker daemon to run device discovery.",
            };
        }

        var result = await _probe
            .DiscoverAsync(client, new DockerDeviceDiscoveryRequest { HelperImage = helperImage }, cancellationToken)
            .ConfigureAwait(false);

        return new WorkloadDeviceDiscoveryResponse
        {
            Ok = true,
            Acknowledged = true,
            Ran = result.Ran,
            Disclosure = disclosure,
            Devices = result.DevicePaths.Select(p => new DiscoveredSerialDevice { Path = p }).ToList(),
            Message = result.Message,
        };
    }

    private static WorkloadDisclosure BuildDisclosure() => new()
    {
        Code = DisclosureCode,
        Label = "Runs a one-shot helper container that reads the host " + DockerDeviceDiscoveryProbe.DefaultDevicePath
            + " directory to enumerate attached serial devices.",
        Severity = WorkloadDisclosureSeverity.Danger,
    };
}
