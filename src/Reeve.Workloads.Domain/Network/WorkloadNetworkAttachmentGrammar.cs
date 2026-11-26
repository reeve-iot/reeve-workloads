using System.Text.RegularExpressions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Network;

namespace Reeve.Workloads.Domain.Network;

/// <summary>The Domain-side network-attachment grammar: names, the two reserved parameter names, wire
/// spellings, and mode-coherence normalization — the single producer of all of these. Adds a strict RFC 1123
/// DNS-subdomain arm on Kubernetes/Manual, since a Docker-legal name can be illegal there.</summary>
public static partial class WorkloadNetworkAttachmentGrammar
{
    /// <summary>Base name pattern — no commas, so the comma-separated encoding is unambiguous.</summary>
    public const string NamePattern = "^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}\\z";
    internal const string NamePatternDisplay = "^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$";

    /// <summary>List bound.</summary>
    public const int MaxAttachments = 8;

    /// <summary>The Kubernetes/Manual arm — an RFC 1123 DNS subdomain: lowercase alphanumerics, - and .,
    /// every label non-empty and starting/ending alphanumeric.</summary>
    public const string Rfc1123SubdomainPattern =
        "^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*\\z";
    internal const string Rfc1123SubdomainPatternDisplay =
        "^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*$";

    /// <summary>RFC 1123's own length bound (253) — not independently reachable today since NamePattern caps
    /// names at 63 first, but kept for fidelity so a future relaxation of that cap doesn't silently lose it.</summary>
    public const int MaxRfc1123SubdomainLength = 253;

    /// <summary>The reserved name for network mode. Reserved through WorkloadReservedParameters.</summary>
    public const string NetworkModeParameter = "network.mode";

    /// <summary>The reserved name for network attachments. Reserved through WorkloadReservedParameters.</summary>
    public const string NetworkAttachmentsParameter = "network.attachments";

    /// <summary><see cref="WorkloadNetworkMode.Cluster"/>'s wire spelling.</summary>
    public const string ClusterMode = "cluster";

    /// <summary><see cref="WorkloadNetworkMode.HostNetwork"/>'s wire spelling.</summary>
    public const string HostNetworkMode = "hostNetwork";

    /// <summary><see cref="WorkloadNetworkMode.OperatorNetwork"/>'s wire spelling.</summary>
    public const string OperatorNetworkMode = "operatorNetwork";

    [GeneratedRegex(NamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex(Rfc1123SubdomainPattern, RegexOptions.CultureInvariant)]
    private static partial Regex Rfc1123SubdomainRegex();

    /// <summary>Parses a resolved network.mode value; ordinal (case-sensitive) match against the three wire
    /// spellings, else null. A blank value is "unset" (also null) — the caller decides what unset means.</summary>
    public static WorkloadNetworkMode? ParseMode(string? wireValue) => wireValue switch
    {
        ClusterMode => WorkloadNetworkMode.Cluster,
        HostNetworkMode => WorkloadNetworkMode.HostNetwork,
        OperatorNetworkMode => WorkloadNetworkMode.OperatorNetwork,
        _ => null,
    };

    /// <summary>Splits a networkAttachment value into names — deliberately un-trimmed: " a" fails on its leading
    /// space rather than being silently repaired.</summary>
    public static IReadOnlyList<string> SplitNames(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',');

    /// <summary>Is value a legal networkAttachment value? A comma-separated list of at most MaxAttachments
    /// non-blank, non-duplicate names each matching NamePattern, plus an RFC 1123 subdomain check when the provider asks for one. Blank means "no attachment" and is accepted — operatorNetwork + blank is a separate, harder failure elsewhere.</summary>
    public static bool IsValidValue(string? value, bool strict)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var names = value.Split(',');
        if (names.Length > MaxAttachments)
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attachmentName in names)
        {
            if (attachmentName.Length == 0
                || !NameRegex().IsMatch(attachmentName)
                || !seen.Add(attachmentName))
            {
                return false;
            }

            if (strict && !IsRfc1123Subdomain(attachmentName))
                return false;
        }

        return true;
    }

    /// <summary>The RFC 1123 DNS-subdomain predicate for one already-base-legal name.</summary>
    public static bool IsRfc1123Subdomain(string name)
        => !string.IsNullOrEmpty(name)
           && name.Length <= MaxRfc1123SubdomainLength
           && Rfc1123SubdomainRegex().IsMatch(name);

    /// <summary>Mode coherence by normalization, never rejection: outside OperatorNetwork the value is reset
    /// to empty (even if malformed) and the dropped names are reported via droppedNames. Runs before the grammar check. Pure — does not log; the caller owns the diagnostic sink.</summary>
    public static string? NormalizeForMode(
        WorkloadNetworkMode mode,
        string? rawValue,
        out IReadOnlyList<string> droppedNames)
    {
        if (mode == WorkloadNetworkMode.OperatorNetwork)
        {
            droppedNames = [];
            return rawValue;
        }

        droppedNames = SplitNames(rawValue)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToArray();
        return string.Empty;
    }
}
