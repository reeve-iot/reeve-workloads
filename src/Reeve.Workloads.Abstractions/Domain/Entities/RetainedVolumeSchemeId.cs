using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Domain.Entities;

/// <summary>Names the naming scheme a retained volume's physical name follows. Distinct from the runtime
/// provider, because a MANUAL instance renders several artifacts and only the APPLIED one's scheme names a
/// volume that really exists.</summary>
/// <remarks>
/// <para>
/// An OPEN vocabulary: a resource namer
/// shipped by a third-party provider names volumes its own way, and a scheme it cannot record is a scheme a
/// later restore cannot resolve — the row would claim a physical name nothing ever created.
/// </para>
/// <para>
/// The two original schemes are persisted as their MEMBER NAME, in a column and inside a retained-volume snapshot document, so
/// <see cref="Parse"/> is case-INSENSITIVE exactly as <c>WorkloadProviderId</c> is: a stored row holds <c>Kubernetes</c> or <c>Docker</c>, and a strict parse would fail to read it.
/// </para>
/// <para>
/// Those two are also still WRITTEN that way — see <see cref="PersistedValue"/>.
/// </para>
/// </remarks>
[JsonConverter(typeof(RetainedVolumeSchemeIdJsonConverter))]
public readonly record struct RetainedVolumeSchemeId : IComparable<RetainedVolumeSchemeId>
{
    /// <summary>The longest id that fits the persisted column, which is <c>nvarchar(32)</c>.</summary>
    public const int MaxLength = 32;

    /// <summary>The RFC 1123 object scheme (<c>reeve-wl-…</c>) — a Kubernetes PVC, and the artifacts a manual
    /// install renders. The default.</summary>
    public static RetainedVolumeSchemeId Kubernetes { get; } = new("kubernetes");

    /// <summary>The docker named-volume scheme (<c>reeve-…</c>, no <c>-wl-</c> segment).</summary>
    public static RetainedVolumeSchemeId Docker { get; } = new("docker");

    private static readonly FrozenDictionary<string, string> SpellingsBefore700 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kubernetes"] = "Kubernetes",
            ["docker"] = "Docker",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private RetainedVolumeSchemeId(string value) => Value = value;

    /// <summary>The canonical id: lowercase, starting with a letter or digit, then letters, digits, <c>-</c>,
    /// <c>_</c> or <c>.</c>. Default-constructed ids are empty — see <see cref="IsEmpty"/>.</summary>
    public string Value { get; }

    /// <summary>The spelling this id is written under wherever it is stored: the canonical <see cref="Value"/>,
    /// except that the two original schemes keep their enum member name.</summary>
    /// <remarks>Reading does not need this — <see cref="Parse"/> accepts either spelling. Writing does, for two
    /// reasons. A reader that matches a snapshot document's scheme CASE-SENSITIVELY would silently lose the retained pointer of every volume a lowercase write touched. And the column's default value goes through the same spelling, so it stays <c>'Kubernetes'</c>, the model snapshot does not move, and a table never holds both spellings of one scheme.</remarks>
    public string PersistedValue
        => Value is not null && SpellingsBefore700.TryGetValue(Value, out var before700) ? before700 : ToString();

    /// <summary>True for the default-constructed value, which names no scheme. A struct cannot forbid
    /// <c>default</c>, so every boundary that accepts a scheme has to be able to ask.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Parses and canonicalises a scheme id, throwing on anything the grammar rejects.</summary>
    public static RetainedVolumeSchemeId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return TryParse(value, out var id)
            ? id
            : throw new ArgumentException(
                $"'{value}' is not a valid retained-volume scheme id: expected 1-{MaxLength} characters matching "
                + "[a-z0-9][a-z0-9._-]*, compared case-insensitively.",
                nameof(value));
    }

    /// <summary>Parses and canonicalises a scheme id, returning false instead of throwing.</summary>
    public static bool TryParse(string? value, out RetainedVolumeSchemeId id)
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

        id = new RetainedVolumeSchemeId(value.ToLowerInvariant());
        return true;
    }

    /// <summary>Ordinal equality over the canonical (lowercased) value.</summary>
    public bool Equals(RetainedVolumeSchemeId other)
        => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => Value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <inheritdoc />
    public int CompareTo(RetainedVolumeSchemeId other) => string.CompareOrdinal(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Reads and writes <see cref="RetainedVolumeSchemeId"/> as a plain JSON string. A snapshot document holding the enum's member name reads back through the case-insensitive parse.</summary>
public sealed class RetainedVolumeSchemeIdJsonConverter : JsonConverter<RetainedVolumeSchemeId>
{
    /// <inheritdoc />
    public override RetainedVolumeSchemeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string retained-volume scheme id but found {reader.TokenType}.");
        }

        var raw = reader.GetString();
        return RetainedVolumeSchemeId.TryParse(raw, out var id)
            ? id
            : throw new JsonException($"'{raw}' is not a valid retained-volume scheme id.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RetainedVolumeSchemeId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.PersistedValue);
    }

    /// <inheritdoc />
    public override RetainedVolumeSchemeId ReadAsPropertyName(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return RetainedVolumeSchemeId.TryParse(raw, out var id)
            ? id
            : throw new JsonException($"'{raw}' is not a valid retained-volume scheme id.");
    }

    /// <inheritdoc />
    public override void WriteAsPropertyName(
        Utf8JsonWriter writer, [DisallowNull] RetainedVolumeSchemeId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.PersistedValue);
    }
}
