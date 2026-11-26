using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.WireShape;

public sealed record ShapeDocument
{
    [JsonPropertyName("version")] public required int Version { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("types")] public required List<WireType> Types { get; init; }
}

public static class ShapeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class ShapeDiff
{
    public static IEnumerable<string> Describe(string oldJson, string newJson)
    {
        var a = Index(oldJson);
        var b = Index(newJson);
        foreach (var name in a.Keys.Except(b.Keys).Order(StringComparer.Ordinal))
            yield return $"REMOVED type {name}";
        foreach (var name in b.Keys.Except(a.Keys).Order(StringComparer.Ordinal))
            yield return $"ADDED   type {name}";
        foreach (var name in a.Keys.Intersect(b.Keys).Order(StringComparer.Ordinal))
        {
            if (Canonical(a[name]) == Canonical(b[name])) continue;
            yield return $"CHANGED type {name}";
            foreach (var line in Members(a[name], b[name])) yield return "    " + line;
        }
    }

    private static string Canonical(WireType t) => JsonSerializer.Serialize(t, ShapeJson.Options);

    private static IEnumerable<string> Members(WireType a, WireType b)
    {
        if (a.Kind != b.Kind) { yield return $"kind {a.Kind} -> {b.Kind}"; yield break; }
        if (a.Values is not null || b.Values is not null)
        {
            var av = a.Values ?? []; var bv = b.Values ?? [];
            foreach (var v in av.Except(bv)) yield return $"value REMOVED {v}";
            foreach (var v in bv.Except(av)) yield return $"value ADDED   {v}";
            yield break;
        }
        var ap = (a.Properties ?? []).ToDictionary(p => p.Wire);
        var bp = (b.Properties ?? []).ToDictionary(p => p.Wire);
        foreach (var k in ap.Keys.Except(bp.Keys).Order(StringComparer.Ordinal)) yield return $"key REMOVED {k}";
        foreach (var k in bp.Keys.Except(ap.Keys).Order(StringComparer.Ordinal)) yield return $"key ADDED   {k}";
        foreach (var k in ap.Keys.Intersect(bp.Keys).Order(StringComparer.Ordinal))
        {
            if (ap[k].Kind == bp[k].Kind && ap[k].Nullable == bp[k].Nullable &&
                ap[k].Required == bp[k].Required && ap[k].Clr == bp[k].Clr) continue;
            yield return $"key CHANGED {k}: {Describe(ap[k])} -> {Describe(bp[k])}";
        }
    }

    private static string Describe(WireProperty p)
        => $"{p.Kind}{(p.Nullable ? "?" : "")}{(p.Required ? " required" : "")}";

    private static Dictionary<string, WireType> Index(string json)
    {
        var doc = JsonSerializer.Deserialize<ShapeDocument>(json, ShapeJson.Options);
        return doc is null ? [] : doc.Types.ToDictionary(t => t.Name);
    }
}
