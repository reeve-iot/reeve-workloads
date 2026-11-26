using System.Collections.Frozen;
using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Core.Tests.Manifest;

internal sealed class TestBootstrapValidator(string @namespace, bool required, params string[] keys)
    : IWorkloadExtensionValidator
{
    public static readonly string[] ReeveHostKeys =
    [
        "gatewayUrl", "backendBaseUrl", "workloadToken", "mqttBrokerUrl", "mqttUsername", "mqttPassword",
    ];

    public static readonly IReadOnlyList<IWorkloadExtensionValidator> ReeveKeys =
    [
        new TestBootstrapValidator("reeve-test", required: false, ReeveHostKeys),
    ];

    public string Namespace => @namespace;

    public bool Required => required;

    public IReadOnlySet<string> BootstrapKeys { get; } = keys.ToFrozenSet(StringComparer.Ordinal);

    public void Validate(
        JsonElement workload, JsonElement extension, string path, ICollection<DeploymentValidationError> errors)
    {
    }
}
