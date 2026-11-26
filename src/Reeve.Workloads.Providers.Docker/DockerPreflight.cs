using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerPreflight
{
    public const string ApiVersionCheckId = "docker-api-version";

    public const string ArchCheckId = "docker-daemon-arch";

    public const string ArchUnverifiableCheckId = "docker-daemon-arch-unverifiable";

    public const string SocketReachableCheckId = "docker-socket-reachable";

    public const string SocketAbsentCheckId = "docker-socket-absent";

    public const string SocketPermissionCheckId = "docker-socket-permission";

    public const string RegistryAuthCheckId = "docker-registry-auth";

    public const string NodeSelectorCheckId = "docker-node-selector";

    public const string MdnsIpv6CheckId = "docker-mdns-ipv6";

    public const string DeviceGroupResolvedCheckId = "docker-device-group-resolved";

    private readonly IDockerClientFactory _clientFactory;

    public DockerPreflight(IDockerClientFactory clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public Task<DockerPreflightResult> ProbeAsync(
        WorkloadOrchestratorSettings settings, CancellationToken cancellationToken = default)
        => ProbeAsync(settings, spec: null, cancellationToken);

    public async Task<DockerPreflightResult> ProbeAsync(
        WorkloadOrchestratorSettings settings, WorkloadDesiredSpec? spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var docker = DockerProviderSettings.From(settings);
        var checks = new List<PreflightCheck>();

        if (spec is not null)
        {
            checks.AddRange(SpecDispositionChecks(spec));
        }

        var registryResult = AddRegistryChecks(docker, checks);

        IDockerClient client;
        try
        {
            client = _clientFactory.CreateClient(docker.SocketUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            checks.Add(ClassifySocketFailure(docker.SocketUri, ex));
            return Build(checks, apiVersion: null, arch: null, registryResult, socketReachable: false);
        }

        VersionResponse version;
        try
        {
            version = await client.System.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            checks.Add(ClassifySocketFailure(docker.SocketUri, ex));
            return Build(checks, apiVersion: null, arch: null, registryResult, socketReachable: false);
        }

        checks.Add(PreflightCheck.Pass(SocketReachableCheckId, "The docker daemon socket is reachable."));

        var apiVersion = AddApiVersionCheck(version, checks);
        var arch = AddArchCheck(version, checks);

        return Build(checks, apiVersion, arch, registryResult, socketReachable: true);
    }

    public static PreflightCheck? NodeSelectorInfoCheck(WorkloadDesiredSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.NodeSelector is null || spec.NodeSelector.Count == 0)
        {
            return null;
        }

        var rendered = string.Join(
            ", ",
            spec.NodeSelector.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

        return PreflightCheck.Pass(
            NodeSelectorCheckId,
            $"This workload declares a nodeSelector [{rendered}], which docker (a single host) cannot honour — the "
                + "container runs on THIS host regardless. The selector is surfaced for awareness, not enforced.",
            PreflightSeverity.Info);
    }

    public static PreflightCheck? MdnsIpv6InfoCheck(WorkloadDesiredSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var network = spec.Network;
        if (network is null || network.HostNetwork || !(network.Mdns || network.Ipv6Required))
        {
            return null;
        }

        var requested = (network.Mdns, network.Ipv6Required) switch
        {
            (true, true) => "mDNS discovery and an IPv6-required network",
            (true, false) => "mDNS discovery",
            _ => "an IPv6-required network",
        };

        return PreflightCheck.Pass(
            MdnsIpv6CheckId,
            $"This workload requests {requested}, which docker's default bridge network on a single host does not "
                + "honour — mDNS is not forwarded and IPv6 is not guaranteed off the host network. The request is "
                + "surfaced for awareness, not enforced; run the workload on the host network if it is required.",
            PreflightSeverity.Info);
    }

    public static PreflightCheck? UnresolvedDeviceGroupCheck(WorkloadDesiredSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Privileges is not { JoinsDeviceGroups: true, DeviceGroupIds.Count: 0, RunAsRoot: false } privileges)
        {
            return null;
        }

        var devices = privileges.DevicePaths.Count > 0
            ? string.Join(", ", privileges.DevicePaths.OrderBy(d => d, StringComparer.Ordinal))
            : "its bound device";

        return PreflightCheck.Fail(
            DeviceGroupResolvedCheckId,
            PreflightSeverity.Warning,
            $"This workload joins the owning group of {devices}, but no group id could be resolved for it, "
                + "so the container is started with no HostConfig.GroupAdd entry. It will fail to open the device "
                + "(EACCES) unless its image runs as root.",
            fixSnippet: "Run the discovery scan on the host that holds the device — the group id is read from that "
                + "host's hardware inventory — then re-run this preflight. Hosts reporting conflicting group ids for "
                + "the same device also resolve to none.");
    }

    public static IReadOnlyList<PreflightCheck> SpecDispositionChecks(WorkloadDesiredSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var checks = new List<PreflightCheck>(capacity: 3);
        if (NodeSelectorInfoCheck(spec) is { } nodeSelector)
        {
            checks.Add(nodeSelector);
        }

        if (MdnsIpv6InfoCheck(spec) is { } mdnsIpv6)
        {
            checks.Add(mdnsIpv6);
        }

        if (UnresolvedDeviceGroupCheck(spec) is { } deviceGroup)
        {
            checks.Add(deviceGroup);
        }

        return checks;
    }


    private static string AddRegistryChecks(DockerProviderSettings docker, List<PreflightCheck> checks)
    {
        var credentials = docker.RegistryCredentials;
        if (credentials is null || credentials.Count == 0)
        {
            return "No registry credentials configured — workload images are pulled anonymously.";
        }

        var hosts = new List<string>(credentials.Count);
        for (var i = 0; i < credentials.Count; i++)
        {
            var credential = credentials[i];
            var host = string.IsNullOrWhiteSpace(credential?.HostPattern) ? "(unset)" : credential!.HostPattern;
            hosts.Add(host);

            var authType = (credential?.AuthType ?? RegistryAuthType.Basic).ToString().ToLowerInvariant();
            var id = $"{RegistryAuthCheckId}[{i}]";

            if (!IsSecureHost(host))
            {
                checks.Add(PreflightCheck.Fail(
                    id,
                    PreflightSeverity.Warning,
                    $"Registry credential #{i + 1} targets '{host}', which docker treats as an insecure (plain-HTTP) "
                        + "host — credentials are refused there at pull time, so an authenticated pull will fail.",
                    fixSnippet: "Serve the registry over HTTPS, or remove the credential for this host."));
            }
            else
            {
                checks.Add(PreflightCheck.Pass(
                    id,
                    $"Registry credential #{i + 1} is configured for '{host}' ({authType} auth).",
                    PreflightSeverity.Info));
            }
        }

        return $"{credentials.Count} registry credential(s) configured for: {string.Join(", ", hosts)}.";
    }

    private static bool IsSecureHost(string host)
    {
        try
        {
            return DockerRegistryAuth.IsSecureRegistry(host);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }


    private static string? AddApiVersionCheck(VersionResponse version, List<PreflightCheck> checks)
    {
        var floor = DockerProviderConstants.MinimumDaemonApiVersion;
        var reported = version.APIVersion;

        if (string.IsNullOrWhiteSpace(reported))
        {
            checks.Add(PreflightCheck.Fail(
                ApiVersionCheckId,
                PreflightSeverity.Warning,
                $"The docker daemon did not report an API version, so it cannot be verified against the minimum '{floor}'."));
            return null;
        }

        var comparison = CompareApiVersions(reported, floor);
        if (comparison is null)
        {
            checks.Add(PreflightCheck.Fail(
                ApiVersionCheckId,
                PreflightSeverity.Warning,
                $"The docker daemon API version '{reported}' could not be parsed to verify the minimum '{floor}'."));
            return reported;
        }

        if (comparison < 0)
        {
            checks.Add(PreflightCheck.Fail(
                ApiVersionCheckId,
                PreflightSeverity.Error,
                $"The docker daemon API version '{reported}' is below the minimum '{floor}' the provider relies on "
                    + $"(Docker Engine {DockerProviderConstants.MinimumDaemonEngineVersion}+).",
                fixSnippet: $"Upgrade the docker daemon to Engine {DockerProviderConstants.MinimumDaemonEngineVersion} "
                    + $"or newer (API {floor}+), then re-run preflight."));
            return reported;
        }

        checks.Add(PreflightCheck.Pass(
            ApiVersionCheckId,
            $"The docker daemon API version '{reported}' meets the minimum '{floor}'."));
        return reported;
    }


    private static string? AddArchCheck(VersionResponse version, List<PreflightCheck> checks)
    {
        var arch = DockerDaemonGate.ResolveDaemonGoArch(version, info: null);

        if (string.IsNullOrEmpty(arch))
        {
            checks.Add(PreflightCheck.Fail(
                ArchUnverifiableCheckId,
                PreflightSeverity.Warning,
                "The docker daemon did not report an architecture, so its GOARCH is unverifiable."));
            return null;
        }

        checks.Add(PreflightCheck.Pass(
            ArchCheckId,
            $"The docker daemon architecture (GOARCH) is '{arch}'."));
        return arch;
    }


    private static PreflightCheck ClassifySocketFailure(string? socketUri, Exception ex)
    {
        var text = FlattenMessages(ex).ToLowerInvariant();
        var socketErrors = CollectSocketErrors(ex);

        var permissionDenied = socketErrors.Contains(SocketError.AccessDenied)
            || text.Contains("permission denied")
            || text.Contains("access is denied")
            || text.Contains("eacces");
        if (permissionDenied)
        {
            return PreflightCheck.Fail(
                SocketPermissionCheckId,
                PreflightSeverity.Error,
                "The docker socket exists but reeve is not permitted to connect to it (permission denied). The "
                    + "workload cannot be applied on this leader until access is granted.",
                fixSnippet: "Grant the reeve process access to the docker socket (e.g. add it to the docker group), or "
                    + "point socketUri at an endpoint it can reach, then re-run preflight.");
        }

        var absent = socketErrors.Contains(SocketError.AddressNotAvailable)
            || text.Contains("no such file")
            || text.Contains("cannot find the file")
            || text.Contains("cannot find the path")
            || text.Contains("does not exist")
            || text.Contains("enoent")
            || UnixSocketFileMissing(socketUri);
        if (absent)
        {
            return PreflightCheck.Fail(
                SocketAbsentCheckId,
                PreflightSeverity.Error,
                "No docker socket/endpoint is present at the configured address — the socket file or endpoint does not "
                    + "exist. A leader with no reachable docker socket cannot apply workloads.",
                fixSnippet: "Start the docker daemon and/or set docker.socketUri to its actual socket/endpoint (e.g. "
                    + "unix:///var/run/docker.sock), then re-run preflight.");
        }

        return PreflightCheck.Fail(
            SocketReachableCheckId,
            PreflightSeverity.Error,
            "The docker daemon is unreachable (the socket/endpoint did not respond). A leader with no reachable docker "
                + "socket cannot apply workloads.",
            fixSnippet: "Ensure the docker daemon is running and reeve can reach its socket/endpoint (docker.socketUri "
                + "/ DOCKER_HOST), then re-run preflight.");
    }

    private static bool UnixSocketFileMissing(string? socketUri)
    {
        try
        {
            var endpoint = DockerClientFactory.ResolveEndpoint(socketUri);
            if (!string.Equals(endpoint.Scheme, "unix", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = endpoint.LocalPath;
            return !string.IsNullOrEmpty(path) && !System.IO.File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FlattenMessages(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            parts.Add(current.Message);
        }

        return string.Join(" | ", parts);
    }

    private static HashSet<SocketError> CollectSocketErrors(Exception ex)
    {
        var errors = new HashSet<SocketError>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                errors.Add(socket.SocketErrorCode);
            }
        }

        return errors;
    }


    internal static int? CompareApiVersions(string reported, string floor)
    {
        if (!TryParseApiVersion(reported, out var r) || !TryParseApiVersion(floor, out var f))
        {
            return null;
        }

        return r.Major != f.Major
            ? r.Major.CompareTo(f.Major)
            : r.Minor.CompareTo(f.Minor);
    }

    private static bool TryParseApiVersion(string value, out (int Major, int Minor) parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var segments = value.Trim().Split('.');
        if (!int.TryParse(segments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
        {
            return false;
        }

        var minor = 0;
        if (segments.Length >= 2
            && !int.TryParse(segments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor))
        {
            return false;
        }

        parsed = (major, minor);
        return true;
    }

    private static DockerPreflightResult Build(
        List<PreflightCheck> checks,
        string? apiVersion,
        string? arch,
        string registryResult,
        bool socketReachable)
        => new()
        {
            Checks = checks,
            DockerApiVersion = apiVersion,
            DaemonArchitecture = arch,
            RegistryPullAuthResult = registryResult,
            SocketReachable = socketReachable,
        };
}

internal sealed record DockerPreflightResult
{
    public required IReadOnlyList<PreflightCheck> Checks { get; init; }

    public string? DockerApiVersion { get; init; }

    public string? DaemonArchitecture { get; init; }

    public string? RegistryPullAuthResult { get; init; }

    public bool SocketReachable { get; init; }

    public bool HasBlockingError => Checks.Any(c => !c.Passed && c.Severity == PreflightSeverity.Error);
}
