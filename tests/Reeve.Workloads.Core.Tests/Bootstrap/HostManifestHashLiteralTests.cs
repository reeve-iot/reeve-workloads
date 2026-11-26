using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Bootstrap;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Tests.Manifest;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Manifest;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Core.Tests.Bootstrap;

public sealed class HostManifestHashLiteralTests
{
    private const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private const string HeartbeatFortyFiveHash = "3d86d957f050a707873321c1fd3bbb5fc2d5cc8086854450a4eaa41cbe5e13d7";

    private const string ApiSchemaTwoHash = "6ed19a56948205215d2c769f64f73f9a912cb9db3b533a5d8e969466f8231c49";

    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static string Manifest(string health, string reeve) => $$"""
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "bridge",
              "displayName": "Bridge",
              "image": { "ref": "registry.example.com/reeve/bridge", "digest": "{{Digest}}" },
              "kind": "service",
              "health": {{health}},
              "extensions": { "reeve": {{reeve}} },
              "env": [
                { "name": "REEVE_GATEWAY_URL", "from": "bootstrap:gatewayUrl" },
                { "name": "REEVE_WORKLOAD_TOKEN", "from": "bootstrap:workloadToken" }
              ]
            }
          ]
        }
        """;

    private const string Protocol = """{ "version": "1", "minSupported": "1" }""";

    [Fact]
    public async Task A_host_mode_manifest_declaring_a_45_second_heartbeat_keeps_its_hash()
    {
        var spec = await Render(Manifest(
            """{ "mode": "adapter-session", "heartbeatSeconds": 45 }""", $$"""{ "protocol": {{Protocol}} }"""));

        spec.SpecHash.Should().Be(HeartbeatFortyFiveHash);
    }

    [Fact]
    public async Task The_heartbeat_reaches_the_hash()
    {
        var spec = await Render(Manifest(
            """{ "mode": "adapter-session", "heartbeatSeconds": 30 }""", $$"""{ "protocol": {{Protocol}} }"""));

        spec.SpecHash.Should().NotBe(HeartbeatFortyFiveHash);
    }

    [Fact]
    public async Task A_manifest_declaring_an_api_schema_keeps_its_hash()
    {
        var spec = await Render(Manifest(
            """{ "mode": "adapter-session" }""", $$"""{ "protocol": {{Protocol}}, "apiSchema": "2" }"""));

        spec.Env.Should().ContainSingle(e => e.Name == "REEVE_API_SCHEMA")
            .Which.Should().Match<ResolvedEnvVar>(e => e.Kind == ResolvedEnvKind.Literal && e.Value == "2");
        spec.SpecHash.Should().Be(ApiSchemaTwoHash);
    }

    [Fact]
    public async Task A_manifest_declaring_no_api_schema_renders_no_api_schema_env()
    {
        var spec = await Render(Manifest("""{ "mode": "adapter-session" }""", $$"""{ "protocol": {{Protocol}} }"""));

        spec.Env.Should().NotContain(e => e.Name == "REEVE_API_SCHEMA");
    }

    private static async Task<WorkloadDesiredSpec> Render(string manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        var parsed = DeploymentManifestParser.Parse(document.RootElement, TestBootstrapValidator.ReeveKeys, out var errors);
        errors.Should().BeEmpty();
        var workload = parsed!.Workloads.Single();

        var request = WorkloadHostBootstrapStep.Request(
            InstanceId, "com.reeve.bridge", DockerProvider.Descriptor.Id, DefaultWorkloadResourceNamer.Instance, workload,
            WorkloadBootstrapAudience.Runtime);
        var composed = await WorkloadHostBootstrapStep.ComposeAsync(
            Host(), request, workload, CancellationToken.None);
        composed.Refusal.Should().BeNull();

        return WorkloadDesiredSpecFactory.Compute(
            workload,
            new WorkloadOrchestratorSettings(),
            new Dictionary<string, string?>(),
            WorkloadHostBootstrapStep.Context(request, composed),
            DockerProvider.Descriptor,
            DefaultWorkloadResourceNamer.Instance,
            GroupInstanceId,
            "com.reeve.bridge",
            InstanceId,
            "install-1");
    }

    private static IWorkloadHostBootstrapSource Host() => new ApiSchemaComposingHost(new FakeHostBootstrapSource());

    private sealed class ApiSchemaComposingHost(IWorkloadHostBootstrapSource inner) : IWorkloadHostBootstrapSource
    {
        public async Task<WorkloadHostBootstrapResult> GetAsync(
            WorkloadHostBootstrapRequest request, CancellationToken cancellationToken = default)
        {
            var answer = await inner.GetAsync(request, cancellationToken);
            return request.Extensions.For("reeve") is { } reeve
                && reeve.TryGetProperty("apiSchema", out var apiSchema)
                && apiSchema.ValueKind == JsonValueKind.String
                && apiSchema.GetString() is { } value
                && !string.IsNullOrWhiteSpace(value)
                    ? answer with
                    {
                        HostEnv =
                        [
                            .. answer.HostEnv,
                            new ResolvedEnvVar { Name = "REEVE_API_SCHEMA", Kind = ResolvedEnvKind.Literal, Value = value },
                        ],
                    }
                    : answer;
        }
    }
}
