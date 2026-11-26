using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Core.Tests;

internal static class ReeveTestExtensions
{
    public static WorkloadExtensions Pair(string version = "1", string minSupported = "1", string? apiSchema = null)
        => new()
        {
            ByNamespace = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["reeve"] = JsonSerializer.SerializeToElement(new { protocol = new { version, minSupported }, apiSchema }),
            },
        };
}
