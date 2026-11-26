using System.Text;
using System.Text.RegularExpressions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Domain.DesiredSpec;

/// <summary>The one grammar for a node-placement expression, shared by the desired-spec factory and the relocate
/// encoder so a value written by one is always read the same way by the other.</summary>
public static class WorkloadNodeSelectorGrammar
{
    /// <summary>Separates label expressions within one parameter value. A placement is frequently more than one
    /// label, and a single expression per parameter would leave those unrepresentable.</summary>
    public const char EntrySeparator = ',';

    /// <summary>Parses <paramref name="raw"/> into label→value pairs. A bare key selects presence with the
    /// conventional "true"; blank entries and a missing key are skipped rather than yielding an empty label.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        foreach (var entry in raw.Split(EntrySeparator))
        {
            var expr = entry.Trim();
            if (expr.Length == 0)
                continue;

            var eq = expr.IndexOf('=');
            var key = (eq < 0 ? expr : expr[..eq]).Trim();
            var value = eq < 0 ? "true" : expr[(eq + 1)..].Trim();

            if (key.Length > 0)
                yield return new KeyValuePair<string, string>(key, value);
        }
    }

    /// <summary>Formats a label map into one parameter value. Sorted by key so the same placement always produces
    /// the same string, and therefore the same SpecHash.</summary>
    public static string Format(IReadOnlyDictionary<string, string> nodeSelector)
    {
        ArgumentNullException.ThrowIfNull(nodeSelector);

        var sb = new StringBuilder();
        foreach (var (key, value) in nodeSelector.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (sb.Length > 0)
                sb.Append(EntrySeparator);
            sb.Append(key).Append('=').Append(value);
        }

        return sb.ToString();
    }

    private static readonly Regex LabelNamePattern =
        new(@"\A([A-Za-z0-9][-A-Za-z0-9_.]*)?[A-Za-z0-9]\z", RegexOptions.CultureInvariant);

    private static readonly Regex LabelValuePattern =
        new(@"\A(([A-Za-z0-9][-A-Za-z0-9_.]*)?[A-Za-z0-9])?\z", RegexOptions.CultureInvariant);

    private static readonly Regex DnsSubdomainPattern =
        new(@"\A[a-z0-9]([-a-z0-9]*[a-z0-9])?(\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*\z", RegexOptions.CultureInvariant);

    private const int MaxLabelNameLength = 63;
    private const int MaxKeyPrefixLength = 253;

    private const int MaxReportedProblems = 20;
    private const int MaxEchoedLength = 80;

    /// <summary>Every entry that is not a legal Kubernetes node label, one message each, ordered by key; empty means applyable.
    /// <see cref="Parse"/> is deliberately permissive, so a value carrying shell quotes parsed and persisted and was refused only by the API server, every pass, behind a "Deploying" status — a placement must be judged where it is WRITTEN.</summary>
    public static IReadOnlyList<string> DescribeInvalidLabels(IReadOnlyDictionary<string, string> nodeSelector)
    {
        ArgumentNullException.ThrowIfNull(nodeSelector);

        var problems = new List<string>();
        foreach (var (key, value) in nodeSelector.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var reason = DescribeInvalidKey(key) ?? DescribeInvalidValue(value);
            if (reason is null)
            {
                continue;
            }

            if (problems.Count == MaxReportedProblems)
            {
                problems.Add("More entries are invalid; fix these first.");
                break;
            }

            problems.Add($"{Echo(key)}={Echo(value)} is not a valid node label: {reason}");
        }

        return problems;
    }

    /// <summary>The same verdict on a RAW placement expression, folding it exactly as <see cref="Parse"/> does.
    /// The fold lives here so a writer validating a raw value and the factory decoding it can never read one text two ways.</summary>
    public static IReadOnlyList<string> DescribeInvalidLabels(string? raw)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in Parse(raw))
        {
            labels[key] = value;
        }

        return DescribeInvalidLabels(labels);
    }

    /// <summary>The one operator-facing sentence for a refused placement, so the writers that report it cannot word
    /// it differently. Returns null when there is nothing to refuse.</summary>
    public static string? RefusalMessage(IReadOnlyCollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        return problems.Count == 0
            ? null
            : "The node selector is not a valid Kubernetes placement, so it could never be applied: "
                + string.Join(" ", problems);
    }

    private static string Echo(string? text)
    {
        var cleaned = new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= MaxEchoedLength ? cleaned : cleaned[..MaxEchoedLength] + "…";
    }

    private static string? DescribeInvalidKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "the label key is empty.";
        }

        var name = key;
        var slash = key.IndexOf('/');
        if (slash >= 0)
        {
            var prefix = key[..slash];
            name = key[(slash + 1)..];

            if (name.Contains('/'))
            {
                return "a label key carries at most one '/' separating its prefix from its name.";
            }

            if (prefix.Length == 0 || prefix.Length > MaxKeyPrefixLength || !DnsSubdomainPattern.IsMatch(prefix))
            {
                return $"the part before '/' must be a DNS subdomain (lowercase letters, digits, '-' and '.'), "
                    + $"at most {MaxKeyPrefixLength} characters.";
            }
        }

        if (name.Length == 0 || name.Length > MaxLabelNameLength || !LabelNamePattern.IsMatch(name))
        {
            return $"the name must be 1–{MaxLabelNameLength} characters of letters, digits, '-', '_' or '.', "
                + "starting and ending with a letter or digit.";
        }

        return null;
    }

    private static string? DescribeInvalidValue(string? value)
    {
        var text = value ?? string.Empty;

        if (text.Length > MaxLabelNameLength)
        {
            return $"the value is longer than {MaxLabelNameLength} characters.";
        }

        if (!LabelValuePattern.IsMatch(text))
        {
            var quoted = text.Length >= 2
                && ((text[0] == '\'' && text[^1] == '\'') || (text[0] == '"' && text[^1] == '"'));

            return quoted
                ? "the value is quoted — write it bare, without the surrounding quotes."
                : "the value may be empty or letters, digits, '-', '_' and '.', starting and ending with a letter or digit.";
        }

        return null;
    }
}
