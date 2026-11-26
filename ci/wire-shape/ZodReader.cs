using System.Text.RegularExpressions;

namespace Reeve.Workloads.WireShape;

public enum ZodKind { Object, Enum, Other }

public sealed record ZodSchema
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public required ZodKind Kind { get; init; }

    public required IReadOnlyList<string> Members { get; init; }

    public required IReadOnlySet<string> Nullable { get; init; }

    public bool Passthrough { get; init; }
}

public static partial class ZodReader
{
    [GeneratedRegex(@"export\s+const\s+(\w+)\s*=\s*", RegexOptions.Compiled)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"^\s*'([^']+)'\s*$", RegexOptions.Compiled)]
    private static partial Regex QuotedMember();

    [GeneratedRegex(@"^([A-Za-z_$][\w$]*)\s*:", RegexOptions.Compiled)]
    private static partial Regex ObjectKey();

    [GeneratedRegex(@"\bz\s*\.\s*object\s*\(", RegexOptions.Compiled)]
    private static partial Regex ObjectHead();

    [GeneratedRegex(@"\bz\s*\.\s*enum\s*\(", RegexOptions.Compiled)]
    private static partial Regex EnumHead();

    public static IEnumerable<(string Name, ZodSchema Schema)> Read(string source, string file)
    {
        var text = StripComments(source);
        foreach (Match m in Declaration().Matches(text))
        {
            var name = m.Groups[1].Value;
            if (!name.EndsWith("Schema", StringComparison.Ordinal)) continue;
            var rest = text[m.Index..];
            var schema = Parse(name, file, rest);
            if (schema is not null) yield return (name, schema);
        }
    }

    private static ZodSchema? Parse(string name, string file, string rest)
    {
        var head = rest[..Math.Min(rest.Length, 200)];
        var enumHead = EnumHead().Match(head);
        if (enumHead.Success)
        {
            var body = Between(rest, rest.IndexOf('[', enumHead.Index), '[', ']');
            var members = body.Split(',').Select(s => QuotedMember().Match(s))
                .Where(x => x.Success).Select(x => x.Groups[1].Value).ToList();
            return new ZodSchema
            {
                Name = name, File = file, Kind = ZodKind.Enum, Members = members,
                Nullable = new HashSet<string>(StringComparer.Ordinal),
            };
        }

        var objectHead = ObjectHead().Match(head);
        if (!objectHead.Success) return new ZodSchema
        {
            Name = name, File = file, Kind = ZodKind.Other, Members = [],
            Nullable = new HashSet<string>(StringComparer.Ordinal),
        };

        var brace = rest.IndexOf('{', objectHead.Index);
        var objectBody = Between(rest, brace, '{', '}');
        var keys = new List<string>();
        var nullable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in TopLevelLines(objectBody))
        {
            var key = ObjectKey().Match(line.TrimStart());
            if (!key.Success) continue;
            keys.Add(key.Groups[1].Value);
            if (AcceptsNull(line)) nullable.Add(key.Groups[1].Value);
        }
        var after = rest[(brace + objectBody.Length + 2)..];
        var tail = after[..Math.Min(after.Length, 80)];
        return new ZodSchema
        {
            Name = name, File = file, Kind = ZodKind.Object, Members = keys, Nullable = nullable,
            Passthrough = tail.Contains(".passthrough()", StringComparison.Ordinal),
        };
    }

    private static bool AcceptsNull(string entry)
    {
        var depth = 0;
        for (var i = 0; i < entry.Length; i++)
        {
            if (entry[i] == '{') depth++;
            else if (entry[i] == '}') depth--;
            else if (depth == 0 && entry[i] == '.')
            {
                if (entry.AsSpan(i).StartsWith(".nullable()") || entry.AsSpan(i).StartsWith(".nullish()")) return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> TopLevelLines(string body)
    {
        var depth = 0;
        var current = new System.Text.StringBuilder();
        foreach (var c in body)
        {
            if (c is '{' or '(' or '[') depth++;
            else if (c is '}' or ')' or ']') depth--;
            if (depth == 0 && c == ',') { yield return current.ToString(); current.Clear(); continue; }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static string Between(string text, int open, char openChar, char closeChar)
    {
        if (open < 0) return "";
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == openChar) depth++;
            else if (text[i] == closeChar)
            {
                depth--;
                if (depth == 0) return text[(open + 1)..i];
            }
        }
        return "";
    }

    private static string StripComments(string source)
    {
        var outText = new System.Text.StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                continue;
            }
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/' && StartsLine(source, i))
            {
                var end = source.IndexOf('\n', i);
                i = end < 0 ? source.Length : end;
                continue;
            }
            outText.Append(source[i]);
            i++;
        }
        return outText.ToString();
    }

    private static bool StartsLine(string source, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (source[i] == '\n') return true;
            if (!char.IsWhiteSpace(source[i])) return false;
        }
        return true;
    }
}
