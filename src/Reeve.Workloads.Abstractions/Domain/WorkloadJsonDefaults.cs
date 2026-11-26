using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Domain;

/// <summary>The workloads side's own copy of the canonical wire policy. A copy rather than a reference, because
/// the library cannot depend on Reeve.Contracts; a parity test pins the two to the same settings.</summary>
public static class WorkloadJsonDefaults
{
    /// <summary>Equivalent to <c>new JsonSerializerOptions(JsonSerializerDefaults.Web)</c> with the canonical
    /// properties pinned explicitly. Do not mutate — meant to be shared.</summary>
    /// <remarks>It sets no <see cref="JsonSerializerOptions.TypeInfoResolver"/>. Trimmed and NativeAOT code copies
    /// it with <c>new JsonSerializerOptions(CanonicalWire)</c> and sets a source-generated context as the resolver.</remarks>
    public static JsonSerializerOptions CanonicalWire { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
