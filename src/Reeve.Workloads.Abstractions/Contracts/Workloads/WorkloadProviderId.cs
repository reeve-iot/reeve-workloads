using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Contracts;

/// <summary>Names one workload provider. An OPEN vocabulary: every provider, the shipped ones included, declares
/// its own id in its own package, and the library never needs to know about any of them.</summary>
/// <remarks>
/// The shape below is deliberately tolerant on the way in and canonical on the way out — see <see cref="Parse"/>.
/// </remarks>
[JsonConverter(typeof(WorkloadProviderIdJsonConverter))]
public readonly record struct WorkloadProviderId : IComparable<WorkloadProviderId>
{
    /// <summary>The longest id that fits the persisted column. Enforced at construction so a provider cannot
    /// register an id that only fails later, on the first row it tries to write.</summary>
    public const int MaxLength = 32;

    private WorkloadProviderId(string value) => Value = value;

    /// <summary>The canonical id: lowercase, starting with a letter or digit, then letters, digits,
    /// <c>-</c>, <c>_</c> or <c>.</c>. Default-constructed ids are empty — see <see cref="IsEmpty"/>.</summary>
    public string Value { get; }

    /// <summary>True for the default-constructed value, which names no provider. A struct cannot forbid
    /// <c>default</c>, so every boundary that accepts an id has to be able to ask.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Parses and canonicalises an id, throwing on anything the grammar rejects.</summary>
    /// <remarks>
    /// Case-INSENSITIVE by design: a stored row may hold the PascalCase spelling (<c>Kubernetes</c>), and a strict parse would fail to read it.
    /// </remarks>
    public static WorkloadProviderId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return TryParse(value, out var id)
            ? id
            : throw new ArgumentException(
                $"'{value}' is not a valid workload provider id: expected 1-{MaxLength} characters matching "
                + "[a-z0-9][a-z0-9._-]*, compared case-insensitively.",
                nameof(value));
    }

    /// <summary>Parses and canonicalises an id, returning false instead of throwing. Use at a wire boundary
    /// where an operator's typo should become a 400, not a 500.</summary>
    public static bool TryParse(string? value, out WorkloadProviderId id)
    {
        id = default;

        if (value is null || value.Length is 0 or > MaxLength)
        {
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        id = new WorkloadProviderId(value.ToLowerInvariant());
        return true;
    }

    /// <summary>Ordinal equality over the canonical (lowercased) value.</summary>
    public bool Equals(WorkloadProviderId other)
        => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => Value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <inheritdoc />
    public int CompareTo(WorkloadProviderId other)
        => string.CompareOrdinal(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Reads and writes <see cref="WorkloadProviderId"/> as a plain JSON string.</summary>
public sealed class WorkloadProviderIdJsonConverter : JsonConverter<WorkloadProviderId>
{
    /// <inheritdoc />
    public override WorkloadProviderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected a string workload provider id but found {reader.TokenType}.");
        }

        var raw = reader.GetString();
        return WorkloadProviderId.TryParse(raw, out var id)
            ? id
            : throw new JsonException($"'{raw}' is not a valid workload provider id.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, WorkloadProviderId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }

    /// <inheritdoc />
    public override WorkloadProviderId ReadAsPropertyName(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return WorkloadProviderId.TryParse(raw, out var id)
            ? id
            : throw new JsonException($"'{raw}' is not a valid workload provider id.");
    }

    /// <inheritdoc />
    public override void WriteAsPropertyName(
        Utf8JsonWriter writer, [DisallowNull] WorkloadProviderId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.Value);
    }
}
