using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reeve.Workloads.WireShape;

public sealed record WireProperty
{
    [JsonPropertyName("wire")] public required string Wire { get; init; }
    [JsonPropertyName("clr")] public required string Clr { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("required")] public required bool Required { get; init; }
    [JsonPropertyName("nullable")] public required bool Nullable { get; init; }
}

public sealed record WireType
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("file")] public required string File { get; init; }
    [JsonPropertyName("properties")] public List<WireProperty>? Properties { get; init; }
    [JsonPropertyName("values")] public List<string>? Values { get; init; }

    [JsonPropertyName("converter")] public string? Converter { get; init; }
}

public static class ShapeExtractor
{
    public sealed class UnreadableShapeException(IReadOnlyList<string> problems)
        : Exception(string.Join(Environment.NewLine, problems))
    {
        public IReadOnlyList<string> Problems { get; } = problems;
    }

    public static List<WireType> ForFile(string absolutePath, string declaredRelativePath)
        => ForFiles([(absolutePath, declaredRelativePath)]);

    public static List<WireType> ForFiles(IReadOnlyList<(string Absolute, string Relative)> files)
    {
        var all = new List<WireType>();
        var problems = new List<string>();
        foreach (var (abs, rel) in files) all.AddRange(OneFile(abs, rel, problems));
        if (problems.Count > 0) throw new UnreadableShapeException(problems);
        all.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return all;
    }

    private static List<WireType> OneFile(string absolutePath, string declaredRelativePath, List<string> problems)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(File.ReadAllText(absolutePath)).GetRoot();
        var types = new List<WireType>();
        foreach (var decl in TopLevelTypes(root))
        {
            if (!decl.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;
            var t = Reduce(decl, declaredRelativePath, problems);
            if (t is not null) types.Add(t);
        }
        types.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return types;
    }

    private static IEnumerable<BaseTypeDeclarationSyntax> TopLevelTypes(CompilationUnitSyntax root)
    {
        foreach (var member in root.Members)
        {
            if (member is BaseTypeDeclarationSyntax t) yield return t;
            else if (member is BaseNamespaceDeclarationSyntax ns)
                foreach (var inner in ns.Members)
                    if (inner is BaseTypeDeclarationSyntax nt) yield return nt;
        }
    }

    private static WireType? Reduce(BaseTypeDeclarationSyntax decl, string file, List<string> problems)
    {
        if (decl is EnumDeclarationSyntax e)
        {
            var values = e.Members.Select(WireNameOf).ToList();
            return new WireType { Name = e.Identifier.Text, Kind = "enum", File = file, Values = values };
        }

        if (decl is not TypeDeclarationSyntax type) return null;

        var converter = ConverterOf(type);
        if (converter is not null)
            return new WireType
            {
                Name = type.Identifier.Text,
                Kind = "custom",
                File = file,
                Converter = converter,
            };

        var consts = type.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword) && f.Declaration.Type.ToString() == "string")
            .SelectMany(f => f.Declaration.Variables)
            .Select(v => (v.Initializer?.Value as LiteralExpressionSyntax)?.Token.ValueText)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList();
        if (type.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            if (consts.Count == 0) return null;
            return new WireType { Name = type.Identifier.Text, Kind = "vocabulary", File = file, Values = consts };
        }

        var props = new List<WireProperty>();
        foreach (var p in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!p.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;
            if (p.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
            if (HasAttribute(p, "JsonIgnore")) continue;
            if (p.ExpressionBody is not null) continue;
            if (p.AccessorList is not null && !p.AccessorList.Accessors.Any(a =>
                    a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration)))
                continue;

            var wire = JsonNameOf(p);
            if (wire is null)
            {
                problems.Add($"{file}: {type.Identifier.Text}.{p.Identifier.Text} carries no " +
                             "[JsonPropertyName]; name the wire key explicitly.");
                continue;
            }

            var clrType = p.Type.ToString();
            props.Add(new WireProperty
            {
                Wire = wire,
                Clr = p.Identifier.Text,
                Kind = WireKind(clrType),
                Required = p.Modifiers.Any(SyntaxKind.RequiredKeyword),
                Nullable = clrType.EndsWith('?'),
            });
        }

        if (props.Count == 0) return null;
        props.Sort((a, b) => string.CompareOrdinal(a.Wire, b.Wire));
        return new WireType { Name = type.Identifier.Text, Kind = "object", File = file, Properties = props };
    }

    private static string WireNameOf(EnumMemberDeclarationSyntax m)
        => AttributeArgument(m.AttributeLists, "JsonStringEnumMemberName") ?? m.Identifier.Text;

    private static string? JsonNameOf(PropertyDeclarationSyntax p)
        => AttributeArgument(p.AttributeLists, "JsonPropertyName");

    private static string? ConverterOf(TypeDeclarationSyntax type)
    {
        foreach (var a in type.AttributeLists.SelectMany(l => l.Attributes))
        {
            var n = a.Name.ToString();
            if (n != "JsonConverter" && n != "JsonConverterAttribute") continue;
            var arg = a.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
            return arg is TypeOfExpressionSyntax t ? t.Type.ToString() : arg?.ToString() ?? "unknown";
        }
        return null;
    }

    private static bool HasAttribute(MemberDeclarationSyntax m, string name)
        => m.AttributeLists.SelectMany(l => l.Attributes)
            .Any(a => a.Name.ToString() is var n && (n == name || n == name + "Attribute"));

    private static string? AttributeArgument(SyntaxList<AttributeListSyntax> lists, string name)
    {
        foreach (var a in lists.SelectMany(l => l.Attributes))
        {
            var n = a.Name.ToString();
            if (n != name && n != name + "Attribute") continue;
            var arg = a.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
            if (arg is not null) return arg.Token.ValueText;
        }
        return null;
    }

    public static string WireKind(string clrType)
    {
        var t = clrType.Trim().TrimEnd('?').Trim();
        if (t.EndsWith("[]")) return "array";
        foreach (var c in new[] { "IReadOnlyList<", "IReadOnlyCollection<", "IList<", "List<", "IEnumerable<" })
            if (t.StartsWith(c, StringComparison.Ordinal)) return "array";
        foreach (var c in new[] { "IReadOnlyDictionary<", "IDictionary<", "Dictionary<" })
            if (t.StartsWith(c, StringComparison.Ordinal)) return "record";
        return t switch
        {
            "string" or "Guid" or "DateTimeOffset" or "DateTime" or "TimeSpan" or "Uri" => "string",
            "int" or "long" or "short" or "byte" or "double" or "float" or "decimal" => "number",
            "bool" => "boolean",
            "JsonElement" or "object" or "JsonNode" => "unknown",
            _ => "ref",
        };
    }
}
