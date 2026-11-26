namespace Reeve.Workloads.Domain;

/// <summary>The host-path allowlists, judged lexically: the one implementation the manifest parser (for an author
/// default) and the parameter resolver (for an operator value) both call.</summary>
internal static class WorkloadHostPathPolicy
{
    private const int MaxPathLength = 512;
    private const int MaxWildcardCount = 8;

    /// <summary>True when <paramref name="path"/> is an allowed host char-device path: an absolute path
    /// strictly under <c>/dev/</c> (never bare <c>/dev</c>, never a traversal that could escape it).</summary>
    internal static bool IsAllowedDevicePath(string? path) => IsUnderPrefix(path, "/dev");

    /// <summary>True when <paramref name="path"/> is an allowed D-Bus socket-dir path, constrained to
    /// <c>/run/**</c> or <c>/var/run/**</c>, never a container-runtime socket or state directory. A manifest default
    /// (or later edit) cannot widen the mount to <c>/etc</c>.</summary>
    internal static bool IsAllowedDbusPath(string? path) =>
        (IsUnderPrefix(path, "/run") || IsUnderPrefix(path, "/var/run")) && !WorkloadHostPathRefusal.IsRefused(path!);

    /// <summary>The safe root allowlist for a read-only host-inspecting mount: <c>/sys/**</c>, <c>/proc/**</c>,
    /// <c>/run/**</c>, or <c>/var/run/**</c>. Ordered widest-first for readability; the check itself is order-independent.</summary>
    internal static readonly IReadOnlyList<string> HostInspectRoots = new[] { "/sys", "/proc", "/run", "/var/run" };

    /// <summary>The udev serial-by-id tree, mounted read-only — the first <c>/dev</c> exception to
    /// <see cref="HostInspectRoots"/>, and the first entry of <see cref="DevInspectRoots"/>.</summary>
    internal const string SerialDeviceRoot = "/dev/serial";

    /// <summary>The narrow exceptions to <see cref="HostInspectRoots"/> reaching into <c>/dev</c>: the udev
    /// serial-by-id tree and the DRM render-node tree, both read-only. Bare <c>/dev</c> and any sibling stay rejected.</summary>
    internal static readonly IReadOnlyList<string> DevInspectRoots = new[] { SerialDeviceRoot, "/dev/dri" };

    /// <summary>True when <paramref name="path"/> is an allowed read-only host-inspect directory (the roots
    /// above, plus the narrow <see cref="DevInspectRoots"/> exceptions). Host root, <c>/etc</c>, any <c>..</c>-escape, a
    /// container-runtime socket or state directory, and a per-process <c>/proc/&lt;pid&gt;</c> tree are rejected.</summary>
    internal static bool IsAllowedHostInspectPath(string? path)
    {
        foreach (var devRoot in DevInspectRoots)
        {
            if (IsPathAtOrUnder(path, devRoot))
                return true;
        }

        foreach (var root in HostInspectRoots)
        {
            if (IsUnderPrefix(path, root))
                return !WorkloadHostPathRefusal.IsRefused(path!);
        }
        return false;
    }

    /// <summary>True when <paramref name="path"/> resides strictly under <paramref name="prefix"/>: absolute,
    /// no <c>..</c> or empty segments, with at least one segment beyond the prefix. Lexical only — never touches the filesystem.</summary>
    internal static bool IsUnderPrefix(string? path, string prefix)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
            return false;

        if (path.Length > MaxPathLength || CountChar(path, '*') > MaxWildcardCount)
            return false;

        var pathSegments = SplitAbsolute(path);
        if (pathSegments is null)
            return false;

        var prefixSegments = SplitAbsolute(prefix);
        if (prefixSegments is null || prefixSegments.Count == 0)
            return false;

        if (pathSegments.Count <= prefixSegments.Count)
            return false;

        for (var i = 0; i < prefixSegments.Count; i++)
        {
            if (!string.Equals(pathSegments[i], prefixSegments[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool IsPathAtOrUnder(string? path, string prefix)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
            return false;

        if (path.Length > MaxPathLength || CountChar(path, '*') > MaxWildcardCount)
            return false;

        var pathSegments = SplitAbsolute(path);
        if (pathSegments is null)
            return false;

        var prefixSegments = SplitAbsolute(prefix);
        if (prefixSegments is null || prefixSegments.Count == 0)
            return false;

        if (pathSegments.Count < prefixSegments.Count)
            return false;

        for (var i = 0; i < prefixSegments.Count; i++)
        {
            if (!string.Equals(pathSegments[i], prefixSegments[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static List<string>? SplitAbsolute(string path)
    {
        var raw = path.Split('/');
        var segments = new List<string>(raw.Length);
        for (var i = 1; i < raw.Length; i++)
        {
            var seg = raw[i];
            if (i == raw.Length - 1 && seg.Length == 0)
                continue;
            if (seg.Length == 0 || seg == "." || seg == "..")
                return null;
            segments.Add(seg);
        }
        return segments;
    }

    private static int CountChar(string s, char c)
    {
        var n = 0;
        foreach (var ch in s)
            if (ch == c)
                n++;
        return n;
    }
}
