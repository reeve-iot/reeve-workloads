using System.Collections.Generic;
using System.Linq;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Docker;

internal static class DockerSupplementaryGroups
{
    public static IReadOnlyList<int> For(ResolvedPrivileges? privileges)
    {
        if (privileges is null)
        {
            return [];
        }

        var groups = new SortedSet<int>(privileges.DeviceGroupIds);
        if (privileges.FsGroup is { } fsGroup)
        {
            groups.Add(fsGroup);
        }

        return groups.ToList();
    }
}
