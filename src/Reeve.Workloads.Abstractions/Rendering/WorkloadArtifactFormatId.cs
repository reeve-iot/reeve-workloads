using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Rendering;

/// <summary>Names one operator-facing artifact format a desired workload spec can render into, for the operator to
/// copy into their own runtime. An OPEN vocabulary: each renderer declares the format it produces, in its own package.</summary>
/// <remarks>
/// <para>
/// The shipped renderers declare <c>compose</c>, <c>kubernetes</c>, <c>helm-values</c> and <c>env</c> — the tokens hosts already put on
/// the wire for them.
/// </para>
/// <para>
/// Nothing reads a format back from storage; writing needs the enum spelling in one place: see
/// <see cref="PersistedValue"/>.
/// </para>
/// </remarks>
[JsonConverter(typeof(WorkloadArtifactFormatIdJsonConverter))]
public readonly record struct WorkloadArtifactFormatId : IComparable<WorkloadArtifactFormatId>
{
    /// <summary>The longest id accepted — the same bound as a provider id, which a format is usually named after.</summary>
    public const int MaxLength = 32;

    private static readonly FrozenDictionary<string, string> SpellingsBefore700 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["compose"] = "Compose",
            ["kubernetes"] = "Kubernetes",
            ["helm-values"] = "HelmValues",
            ["env"] = "Env",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private WorkloadArtifactFormatId(string value) => Value = value;

    /// <summary>The canonical id: lowercase, starting with a letter or digit, then letters, digits, <c>-</c>,
    /// <c>_</c> or <c>.</c>. Default-constructed ids are empty — see <see cref="IsEmpty"/>.</summary>
    public string Value { get; }

    /// <summary>The spelling this format is recorded under in an audit entry's metadata: the canonical
    /// <see cref="Value"/>, except that the four original formats keep their enum member name.</summary>
    /// <remarks>An audit trail is read by people and by whatever the host feeds it into. The same render must not be spelled <c>HelmValues</c> in one entry and <c>helm-values</c> in another.</remarks>
    public string PersistedValue
        => Value is not null && SpellingsBefore700.TryGetValue(Value, out var before700) ? before700 : ToString();

    /// <summary>True for the default-constructed value, which names no format. A struct cannot forbid
    /// <c>default</c>, so every boundary that accepts a format has to be able to ask.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Parses and canonicalises a format id, throwing on anything the grammar rejects.</summary>
    public static WorkloadArtifactFormatId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return TryParse(value, out var id)
            ? id
            : throw new ArgumentException(
                $"'{value}' is not a valid artifact format id: expected 1-{MaxLength} characters matching "
                + "[a-z0-9][a-z0-9._-]*, compared case-insensitively.",
                nameof(value));
    }

    /// <summary>Parses and canonicalises a format id, returning false instead of throwing. Use at a wire boundary
    /// where an operator's typo should become a 400, not a 500.</summary>
    public static bool TryParse(string? value, out WorkloadArtifactFormatId id)
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

        id = new WorkloadArtifactFormatId(value.ToLowerInvariant());
        return true;
    }

    /// <summary>Ordinal equality over the canonical (lowercased) value.</summary>
    public bool Equals(WorkloadArtifactFormatId other)
        => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => Value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <inheritdoc />
    public int CompareTo(WorkloadArtifactFormatId other) => string.CompareOrdinal(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Reads and writes <see cref="WorkloadArtifactFormatId"/> as a plain JSON string, its canonical value.</summary>
public sealed class WorkloadArtifactFormatIdJsonConverter : JsonConverter<WorkloadArtifactFormatId>
{
    /// <inheritdoc />
    public override WorkloadArtifactFormatId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string artifact format id but found {reader.TokenType}.");
        }

        var raw = reader.GetString();
        return WorkloadArtifactFormatId.TryParse(raw, out var id)
            ? id
            : throw new JsonException($"'{raw}' is not a valid artifact format id.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, WorkloadArtifactFormatId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }

    /// <inheritdoc />
    public override WorkloadArtifactFormatId ReadAsPropertyName(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return WorkloadArtifactFormatId.TryParse(raw, out var id)
            ? id
            : throw new JsonException($"'{raw}' is not a valid artifact format id.");
    }

    /// <inheritdoc />
    public override void WriteAsPropertyName(
        Utf8JsonWriter writer, [DisallowNull] WorkloadArtifactFormatId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.Value);
    }
}
