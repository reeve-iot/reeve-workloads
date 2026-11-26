namespace Reeve.Workloads.Core.Session;

/// <summary>Whether a running version is strictly older than an installed one, by semantic version where both
/// sides parse and ordinally where they do not. Unknown on either side is not a skew.</summary>
/// <remarks>The parse is deliberately lenient in two ways: a leading <c>v</c> is dropped and build metadata after <c>+</c> is ignored, because
/// a tag and a manifest routinely disagree on both and neither disagreement is a skew.</remarks>
public static class WorkloadVersionOrder
{
    /// <summary>True when <paramref name="runningVersion"/> is strictly below
    /// <paramref name="installedVersion"/>. Either side blank or null is false: unknown is not older.</summary>
    public static bool IsOlder(string? runningVersion, string? installedVersion)
        => !string.IsNullOrWhiteSpace(runningVersion)
            && !string.IsNullOrWhiteSpace(installedVersion)
            && Compare(runningVersion, installedVersion) < 0;

    /// <summary>Semantic order where both parse; a parseable version outranks an unparseable one; two
    /// unparseable tokens fall back to ordinal so the order is at least total and stable.</summary>
    public static int Compare(string? left, string? right)
    {
        var leftOk = TryParse(left, out var leftVersion);
        var rightOk = TryParse(right, out var rightVersion);

        if (leftOk && rightOk)
        {
            return leftVersion.CompareTo(rightVersion);
        }

        if (leftOk)
        {
            return 1;
        }

        if (rightOk)
        {
            return -1;
        }

        return string.CompareOrdinal(left ?? string.Empty, right ?? string.Empty);
    }

    private static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed[0] is 'v' or 'V')
        {
            trimmed = trimmed[1..];
        }

        var plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        string? prerelease = null;
        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = trimmed[(dash + 1)..];
            trimmed = trimmed[..dash];
            if (prerelease.Length == 0)
            {
                prerelease = null;
            }
        }

        var parts = trimmed.Split('.');
        if (parts.Length is 0 or > 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) || major < 0)
        {
            return false;
        }

        var minor = 0;
        if (parts.Length >= 2 && (!int.TryParse(parts[1], out minor) || minor < 0))
        {
            return false;
        }

        var patch = 0;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out patch) || patch < 0))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    private readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease)
        : IComparable<SemanticVersion>
    {
        public int CompareTo(SemanticVersion other)
        {
            var byMajor = Major.CompareTo(other.Major);
            if (byMajor != 0)
            {
                return byMajor;
            }

            var byMinor = Minor.CompareTo(other.Minor);
            if (byMinor != 0)
            {
                return byMinor;
            }

            var byPatch = Patch.CompareTo(other.Patch);
            if (byPatch != 0)
            {
                return byPatch;
            }

            var leftIsRelease = string.IsNullOrEmpty(Prerelease);
            var rightIsRelease = string.IsNullOrEmpty(other.Prerelease);
            if (leftIsRelease && rightIsRelease)
            {
                return 0;
            }

            if (leftIsRelease)
            {
                return 1;
            }

            if (rightIsRelease)
            {
                return -1;
            }

            return string.CompareOrdinal(Prerelease, other.Prerelease);
        }
    }
}
