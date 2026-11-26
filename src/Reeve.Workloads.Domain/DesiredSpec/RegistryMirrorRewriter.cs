using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Domain.DesiredSpec;

/// <summary>Pure, digest-preserving registry-mirror rewrite: changes only the registry host of an image
/// reference (first matching mirror wins, full repository path preserved) and never touches the immutable <c>@digest</c> pin.</summary>
public static class RegistryMirrorRewriter
{
    /// <summary>Docker's implicit default registry for refs with no explicit host (e.g. <c>library/redis</c>).</summary>
    public const string DefaultRegistry = "docker.io";

    /// <summary>The canonical (index) form of the default registry; equivalent to <see cref="DefaultRegistry"/> for matching.</summary>
    public const string CanonicalDefaultRegistry = "index.docker.io";

    /// <summary>The implied namespace for a bare official docker.io image (e.g. <c>redis</c> ⇒ <c>library/redis</c>).</summary>
    public const string OfficialLibraryNamespace = "library";

    /// <summary>Rewrites the registry host of <paramref name="imageRef"/> per the first matching mirror,
    /// preserving the full repository path. <paramref name="digest"/> is accepted only to make "digest never altered" explicit — never embedded here.</summary>
    public static string Rewrite(string imageRef, string digest, IReadOnlyList<RegistryMirror>? mirrors)
    {
        _ = digest;

        if (string.IsNullOrWhiteSpace(imageRef) || mirrors is null || mirrors.Count == 0)
            return imageRef;

        var (host, repository) = SplitAndNormalize(imageRef);

        foreach (var mirror in mirrors)
        {
            if (mirror is null || string.IsNullOrWhiteSpace(mirror.MatchRegistry) || string.IsNullOrWhiteSpace(mirror.MirrorRegistry))
                continue;

            if (HostsMatch(CanonicalizeHost(mirror.MatchRegistry), host))
            {
                return $"{mirror.MirrorRegistry}/{repository}";
            }
        }

        return imageRef;
    }

    private static (string Host, string Repository) SplitAndNormalize(string imageRef)
    {
        var slash = imageRef.IndexOf('/');
        if (slash < 0)
        {
            return (DefaultRegistry, $"{OfficialLibraryNamespace}/{imageRef}");
        }

        var firstSegment = imageRef[..slash];
        var isRegistryHost = firstSegment.Contains('.', StringComparison.Ordinal)
            || firstSegment.Contains(':', StringComparison.Ordinal)
            || string.Equals(firstSegment, "localhost", StringComparison.Ordinal);

        if (isRegistryHost)
        {
            var host = CanonicalizeHost(firstSegment);
            var remainder = imageRef[(slash + 1)..];
            if (IsDefaultRegistry(host) && !remainder.Contains('/', StringComparison.Ordinal))
                remainder = $"{OfficialLibraryNamespace}/{remainder}";
            return (host, remainder);
        }

        return (DefaultRegistry, imageRef);
    }

    private static string CanonicalizeHost(string host)
        => IsDefaultRegistry(host) ? DefaultRegistry : host;

    private static bool IsDefaultRegistry(string host)
        => string.Equals(host, DefaultRegistry, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, CanonicalDefaultRegistry, StringComparison.OrdinalIgnoreCase);

    private static bool HostsMatch(string mirrorMatch, string refHost)
        => string.Equals(mirrorMatch, refHost, StringComparison.OrdinalIgnoreCase);
}
