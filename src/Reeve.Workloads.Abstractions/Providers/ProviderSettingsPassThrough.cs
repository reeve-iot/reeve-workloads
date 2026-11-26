using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>Keeps the properties of a provider's settings entry that its typed record does not own. A codec that
/// parses an entry into a record and serializes the record back would otherwise drop every key the record has
/// no property for, and the stored row is the only place a key that left a record still lives.</summary>
/// <remarks>Each method returns the <c>owned</c> element unchanged when it is not a JSON object. A property the
/// owned element already has is never replaced, whatever the key set says, so no result carries a key twice.
/// A property whose name matches an owned key in any casing is dropped rather than carried: the record never reads
/// it, and carrying it would store and echo a mis-cased copy of an owned secret unprotected and unmasked.</remarks>
public static class ProviderSettingsPassThrough
{
    /// <summary>The JSON property names a settings record owns: its [JsonPropertyName]s, else camelCase names.</summary>
    /// <typeparam name="TSettings">The provider's typed settings record.</typeparam>
    public static IReadOnlySet<string> OwnedKeys<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSettings>()
        => typeof(TSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary><paramref name="owned"/> plus every property of <paramref name="source"/> it does not own.</summary>
    /// <param name="owned">The typed record, serialized.</param>
    /// <param name="source">The entry the record was parsed from.</param>
    /// <param name="ownedKeys">What the record owns, from <see cref="OwnedKeys{TSettings}"/>.</param>
    public static JsonElement Carry(JsonElement owned, JsonElement source, IReadOnlySet<string> ownedKeys)
    {
        ArgumentNullException.ThrowIfNull(ownedKeys);

        if (owned.ValueKind is not JsonValueKind.Object || source.ValueKind is not JsonValueKind.Object)
        {
            return owned;
        }

        var carried = new List<JsonProperty>();
        foreach (var property in source.EnumerateObject())
        {
            if (!IsOwned(property.Name, ownedKeys) && !owned.TryGetProperty(property.Name, out _))
            {
                carried.Add(property);
            }
        }

        return With(owned, carried);
    }

    /// <summary>For a save: an unowned property the request carries (present, even as null) wins; otherwise the
    /// stored one is kept. An unowned property absent from both is absent.</summary>
    /// <param name="owned">The typed record the codec built from the request, serialized.</param>
    /// <param name="incoming">The request's entry.</param>
    /// <param name="stored">The stored entry, or null when nothing is stored.</param>
    /// <param name="ownedKeys">What the record owns, from <see cref="OwnedKeys{TSettings}"/>.</param>
    public static JsonElement Merge(
        JsonElement owned, JsonElement incoming, JsonElement? stored, IReadOnlySet<string> ownedKeys)
    {
        ArgumentNullException.ThrowIfNull(ownedKeys);

        if (owned.ValueKind is not JsonValueKind.Object)
        {
            return owned;
        }

        var carried = new List<JsonProperty>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in new[] { incoming, stored ?? default })
        {
            if (source.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in source.EnumerateObject())
            {
                if (!IsOwned(property.Name, ownedKeys)
                    && !owned.TryGetProperty(property.Name, out _)
                    && seen.Add(property.Name))
                {
                    carried.Add(property);
                }
            }
        }

        return With(owned, carried);
    }

    private static bool IsOwned(string name, IReadOnlySet<string> ownedKeys)
        => ownedKeys.Contains(name) || ownedKeys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    private static JsonElement With(JsonElement owned, IReadOnlyList<JsonProperty> carried)
    {
        if (carried.Count == 0)
        {
            return owned;
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in owned.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            foreach (var property in carried)
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
