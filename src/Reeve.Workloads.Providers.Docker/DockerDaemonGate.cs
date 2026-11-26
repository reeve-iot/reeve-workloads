using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerDaemonGate : IDockerDaemonGate
{
    public const string ReachableCheckId = "docker-daemon-reachable";

    public const string ArchMatchCheckId = "docker-arch-match";

    public const string ArchMismatchCheckId = "docker-arch-mismatch";

    public const string ArchUnverifiableCheckId = "docker-arch-unverifiable";

    public const string HostNetworkNonLinuxCheckId = "docker-hostnetwork-non-linux";

    public const string HostNetworkOsCheckId = "docker-hostnetwork-os";

    public async Task<IReadOnlyList<PreflightCheck>> EvaluateAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(spec);

        VersionResponse? version = null;
        SystemInfoResponse? info = null;
        Exception? versionError = null;
        Exception? infoError = null;

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
            versionError = ex;
        }

        try
        {
            info = await client.System.GetSystemInfoAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            infoError = ex;
        }

        if (version is null && info is null)
        {
            var reason = versionError?.Message ?? infoError?.Message ?? "the daemon did not respond";
            return new[]
            {
                PreflightCheck.Fail(
                    ReachableCheckId,
                    PreflightSeverity.Error,
                    $"The docker daemon is unreachable ({reason}). The workload cannot be applied until the daemon responds.",
                    fixSnippet: "Ensure the docker daemon is running and reeve can reach its socket/endpoint, then re-run preflight."),
            };
        }

        var checks = new List<PreflightCheck>(capacity: 3);

        EvaluateArch(spec, version, info, checks);
        EvaluateHostNetwork(spec, version, info, checks);

        return checks;
    }

    private static void EvaluateArch(
        WorkloadDesiredSpec spec,
        VersionResponse? version,
        SystemInfoResponse? info,
        List<PreflightCheck> checks)
    {
        var requiredArches = spec.Platforms
            .Select(ExtractPlatformArch)
            .Where(a => !string.IsNullOrEmpty(a))
            .Select(a => a!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requiredArches.Count == 0)
        {
            return;
        }

        var daemonArch = ResolveDaemonGoArch(version, info);

        if (string.IsNullOrEmpty(daemonArch))
        {
            checks.Add(PreflightCheck.Fail(
                ArchUnverifiableCheckId,
                PreflightSeverity.Warning,
                $"The docker daemon did not report an architecture, so its GOARCH cannot be verified against the "
                    + $"declared image platform(s) [{string.Join(", ", spec.Platforms)}]. The apply is not blocked, "
                    + "but an architecture mismatch cannot be ruled out."));
            return;
        }

        if (requiredArches.Any(a => string.Equals(a, daemonArch, StringComparison.OrdinalIgnoreCase)))
        {
            checks.Add(PreflightCheck.Pass(
                ArchMatchCheckId,
                $"The image platform architecture(s) [{string.Join(", ", requiredArches)}] include the docker daemon "
                    + $"GOARCH '{daemonArch}'."));
            return;
        }

        checks.Add(PreflightCheck.Fail(
            ArchMismatchCheckId,
            PreflightSeverity.Error,
            $"None of the image platform architecture(s) [{string.Join(", ", requiredArches)}] match the docker daemon "
                + $"GOARCH '{daemonArch}'. The image would fail to execute (exec format error) on this host.",
            fixSnippet: $"Deploy this workload on a '{daemonArch}' host, or publish/select an image built for '{daemonArch}'."));
    }

    private static void EvaluateHostNetwork(
        WorkloadDesiredSpec spec,
        VersionResponse? version,
        SystemInfoResponse? info,
        List<PreflightCheck> checks)
    {
        if (spec.Network?.HostNetwork != true)
        {
            return;
        }

        var osType = ResolveOsType(version, info);

        if (!string.IsNullOrEmpty(osType) && !string.Equals(osType, "linux", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(PreflightCheck.Fail(
                HostNetworkNonLinuxCheckId,
                PreflightSeverity.Error,
                $"This workload requests host networking (hostNetwork=true), which docker only implements on Linux "
                    + $"daemons. The daemon OS is '{osType}', where host networking is unavailable.",
                fixSnippet: "Disable hostNetwork for this workload, or deploy it on a Linux docker daemon."));
        }
        else if (string.Equals(osType, "linux", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(PreflightCheck.Pass(
                HostNetworkOsCheckId,
                "Host networking is supported: the docker daemon OS is Linux."));
        }
    }


    public static string? ExtractPlatformArch(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return null;
        }

        var segments = platform.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 2 ? segments[1].ToLowerInvariant() : null;
    }

    public static string? ResolveDaemonGoArch(VersionResponse? version, SystemInfoResponse? info)
    {
        if (!string.IsNullOrWhiteSpace(version?.Arch))
        {
            return NormalizeArch(version!.Arch);
        }

        if (!string.IsNullOrWhiteSpace(info?.Architecture))
        {
            return NormalizeArch(info!.Architecture);
        }

        return null;
    }

    public static string NormalizeArch(string arch)
    {
        return arch.Trim().ToLowerInvariant() switch
        {
            "x86_64" => "amd64",
            "aarch64" => "arm64",
            "armv7l" => "arm",
            "armv6l" => "arm",
            var other => other,
        };
    }

    private static string? ResolveOsType(VersionResponse? version, SystemInfoResponse? info)
    {
        if (!string.IsNullOrWhiteSpace(info?.OSType))
        {
            return info!.OSType.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(version?.Os))
        {
            return version!.Os.Trim().ToLowerInvariant();
        }

        return null;
    }
}
