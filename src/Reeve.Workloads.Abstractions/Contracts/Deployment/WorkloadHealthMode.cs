using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Contracts.Deployment;

/// <summary>The health mode, OPEN. The library knows <c>container</c> and <c>http</c> and evaluates them; any
/// other value is a host mode it carries verbatim and hands to the registered health contributor.</summary>
[JsonConverter(typeof(WorkloadHealthModeConverter))]
public readonly record struct WorkloadHealthMode
{
    private readonly string? _value;

    /// <summary>The wire value, exactly as authored. Never null — an uninitialized mode reads as the empty
    /// string, which is not library-owned, so it lands on the contributed-presence arm rather than nowhere.</summary>
    public string Value => _value ?? string.Empty;

    public WorkloadHealthMode(string value) => _value = value ?? string.Empty;

    /// <summary>Provider readiness is the whole signal (weakest; "basic health only").</summary>
    public static readonly WorkloadHealthMode Container = new("container");

    /// <summary>Readiness/liveness probes only.</summary>
    public static readonly WorkloadHealthMode Http = new("http");

    /// <summary>Whether the LIBRARY evaluates this mode itself. False means a host contributor owns the
    /// verdict — which is a decision the host made by registering one, never an omission.</summary>
    public bool IsLibraryOwned => Value is "container" or "http";

    public override string ToString() => Value;
}

/// <summary>Carries the mode as its bare wire string, in BOTH directions and with no rewriting: the manifest
/// parser and a stored spec must read one authored token the same way, or the health owner depends on the path.</summary>
public sealed class WorkloadHealthModeConverter : JsonConverter<WorkloadHealthMode>
{
    public override WorkloadHealthMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(ReadToken(ref reader));

    public override void Write(Utf8JsonWriter writer, WorkloadHealthMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);

    public override WorkloadHealthMode ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(ReadToken(ref reader));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, WorkloadHealthMode value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value);

    private static string ReadToken(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.String
            ? reader.GetString() ?? string.Empty
            : throw new JsonException($"health mode must be a JSON string but was {reader.TokenType}.");
}
