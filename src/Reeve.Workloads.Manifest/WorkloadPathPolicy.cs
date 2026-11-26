using System.Text.RegularExpressions;
using Reeve.Workloads.Domain;

namespace Reeve.Workloads.Manifest;

/// <summary>Pure, side-effect-free security policy predicates: a device path must live under <c>/dev</c>,
/// a D-Bus path under <c>/run</c> or <c>/var/run</c>. The host-inspect and D-Bus roots also refuse the container-runtime sockets and
/// state directories and the per-process <c>/proc/&lt;pid&gt;</c> trees, because a read-only bind neither stops <c>connect()</c> on a socket
/// nor confines <c>/proc/&lt;pid&gt;/root</c> to the directory it names.</summary>
public static class WorkloadPathPolicy
{
    private static readonly Regex QuantityPattern = new(
        @"^\+?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:(?:[eE][+-]?[0-9]+)|m|k|M|G|T|P|E|Ki|Mi|Gi|Ti|Pi|Ei)?\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumericVersionPattern = new(
        @"^[0-9]{1,9}(?:\.[0-9]{1,9})*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PlatformTokenPattern = new(
        @"^[a-z0-9]+/[a-z0-9]+(?:/[a-z0-9]+)?\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="path"/> is an allowed host char-device path: an absolute path
    /// strictly under <c>/dev/</c> (never bare <c>/dev</c>, never a traversal that could escape it).</summary>
    public static bool IsAllowedDevicePath(string? path) => WorkloadHostPathPolicy.IsAllowedDevicePath(path);

    /// <summary>True when <paramref name="path"/> is an allowed D-Bus socket-dir path, constrained to
    /// <c>/run/**</c> or <c>/var/run/**</c>, never a container-runtime socket or state directory.</summary>
    public static bool IsAllowedDbusPath(string? path) => WorkloadHostPathPolicy.IsAllowedDbusPath(path);

    /// <summary>The safe root allowlist for a read-only host-inspecting mount: <c>/sys/**</c>, <c>/proc/**</c>,
    /// <c>/run/**</c>, or <c>/var/run/**</c>.</summary>
    public static readonly IReadOnlyList<string> HostInspectRoots = WorkloadHostPathPolicy.HostInspectRoots;

    /// <summary>The udev serial-by-id tree, mounted read-only.</summary>
    public const string SerialDeviceRoot = WorkloadHostPathPolicy.SerialDeviceRoot;

    /// <summary>The narrow read-only exceptions to <see cref="HostInspectRoots"/> reaching into <c>/dev</c>.</summary>
    public static readonly IReadOnlyList<string> DevInspectRoots = WorkloadHostPathPolicy.DevInspectRoots;

    /// <summary>True when <paramref name="path"/> is an allowed read-only host-inspect directory. Host root, <c>/etc</c>,
    /// any <c>..</c>-escape, a container-runtime socket or state directory, and a per-process <c>/proc/&lt;pid&gt;</c>
    /// tree are rejected.</summary>
    public static bool IsAllowedHostInspectPath(string? path) => WorkloadHostPathPolicy.IsAllowedHostInspectPath(path);

    /// <summary>True when <paramref name="path"/> resides strictly under <paramref name="prefix"/>: absolute, no
    /// <c>..</c> or empty segments, with at least one segment beyond the prefix. Lexical only.</summary>
    public static bool IsUnderPrefix(string? path, string prefix) => WorkloadHostPathPolicy.IsUnderPrefix(path, prefix);

    /// <summary>True when <paramref name="subdir"/> is a safe relative path for node-local state: non-empty, no
    /// leading <c>/</c>, and no <c>..</c> segment that could escape the curated directory.</summary>
    public static bool IsSafeRelativeSubdir(string? subdir)
    {
        if (string.IsNullOrWhiteSpace(subdir))
            return false;
        if (subdir[0] == '/')
            return false;

        var raw = subdir.Split('/');
        for (var i = 0; i < raw.Length; i++)
        {
            var seg = raw[i];
            if (seg.Length == 0)
            {
                if (i == raw.Length - 1)
                    continue;
                return false;
            }
            if (seg == "..")
                return false;
        }
        return true;
    }

    /// <summary>True when <paramref name="value"/> is a valid Kubernetes resource quantity (e.g. <c>64Mi</c>, <c>100m</c>, <c>1Gi</c>).</summary>
    public static bool IsValidQuantity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && QuantityPattern.IsMatch(value);

    /// <summary>True when <paramref name="value"/> is a dot-separated numeric-version string (e.g. <c>1</c>, <c>1.2</c>).</summary>
    public static bool IsNumericVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && NumericVersionPattern.IsMatch(value);

    /// <summary>True when <paramref name="token"/> is a valid OCI <c>os/arch[/variant]</c> platform token (e.g. <c>linux/amd64</c>).</summary>
    public static bool IsValidPlatformToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && PlatformTokenPattern.IsMatch(token);

    /// <summary>Compares two numeric-version strings component-wise (missing components treated as 0).
    /// Non-numeric input compares as equal (0), so a caller that already flagged malformed input doesn't also get a spurious ordering error.</summary>
    public static int CompareNumericVersions(string? a, string? b)
    {
        if (!IsNumericVersion(a) || !IsNumericVersion(b))
            return 0;

        var pa = a!.Split('.');
        var pb = b!.Split('.');
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var ca = i < pa.Length ? long.Parse(pa[i]) : 0L;
            var cb = i < pb.Length ? long.Parse(pb[i]) : 0L;
            if (ca != cb)
                return ca < cb ? -1 : 1;
        }
        return 0;
    }
}
