using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerPrivilegeRenderer : IDockerPrivilegeRenderer
{
    private const string DevicePermissions = "rwm";

    public DockerPrivilegeRendering RenderPrivileges(WorkloadDesiredSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var priv = spec.Privileges;

        var hostConfig = new HostConfig
        {
            CapDrop = new List<string> { "ALL" },
        };

        if (priv is null)
        {
            return new DockerPrivilegeRendering { HostConfig = hostConfig, User = null };
        }

        if (priv.Capabilities is { Count: > 0 })
        {
            hostConfig.CapAdd = new List<string>(priv.Capabilities);
        }

        hostConfig.Privileged = priv.Privileged;

        List<string>? binds = null;
        List<DeviceMapping>? devices = null;

        if (!string.IsNullOrEmpty(priv.DbusHostPath))
        {
            (binds ??= new List<string>()).Add($"{priv.DbusHostPath}:{priv.DbusHostPath}:ro");
        }

        if (priv.NodeLocalState is { } nls && !string.IsNullOrEmpty(nls.HostPath))
        {
            (binds ??= new List<string>()).Add($"{nls.HostPath}:{nls.HostPath}");
        }

        if (priv.TunDevice)
        {
            (devices ??= new List<DeviceMapping>()).Add(Device("/dev/net/tun"));
        }

        for (var i = 0; i < priv.DevicePaths.Count; i++)
        {
            var path = priv.DevicePaths[i];
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            (devices ??= new List<DeviceMapping>()).Add(Device(path));
        }

        foreach (var hp in priv.HostPaths)
        {
            if (string.IsNullOrEmpty(hp.HostPath) || string.IsNullOrEmpty(hp.MountPath))
            {
                continue;
            }

            (binds ??= new List<string>()).Add($"{hp.HostPath}:{hp.MountPath}:ro");
        }

        var groups = DockerSupplementaryGroups.For(priv);
        if (groups.Count > 0)
        {
            hostConfig.GroupAdd = groups.Select(g => g.ToString(CultureInfo.InvariantCulture)).ToList();
        }

        if (binds is not null)
        {
            hostConfig.Binds = binds;
        }

        if (devices is not null)
        {
            hostConfig.Devices = devices;
        }

        return new DockerPrivilegeRendering
        {
            HostConfig = hostConfig,
            User = ContainerUser(priv),
        };
    }

    internal static string? ContainerUser(ResolvedPrivileges? priv) => priv?.RunAsRoot == true ? "0" : null;

    private static DeviceMapping Device(string path) => new()
    {
        PathOnHost = path,
        PathInContainer = path,
        CgroupPermissions = DevicePermissions,
    };
}
