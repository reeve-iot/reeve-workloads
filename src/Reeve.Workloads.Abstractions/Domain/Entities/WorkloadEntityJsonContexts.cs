using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reeve.Workloads.Abstractions.Domain.Entities;

[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class WorkloadEntityJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ReferenceHashPin))]
internal sealed partial class WorkloadEntityWebJsonContext : JsonSerializerContext;
