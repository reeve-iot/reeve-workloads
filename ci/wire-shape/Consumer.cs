using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.WireShape;

public sealed record PairingDocument
{
    [JsonPropertyName("version")] public required int Version { get; init; }

    [JsonPropertyName("twins")] public required List<string> Twins { get; init; }

    [JsonPropertyName("pairs")] public required Dictionary<string, string> Pairs { get; init; }

    [JsonPropertyName("backendOnly")] public Dictionary<string, string> BackendOnly { get; init; } = [];

    [JsonPropertyName("backendOnlyFiles")] public Dictionary<string, string> BackendOnlyFiles { get; init; } = [];

    [JsonPropertyName("frontendOnly")] public Dictionary<string, string> FrontendOnly { get; init; } = [];

    [JsonPropertyName("unmirroredKeys")] public Dictionary<string, List<string>> UnmirroredKeys { get; init; } = [];
}

public static class Consumer
{
    public static int Run(List<string> argv, Func<string, string, string> opt)
    {
        var shapePath = opt("--shape", "");
        var pairsPath = opt("--pairs", "");
        var consumerRoot = opt("--consumer-root", Directory.GetCurrentDirectory());
        if (shapePath.Length == 0 || pairsPath.Length == 0)
        {
            Console.Error.WriteLine("usage: WireShape parity --shape <wire-shape.json> --pairs <wire-pairs.json> " +
                                    "[--consumer-root <dir>]");
            return 2;
        }

        ShapeDocument shape;
        PairingDocument pairing;
        try
        {
            shape = JsonSerializer.Deserialize<ShapeDocument>(File.ReadAllText(shapePath), ShapeJson.Options)
                    ?? throw new InvalidOperationException("empty shape document");
            pairing = JsonSerializer.Deserialize<PairingDocument>(File.ReadAllText(pairsPath), ShapeJson.Options)
                      ?? throw new InvalidOperationException("empty pairing document");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"wire-parity: could not read the inputs: {ex.Message}");
            return 2;
        }

        var twins = new Dictionary<string, ZodSchema>(StringComparer.Ordinal);
        foreach (var rel in pairing.Twins)
        {
            var path = Path.Combine(consumerRoot, rel);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"wire-parity: declared twin file not found: {rel}");
                return 2;
            }
            foreach (var (name, schema) in ZodReader.Read(File.ReadAllText(path), rel))
                twins[name] = schema;
        }

        var problems = Compare(shape, pairing, twins);
        if (problems.Count == 0)
        {
            Console.WriteLine($"wire-parity OK: {pairing.Pairs.Count} paired contract types agree with their " +
                              $"zod twins across {pairing.Twins.Count} file(s); " +
                              $"{shape.Types.Count} published types and {twins.Count} schemas all accounted for.");
            return 0;
        }

        Console.Error.WriteLine($"wire-parity: {problems.Count} problem(s).");
        foreach (var p in problems) Console.Error.WriteLine("  " + p);
        return 1;
    }

    public static List<string> Compare(ShapeDocument shape, PairingDocument pairing,
                                       IReadOnlyDictionary<string, ZodSchema> twins)
    {
        var problems = new List<string>();
        var byName = shape.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);

        foreach (var type in shape.Types)
        {
            var fileExcused = pairing.BackendOnlyFiles.ContainsKey(type.File);
            var classifications = 0;
            if (pairing.Pairs.ContainsKey(type.Name)) classifications++;
            if (pairing.BackendOnly.ContainsKey(type.Name)) classifications++;
            if (fileExcused) classifications++;
            if (classifications == 0)
                problems.Add($"{type.Name} ({type.File}) is published but this consumer says nothing about it: " +
                             "pair it with a zod schema, or record in backendOnly why nothing here parses it.");
            else if (classifications > 1)
                problems.Add($"{type.Name} is classified {classifications} times; each type is paired, " +
                             "backend-only, or excused by its file - exactly one.");
            if (type.Kind == "custom" && pairing.Pairs.ContainsKey(type.Name))
                problems.Add($"{type.Name} is written by a [JsonConverter], so its shape is not derivable and " +
                             "pairing it would compare nothing. Record it in backendOnly with the reason.");
        }

        foreach (var (typeName, schemaName) in pairing.Pairs)
        {
            if (!byName.ContainsKey(typeName))
            {
                problems.Add($"pairs names contract type {typeName}, which the published shape does not " +
                             "contain. Either it was renamed on the library side only, or the pin moved.");
                continue;
            }
            if (!twins.ContainsKey(schemaName))
            {
                problems.Add($"pairs names zod schema {schemaName}, which none of the twin files declares. " +
                             "Either it was renamed on this side only, or the twin list is short.");
                continue;
            }
            pairing.UnmirroredKeys.TryGetValue(typeName, out var allowed);
            problems.AddRange(ComparePair(byName[typeName], schemaName, twins[schemaName], allowed ?? []));
        }

        foreach (var (name, reason) in pairing.BackendOnly.Concat(pairing.FrontendOnly).Concat(pairing.BackendOnlyFiles))
            if (string.IsNullOrWhiteSpace(reason))
                problems.Add($"{name} is excused with an empty reason; say why, or pair it.");

        var files = shape.Types.Select(t => t.File).ToHashSet(StringComparer.Ordinal);
        foreach (var f in pairing.BackendOnlyFiles.Keys.Where(f => !files.Contains(f)).Order(StringComparer.Ordinal))
            problems.Add($"backendOnlyFiles names {f}, which publishes no type - drop it rather than " +
                         "leaving an exemption that stands for nothing.");

        var paired = pairing.Pairs.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var name in twins.Keys.Order(StringComparer.Ordinal))
            if (!paired.Contains(name) && !pairing.FrontendOnly.ContainsKey(name))
                problems.Add($"zod schema {name} is declared in a twin file but pairs with nothing: " +
                             "pair it, or record in frontendOnly what it actually mirrors.");
        foreach (var name in pairing.FrontendOnly.Keys.Order(StringComparer.Ordinal))
            if (!twins.ContainsKey(name))
                problems.Add($"frontendOnly names {name}, which no twin file declares any more.");

        problems.Sort(StringComparer.Ordinal);
        return problems;
    }

    private static IEnumerable<string> ComparePair(WireType type, string schemaName, ZodSchema schema,
                                                  IReadOnlyList<string> allowed)
    {
        if (type.Kind is "enum" or "vocabulary")
        {
            if (schema.Kind != ZodKind.Enum)
            {
                yield return $"{type.Name} is a closed {type.Kind} but {schemaName} is not a z.enum.";
                yield break;
            }
            var backend = (type.Values ?? []).ToHashSet(StringComparer.Ordinal);
            var frontend = schema.Members.ToHashSet(StringComparer.Ordinal);
            if (!backend.SetEquals(frontend))
                yield return $"{type.Name} / {schemaName}: the vocabularies differ. " +
                             $"backend-only: [{string.Join(", ", backend.Except(frontend).Order(StringComparer.Ordinal))}]; " +
                             $"frontend-only: [{string.Join(", ", frontend.Except(backend).Order(StringComparer.Ordinal))}]. " +
                             "A value the twin does not list is a hard parse error the moment it is sent.";
            yield break;
        }

        if (schema.Kind != ZodKind.Object)
        {
            yield return $"{type.Name} serializes as an object but {schemaName} is not a z.object.";
            yield break;
        }

        var backendKeys = (type.Properties ?? []).Select(p => p.Wire).ToHashSet(StringComparer.Ordinal);
        var frontendKeys = schema.Members.ToHashSet(StringComparer.Ordinal);
        var unmirrored = allowed.ToHashSet(StringComparer.Ordinal);
        var backendOnly = backendKeys.Except(frontendKeys).Except(unmirrored).Order(StringComparer.Ordinal).ToList();
        var frontendOnly = frontendKeys.Except(backendKeys).Order(StringComparer.Ordinal).ToList();
        if (backendOnly.Count > 0)
            yield return $"{type.Name} / {schemaName}: the backend sends [{string.Join(", ", backendOnly)}], " +
                         "which the twin does not declare - rendered by nothing. Mirror it, or list it under " +
                         "unmirroredKeys with a reason so the NEXT one still fails.";
        if (frontendOnly.Count > 0)
            yield return $"{type.Name} / {schemaName}: the twin declares [{string.Join(", ", frontendOnly)}], " +
                         "which the backend never sends - it parses to undefined on every response.";
        foreach (var key in unmirrored.Except(backendKeys).Order(StringComparer.Ordinal))
            yield return $"{type.Name} / {schemaName}: unmirroredKeys lists `{key}`, which the backend does not " +
                         "send any more - drop the entry rather than leaving an excuse for nothing.";

        foreach (var p in (type.Properties ?? []).Where(p => p.Nullable))
            if (frontendKeys.Contains(p.Wire) && !schema.Nullable.Contains(p.Wire))
                yield return $"{type.Name} / {schemaName}: `{p.Wire}` is nullable on the backend but the twin " +
                             "does not accept null, so one null fails the whole parse.";
    }
}
