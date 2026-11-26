using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;

namespace Reeve.Workloads.Domain;

/// <summary>The normalized SHA-256 digest of an <c>unsupportedPodPatch</c> body: keys sorted recursively, no
/// insignificant whitespace, array order preserved — so a body swap can never slip past a patch-presence bit.</summary>
public static class WorkloadPatchDigest
{
    /// <summary>The normalized digest of the patch body, or <c>null</c> when the workload declares no patch.</summary>
    public static string? Normalize(UnsupportedPodPatch? patch)
        => patch is null ? null : Normalize(patch.Body);

    /// <summary>The normalized digest of an arbitrary JSON element, exposed so a later gate can re-hash a
    /// freshly-rendered patch without reconstructing the typed wrapper.</summary>
    public static string Normalize(JsonElement body)
    {
        var canonical = Canonicalize(body);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Canonicalize(JsonElement element)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, element);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string NormalizeDecimal(decimal value)
    {
        var s = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s.Contains('.'))
        {
            s = s.TrimEnd('0').TrimEnd('.');
            if (s.Length == 0 || s == "-")
                s = "0";
        }
        return s;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.Number:
                if (element.TryGetDecimal(out var dec))
                    writer.WriteRawValue(NormalizeDecimal(dec), skipInputValidation: true);
                else
                    writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }
}
