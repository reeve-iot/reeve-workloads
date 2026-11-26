namespace Reeve.Workloads.Domain;

internal static class WorkloadHostPathRefusal
{
    internal const string Display =
        "never a host-control socket or container-runtime state directory under /run or /var/run (docker, docker.sock, "
        + "containerd, crio, podman, k3s, k0s, cri-dockerd.sock, dockershim.sock, balena-engine, balena-engine.sock, "
        + "balena.sock, libvirt, snapd.socket, snapd-snap.socket, systemd/private, user) or a directory holding one, "
        + "and never a per-process /proc/<pid>, /proc/self or /proc/thread-self tree";

    private static readonly string[][] RefusedRunEntries =
    [
        ["docker"],
        ["docker.sock"],
        ["containerd"],
        ["crio"],
        ["podman"],
        ["k3s"],
        ["k0s"],
        ["cri-dockerd.sock"],
        ["dockershim.sock"],
        ["balena-engine"],
        ["balena-engine.sock"],
        ["balena.sock"],
        ["libvirt"],
        ["snapd.socket"],
        ["snapd-snap.socket"],
        ["systemd", "private"],
        ["user"],
    ];

    internal static bool IsRefused(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0] == "proc")
            return IsProcessEntry(segments[1]);
        if (segments.Length >= 2 && segments[0] == "run")
            return IsRefusedUnderRun(segments.AsSpan(1));
        if (segments.Length >= 3 && segments[0] == "var" && segments[1] == "run")
            return IsRefusedUnderRun(segments.AsSpan(2));
        return false;
    }

    private static bool IsRefusedUnderRun(ReadOnlySpan<string> relative)
    {
        foreach (var entry in RefusedRunEntries)
        {
            var shared = Math.Min(entry.Length, relative.Length);
            var matches = true;
            for (var i = 0; i < shared; i++)
            {
                if (!string.Equals(entry[i], relative[i], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return true;
        }

        return false;
    }

    private static bool IsProcessEntry(string segment) =>
        segment is "self" or "thread-self" || (segment.Length > 0 && segment.All(char.IsAsciiDigit));
}
