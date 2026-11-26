using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Contracts.Deployment;

/// <summary>The open extension namespace: host-owned manifest data the library carries VERBATIM. Keyed by
/// host namespace, so one manifest addresses several hosts without any of them knowing the others.</summary>
[JsonConverter(typeof(WorkloadExtensionsConverter))]
public sealed record WorkloadExtensions
{
    /// <summary>Namespace to its raw object, exactly as authored. The library validates the SHAPE — an object
    /// whose values are objects — and nothing about the contents; a registered host validator does that.</summary>
    public IReadOnlyDictionary<string, JsonElement> ByNamespace { get; init; }
        = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>A workload addressing no host.</summary>
    public static readonly WorkloadExtensions None = new();

    /// <summary>The namespace's raw object, or null when this manifest says nothing to that host. Callers
    /// project it into their own types; the library has none to project into.</summary>
    public JsonElement? For(string extensionNamespace)
        => ByNamespace.TryGetValue(extensionNamespace, out var element) ? element : null;
}

/// <summary>Serializes as the WIRE shape — the bare namespace map — not as a wrapper object. A round trip through
/// JSON has to land back in the parser unchanged, and the parser reads <c>extensions</c> as the map itself.</summary>
public sealed class WorkloadExtensionsConverter : JsonConverter<WorkloadExtensions>
{
    public override WorkloadExtensions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return WorkloadExtensions.None;
        }

        var byNamespace = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            byNamespace[property.Name] = property.Value.Clone();
        }

        return byNamespace.Count == 0 ? WorkloadExtensions.None : new WorkloadExtensions { ByNamespace = byNamespace };
    }

    public override void Write(Utf8JsonWriter writer, WorkloadExtensions value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        foreach (var (extensionNamespace, element) in value.ByNamespace)
        {
            writer.WritePropertyName(extensionNamespace);
            element.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
