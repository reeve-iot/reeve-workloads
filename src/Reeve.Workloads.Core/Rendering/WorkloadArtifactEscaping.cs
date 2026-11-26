using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Reeve.Workloads.Core.Rendering;

/// <summary>The one escaper the text renderers share. It exists because the parser is a choke point by convention
/// only — nothing structurally stops a manifest from reaching a renderer unparsed — and a rendered artifact is the
/// one place Reeve hands out that Reeve never re-validates: the operator runs it directly.</summary>
public static partial class WorkloadArtifactEscaping
{
    /// <summary>C_IDENTIFIER. The same grammar the manifest parser enforces on <c>env[].name</c>, restated here so
    /// a renderer is safe on its own rather than safe because someone upstream was careful.</summary>
    public static bool IsSafeEnvName(string? name)
        => !string.IsNullOrEmpty(name) && EnvNameRegex().IsMatch(name);

    /// <summary>A double-quoted YAML scalar with every control character escaped. Used for names AND values: a
    /// newline in EITHER half injects a sibling key into the operator's compose or values file.</summary>
    public static string YamlScalar(string? value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value ?? string.Empty)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary>A YAML scalar for a position the renderers currently write UNQUOTED: returned verbatim when it is
    /// unambiguously safe as a plain scalar, and as <see cref="YamlScalar"/> otherwise.</summary>
    /// <remarks>Two rules would each have been wrong on their own. Leaving these positions raw is what let a
    /// mountPath — validated only as "starts with /" — carry a newline and open a sibling key in the operator's
    /// values file. Quoting them all would have been safe but would have rewritten every artifact Reeve has ever
    /// rendered, for values like <c>/dev/ttyUSB0</c> and <c>ghcr.io/x@sha256:…</c> that were never in danger; a
    /// diff that large is one nobody reads, and the golden churn hides the change that matters. So: verbatim for
    /// the safe set, quoted for everything else. The set deliberately excludes a trailing ':' (ambiguous with a
    /// mapping key) and the YAML 1.1 boolean and null spellings, so a value of "off" or "null" is quoted into
    /// being a string rather than silently read as one.</remarks>
    public static string YamlPlainOrQuoted(string? value)
        => IsSafePlainScalar(value) ? value! : YamlScalar(value);

    private static bool IsSafePlainScalar(string? value)
        => !string.IsNullOrEmpty(value)
           && PlainScalarRegex().IsMatch(value)
           && value[^1] != ':'
           && !YamlBooleanOrNullSpellings.Contains(value);

    private static readonly HashSet<string> YamlBooleanOrNullSpellings = new(StringComparer.OrdinalIgnoreCase)
    {
        "y", "n", "yes", "no", "true", "false", "on", "off", "null", "~",
    };

    /// <summary>Whether a value can be written to a <c>.env</c> file at all. A line break ends the entry, so a
    /// value carrying one would inject a sibling key; nothing else in the value can change the file's structure.</summary>
    public static bool IsSafeDotEnvValue(string? value)
        => value is null || !value.Any(char.IsControl);

    /// <summary>A dotenv value, VERBATIM. dotenv has no portable quoting: `docker run --env-file` takes
    /// everything after the `=` literally, quotes included, so quoting here would ship the quotes to the process.</summary>
    /// <remarks>Measured on docker 29.1.3: `QUOTED="wss://x"` in an --env-file arrives as `"wss://x"` with the
    /// quote characters. Compose and systemd DO strip them, so a quoted file means two different things depending on who reads it. A value this cannot represent is dropped by the renderer and named in the notes, never quoted.</remarks>
    public static string DotEnvValue(string? value) => value ?? string.Empty;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex EnvNameRegex();

    [GeneratedRegex("^[A-Za-z0-9/][A-Za-z0-9._@:/+-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainScalarRegex();
}
