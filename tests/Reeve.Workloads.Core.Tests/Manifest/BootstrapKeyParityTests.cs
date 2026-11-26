using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Manifest;

namespace Reeve.Workloads.Core.Tests.Manifest;

public sealed class BootstrapKeyParityTests
{
    private const string GoodDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private const string MqttBootstrapForbiddenForAdapter = "DEPLOYMENT_MQTT_BOOTSTRAP_FORBIDDEN_FOR_ADAPTER";

    private static readonly IReadOnlyList<IWorkloadExtensionValidator> ReeveValidators = [new ReeveRules()];

    private const string ReeveExtension =
        "\"extensions\": { \"reeve\": { \"protocol\": { \"version\": \"1\", \"minSupported\": \"1\" } } }";

    private const string HttpHealth = "\"health\": { \"mode\": \"http\", \"http\": { \"port\": 8080 } }";

    private const string AdapterHealth = "\"health\": { \"mode\": \"adapter-session\" }";

    private static string Workload(string health, string body = "", string extensions = ReeveExtension) => $$"""
        {
          "id": "w",
          "displayName": "W",
          "image": { "ref": "registry.example.com/reeve/w", "digest": "{{GoodDigest}}" },
          "kind": "service",
          {{health}}
          {{(string.IsNullOrEmpty(extensions) ? "" : "," + extensions)}}
          {{(string.IsNullOrEmpty(body) ? "" : "," + body)}}
        }
        """;

    private static string Env(params string[] froms) =>
        "\"env\": [ " + string.Join(", ", froms.Select((f, i) => $$"""{ "name": "X{{i}}", "from": "{{f}}" }""")) + " ]";

    private static DeploymentManifest? Parse(
        string workload, IReadOnlyList<IWorkloadExtensionValidator> validators,
        out IReadOnlyList<DeploymentValidationError> errors)
    {
        using var doc = JsonDocument.Parse($$"""{ "schemaVersion": 1, "workloads": [ {{workload}} ] }""");
        return DeploymentManifestParser.Parse(doc.RootElement, validators, out errors);
    }

    private static IReadOnlyList<DeploymentValidationError> Registered(string workload)
    {
        Parse(workload, ReeveValidators, out var errors);
        return errors;
    }

    [Theory]
    [InlineData("gatewayUrl")]
    [InlineData("backendBaseUrl")]
    [InlineData("instanceId")]
    [InlineData("workloadToken")]
    [InlineData("mqttBrokerUrl")]
    [InlineData("mqttUsername")]
    [InlineData("mqttPassword")]
    [InlineData("specHash")]
    public void Each_of_the_eight_keys_parses_clean_under_http_health(string key)
    {
        Registered(Workload(HttpHealth, Env($"bootstrap:{key}"))).Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_key_is_UnknownBootstrapKey_naming_the_set()
    {
        Registered(Workload(HttpHealth, Env("bootstrap:nope"))).Should().Equal(new DeploymentValidationError(
            "deployment.workloads[0].env[0].from",
            "bootstrap key 'nope' is not one the library or a registered extension validator declares. Declared keys: backendBaseUrl, gatewayUrl, instanceId, mqttBrokerUrl, mqttPassword, mqttUsername, specHash, workloadToken, workloadHost:<id>.",
            DeploymentValidationCodes.UnknownBootstrapKey));
    }

    [Theory]
    [InlineData("mqttBrokerUrl")]
    [InlineData("mqttUsername")]
    [InlineData("mqttPassword")]
    public void An_mqtt_key_under_adapter_session_is_MqttBootstrapForbiddenForAdapter(string key)
    {
        Registered(Workload(AdapterHealth, Env($"bootstrap:{key}"))).Should().Equal(new DeploymentValidationError(
            "deployment.workloads[0].env[0].from",
            $"bootstrap:{key} is not available to an adapter-session workload — it speaks to the gateway; only genuine-MQTT (container/http) workloads may take internal-broker credentials.",
            MqttBootstrapForbiddenForAdapter));
    }

    [Theory]
    [InlineData("gatewayUrl")]
    [InlineData("workloadToken")]
    [InlineData("backendBaseUrl")]
    [InlineData("instanceId")]
    [InlineData("specHash")]
    public void Every_other_key_parses_clean_under_adapter_session(string key)
    {
        Registered(Workload(AdapterHealth, Env($"bootstrap:{key}"))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("deployment-zwave.json")]
    [InlineData("deployment-matter.json")]
    [InlineData("deployment-bluetooth.json")]
    public void The_adapter_session_example_fixtures_parse_clean(string fixture)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture)));
        DeploymentManifestParser.Parse(doc.RootElement, ReeveValidators, out var errors);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void A_zero_heartbeat_is_InvalidHeartbeat()
    {
        Registered(Workload("\"health\": { \"mode\": \"adapter-session\", \"heartbeatSeconds\": 0 }"))
            .Should().Equal(new DeploymentValidationError(
                "deployment.workloads[0].health.heartbeatSeconds",
                "health.heartbeatSeconds must be > 0 but was 0.",
                DeploymentValidationCodes.InvalidHeartbeat));
    }

    [Fact]
    public void A_string_heartbeat_is_WrongType()
    {
        Registered(Workload("\"health\": { \"mode\": \"adapter-session\", \"heartbeatSeconds\": \"45\" }"))
            .Should().Equal(new DeploymentValidationError(
                "deployment.workloads[0].health.heartbeatSeconds",
                "Field 'heartbeatSeconds' must be an integer but was String.",
                DeploymentValidationCodes.WrongType));
    }

    [Fact]
    public void A_heartbeat_on_http_health_is_InvalidHealthShape()
    {
        Registered(Workload("\"health\": { \"mode\": \"http\", \"http\": { \"port\": 8080 }, \"heartbeatSeconds\": 45 }"))
            .Should().Equal(new DeploymentValidationError(
                "deployment.workloads[0].health.heartbeatSeconds",
                "health.mode 'http' does not use 'heartbeatSeconds' (only a host health mode takes it).",
                DeploymentValidationCodes.InvalidHealthShape));
    }

    [Fact]
    public void A_heartbeat_under_a_host_mode_is_carried()
    {
        var manifest = Parse(
            Workload("\"health\": { \"mode\": \"adapter-session\", \"heartbeatSeconds\": 45 }"),
            ReeveValidators, out var errors);

        errors.Should().BeEmpty();
        manifest!.Workloads[0].Health.HeartbeatSeconds.Should().Be(45);
    }

    [Theory]
    [InlineData("protocol", "{ \"version\": \"1\", \"minSupported\": \"1\" }")]
    [InlineData("apiSchema", "\"1\"")]
    public void A_top_level_host_field_is_EvictedManifestField(string field, string value)
    {
        Registered(Workload(HttpHealth, $"\"{field}\": {value}")).Should().Equal(new DeploymentValidationError(
            $"deployment.workloads[0].{field}",
            $"'{field}' is not a manifest field: it belongs in a host extension (extensions.<namespace>). Re-publish the plugin against the current schema.",
            DeploymentValidationCodes.EvictedManifestField));
    }

    [Fact]
    public void A_health_adapterType_is_EvictedManifestField_saying_it_was_removed()
    {
        Registered(Workload("\"health\": { \"mode\": \"adapter-session\", \"adapterType\": \"zwave\" }"))
            .Should().Equal(new DeploymentValidationError(
                "deployment.workloads[0].health.adapterType",
                "'adapterType' is not a manifest field: nothing reads it. Re-publish the plugin against the current schema.",
                DeploymentValidationCodes.EvictedManifestField));
    }

    [Fact]
    public void A_workload_missing_the_reeve_namespace_reports_the_namespace_alone()
    {
        Registered(Workload(AdapterHealth, Env("bootstrap:mqttBrokerUrl"), extensions: "")).Should().Equal(
            new DeploymentValidationError(
                "deployment.workloads[0].extensions.reeve",
                "Required extension namespace 'reeve' is missing. Every workload must declare it; a misspelled namespace reads as a foreign host's and would drop the contract silently.",
                DeploymentValidationCodes.MissingExtensionNamespace));
    }

    [Fact]
    public void A_workload_with_a_non_object_reeve_namespace_reports_the_shape_alone()
    {
        Registered(Workload(AdapterHealth, Env("bootstrap:mqttBrokerUrl"), extensions: "\"extensions\": { \"reeve\": 5 }"))
            .Should().Equal(new DeploymentValidationError(
                "deployment.workloads[0].extensions.reeve",
                "Extension namespace 'reeve' must be a JSON object but was Number.",
                DeploymentValidationCodes.InvalidExtensionShape));
    }

    [Fact]
    public void The_mqtt_rule_reports_before_the_library_reference_errors()
    {
        Registered(Workload(AdapterHealth, Env("bootstrap:nope", "bootstrap:mqttBrokerUrl"))).Should().Equal(
            new DeploymentValidationError(
                "deployment.workloads[0].env[1].from",
                "bootstrap:mqttBrokerUrl is not available to an adapter-session workload — it speaks to the gateway; only genuine-MQTT (container/http) workloads may take internal-broker credentials.",
                MqttBootstrapForbiddenForAdapter),
            new DeploymentValidationError(
                "deployment.workloads[0].env[0].from",
                "bootstrap key 'nope' is not one the library or a registered extension validator declares. Declared keys: backendBaseUrl, gatewayUrl, instanceId, mqttBrokerUrl, mqttPassword, mqttUsername, specHash, workloadToken, workloadHost:<id>.",
                DeploymentValidationCodes.UnknownBootstrapKey));
    }

    [Fact]
    public void The_mqtt_rule_indexes_env_as_the_parsed_list_does()
    {
        Registered(Workload(AdapterHealth, "\"env\": [ \"stray\", { \"name\": \"X\", \"from\": \"bootstrap:mqttBrokerUrl\" } ]"))
            .Should().Equal(
                new DeploymentValidationError(
                    "deployment.workloads[0].env[0].from",
                    "bootstrap:mqttBrokerUrl is not available to an adapter-session workload — it speaks to the gateway; only genuine-MQTT (container/http) workloads may take internal-broker credentials.",
                    MqttBootstrapForbiddenForAdapter),
                new DeploymentValidationError(
                    "deployment.workloads[0].env[0]",
                    "Each env entry must be a JSON object but was String.",
                    DeploymentValidationCodes.NotAnObject));
    }

    private static readonly IReadOnlyList<IWorkloadExtensionValidator> NoValidators = [];

    private static IReadOnlyList<IWorkloadExtensionValidator> Acme(params string[] keys) =>
        [new TestBootstrapValidator("acme", required: false, keys)];

    [Fact]
    public void A_key_declared_by_a_validator_parses()
    {
        Parse(Workload(HttpHealth, Env("bootstrap:brokerUrl"), extensions: "\"extensions\": { \"acme\": {} }"),
            Acme("brokerUrl"), out var errors);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void An_undeclared_key_is_UnknownBootstrapKey_with_no_validators()
    {
        Parse(Workload(HttpHealth, Env("bootstrap:gatewayUrl"), extensions: ""), NoValidators, out var errors);

        errors.Should().Equal(new DeploymentValidationError(
            "deployment.workloads[0].env[0].from",
            "bootstrap key 'gatewayUrl' is not one the library or a registered extension validator declares. Declared keys: instanceId, specHash, workloadHost:<id>.",
            DeploymentValidationCodes.UnknownBootstrapKey));
    }

    [Theory]
    [InlineData("instanceId")]
    [InlineData("specHash")]
    public void Library_keys_parse_with_no_validators(string key)
    {
        Parse(Workload(HttpHealth, Env($"bootstrap:{key}"), extensions: ""), NoValidators, out var errors);

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("instanceId")]
    [InlineData("specHash")]
    public void A_validator_declaring_a_library_key_is_a_programming_error(string key)
    {
        var act = () => Parse(Workload(HttpHealth, extensions: ""), Acme("brokerUrl", key), out _);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("'acme'").And.Contain($"'{key}'");
    }

    [Fact]
    public void Two_validators_may_declare_the_same_key()
    {
        IReadOnlyList<IWorkloadExtensionValidator> validators =
        [
            new TestBootstrapValidator("acme", required: false, "brokerUrl"),
            new TestBootstrapValidator("other", required: false, "brokerUrl"),
        ];

        Parse(Workload(HttpHealth, Env("bootstrap:brokerUrl"), extensions: ""), validators, out var errors);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void The_allowed_keys_are_the_union_of_every_validator_s()
    {
        IReadOnlyList<IWorkloadExtensionValidator> validators =
        [
            new TestBootstrapValidator("acme", required: false, "a"),
            new TestBootstrapValidator("other", required: false, "b"),
        ];

        Parse(Workload(HttpHealth, Env("bootstrap:a", "bootstrap:b"), extensions: ""), validators, out var errors);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void A_validator_returning_null_keys_is_a_programming_error()
    {
        var act = () => Parse(Workload(HttpHealth, extensions: ""), [new NullKeysValidator()], out _);

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("'acme'");
    }

    [Fact]
    public void A_null_validator_is_a_programming_error_naming_its_index()
    {
        var act = () => Parse(Workload(HttpHealth, extensions: ""), [Acme("a")[0], null!], out _);

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("[1]");
    }

    private sealed class NullKeysValidator : IWorkloadExtensionValidator
    {
        public string Namespace => "acme";

        public bool Required => false;

        public IReadOnlySet<string> BootstrapKeys => null!;

        public void Validate(
            JsonElement workload, JsonElement extension, string path, ICollection<DeploymentValidationError> errors)
        {
        }
    }

    private sealed class ReeveRules : IWorkloadExtensionValidator
    {
        private static readonly HashSet<string> BrokerKeys =
            new(StringComparer.Ordinal) { "mqttBrokerUrl", "mqttUsername", "mqttPassword" };

        public string Namespace => "reeve";

        public bool Required => true;

        public IReadOnlySet<string> BootstrapKeys { get; } =
            new HashSet<string>(TestBootstrapValidator.ReeveHostKeys, StringComparer.Ordinal);

        public void Validate(
            JsonElement workload, JsonElement extension, string path, ICollection<DeploymentValidationError> errors)
        {
            if (!workload.TryGetProperty("health", out var health)
                || health.ValueKind != JsonValueKind.Object
                || !health.TryGetProperty("mode", out var mode)
                || mode.ValueKind != JsonValueKind.String
                || mode.GetString() != "adapter-session"
                || !workload.TryGetProperty("env", out var env)
                || env.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var workloadPath = path[..^".extensions.reeve".Length];
            var index = 0;
            foreach (var entry in env.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (entry.TryGetProperty("from", out var from)
                    && from.ValueKind == JsonValueKind.String
                    && from.GetString() is { } source
                    && source.StartsWith("bootstrap:", StringComparison.Ordinal)
                    && BrokerKeys.Contains(source["bootstrap:".Length..]))
                {
                    errors.Add(new(
                        $"{workloadPath}.env[{index}].from",
                        $"{source} is not available to an adapter-session workload — it speaks to the gateway; only genuine-MQTT (container/http) workloads may take internal-broker credentials.",
                        MqttBootstrapForbiddenForAdapter));
                }

                index++;
            }
        }
    }

    [Fact]
    public void A_declared_key_counts_without_the_namespace()
    {
        Parse(Workload(HttpHealth, Env("bootstrap:brokerUrl"), extensions: ""), Acme("brokerUrl"), out var errors);

        errors.Should().BeEmpty("a key belongs to the host that resolves it, not to the workloads that address it");
    }

    [Fact]
    public void The_unknown_key_message_lists_the_declared_keys_in_ordinal_order()
    {
        Parse(Workload(HttpHealth, Env("bootstrap:nope"), extensions: ""), Acme("zeta", "alpha"), out var errors);

        errors.Should().Equal(new DeploymentValidationError(
            "deployment.workloads[0].env[0].from",
            "bootstrap key 'nope' is not one the library or a registered extension validator declares. Declared keys: alpha, instanceId, specHash, zeta, workloadHost:<id>.",
            DeploymentValidationCodes.UnknownBootstrapKey));
    }
}
