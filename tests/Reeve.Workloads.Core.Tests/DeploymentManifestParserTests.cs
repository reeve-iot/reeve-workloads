using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core.Tests.Manifest;
using Reeve.Workloads.Manifest;

namespace Reeve.Workloads.Core.Tests;

public sealed class DeploymentManifestParserTests
{
    private static readonly IReadOnlyList<IWorkloadExtensionValidator> NoValidators = [];

    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static JsonElement ParseElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string ReadFixture(string name) => File.ReadAllText(Path.Combine(FixtureDir, name));

    [Fact]
    public void FullyPopulated_model_round_trips_through_json_and_parser()
    {
        var patchBody = ParseElement("""[{"op":"add","path":"/spec/hostPID","value":true}]""");
        var original = new DeploymentManifest
        {
            SchemaVersion = 1,
            Workloads = new[]
            {
                new WorkloadManifest
                {
                    Id = "everything",
                    DisplayName = "Every field populated",
                    Description = "What this workload is for, in the author's own words.",
                    Image = new WorkloadImage
                    {
                        Ref = "registry.example.com/reeve/everything",
                        Digest = "sha256:abc0000000000000000000000000000000000000000000000000000000000000",
                        Tag = "9.9.9",
                        Platforms = new[] { "linux/amd64", "linux/arm64" },
                    },
                    Kind = WorkloadKind.SingleNode,
                    Resources = new WorkloadResources
                    {
                        Requests = new ResourceQuantities { Cpu = "100m", Memory = "256Mi" },
                        Limits = new ResourceQuantities { Cpu = "500m", Memory = "512Mi" },
                    },
                    Network = new WorkloadNetwork
                    {
                        HostNetwork = true,
                        Mdns = true,
                        Ipv6Required = true,
                        Ports = new[] { new WorkloadPort { Name = "api", Port = 8080, Protocol = "TCP" } },
                        Attachments = new[]
                        {
                            new WorkloadNetworkAttachmentSlot { Parameter = "lanAttachment", Description = "camera VLAN" },
                        },
                    },
                    Privileges = new WorkloadPrivileges
                    {
                        Capabilities = new[] { WorkloadCapability.NetAdmin, WorkloadCapability.SysAdmin },
                        Privileged = true,
                        RunAsRoot = true,
                        FsGroup = 1000,
                        HostDbus = true,
                        TunDevice = true,
                        Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } },
                        NodeLocalState = new NodeLocalState
                        {
                            Subdir = "state",
                            Criticality = VolumeCriticality.Critical,
                            Description = "per-node bindkeys",
                        },
                    },
                    Volumes = new[]
                    {
                        new WorkloadVolume
                        {
                            Id = "data",
                            MountPath = "/data",
                            Size = "64Mi",
                            Criticality = VolumeCriticality.Recoverable,
                            Description = "cache",
                            AccessMode = "rwo",
                        },
                    },
                    ConfigMapMounts = new[]
                    {
                        new ConfigMapMount { MountPath = "/etc/matter/paa", Parameter = "paaConfigMapName" },
                    },
                    Health = new WorkloadHealth
                    {
                        Mode = new WorkloadHealthMode("adapter-session"),
                        HeartbeatSeconds = 45,
                        Http = new WorkloadHttpProbe { Port = 8081, LivenessPath = "/live", ReadinessPath = "/ready" },
                    },
                    HostRequirements = new[]
                    {
                        new HostRequirement { Kind = HostRequirementKind.DbusService, Name = "bluetoothd", Description = "BlueZ" },
                        new HostRequirement { Kind = HostRequirementKind.Multicast, Description = "mDNS" },
                    },
                    Observability = new WorkloadObservability
                    {
                        Metrics = new WorkloadMetrics { Port = 9090, Path = "/metrics" },
                    },
                    Env = new[]
                    {
                        new WorkloadEnv { Name = "REEVE_SPEC_HASH", From = "bootstrap:specHash" },
                        new WorkloadEnv { Name = "TOKEN", From = "parameter:token" },
                        new WorkloadEnv { Name = "LITERAL", From = "literal:hello" },
                    },
                    Parameters = new[]
                    {
                        new WorkloadParameter
                        {
                            Name = "token",
                            Type = WorkloadParameterType.Password,
                            DisplayName = "API token",
                            Description = "secret",
                            Required = true,
                            Hint = "paste it",
                            Section = "Credentials",
                        },
                        new WorkloadParameter
                        {
                            Name = "mode",
                            Type = WorkloadParameterType.Enum,
                            Required = false,
                            Default = "auto",
                            EnumValues = new[] { "auto", "manual" },
                            Widget = WorkloadParameterWidget.Radio,
                            EnumLabels = new Dictionary<string, string> { ["auto"] = "Automatic", ["manual"] = "Manual" },
                            Reveals = new Dictionary<string, IReadOnlyList<string>> { ["manual"] = new[] { "Manual address" } },
                        },
                        new WorkloadParameter
                        {
                            Name = "manualAddress",
                            Type = WorkloadParameterType.Text,
                            Section = "Manual address",
                        },
                        new WorkloadParameter
                        {
                            Name = "serialPort",
                            Type = WorkloadParameterType.DevicePath,
                            Required = true,
                            Default = "/dev/ttyUSB0",
                        },
                        new WorkloadParameter
                        {
                            Name = "paaConfigMapName",
                            Type = WorkloadParameterType.Text,
                            Required = false,
                        },
                        new WorkloadParameter
                        {
                            Name = "lanAttachment",
                            Type = WorkloadParameterType.NetworkAttachment,
                            Required = false,
                        },
                    },
                    UnsupportedPodPatch = new UnsupportedPodPatch
                    {
                        RawJson = patchBody.GetRawText(),
                        Body = patchBody,
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(original, SerializerOptions);

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty("a fully-valid model must round-trip with no structural errors");
        parsed.Should().NotBeNull();

        parsed!.Should().BeEquivalentTo(original, opts => opts
            .Excluding(m => m.Path.EndsWith("Body"))
            .Excluding(m => m.Path.EndsWith("RawJson")));

        var originalPatch = original.Workloads[0].UnsupportedPodPatch!;
        var parsedPatch = parsed.Workloads[0].UnsupportedPodPatch!;
        parsedPatch.Should().NotBeNull();
        NormalizeJson(parsedPatch.RawJson).Should().Be(NormalizeJson(originalPatch.RawJson));
        parsedPatch.Body.ValueKind.Should().Be(JsonValueKind.Array);
        parsedPatch.Body.GetArrayLength().Should().Be(1);
        parsedPatch.Body[0].GetProperty("path").GetString().Should().Be("/spec/hostPID");
    }

    [Fact]
    public void Product_image_component_model_round_trips_through_json_and_parser()
    {
        var original = new DeploymentManifest
        {
            SchemaVersion = 1,
            Workloads = new[]
            {
                new WorkloadManifest
                {
                    Id = "vd",
                    DisplayName = "Virtual Devices",
                    Image = new WorkloadImage { Component = "virtual-devices" },
                    Kind = WorkloadKind.Service,
                    Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
                },
            },
        };

        var json = JsonSerializer.Serialize(original, SerializerOptions);
        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty("a component-image model must round-trip with no structural errors");
        parsed.Should().NotBeNull();
        parsed!.Workloads[0].Image.Component.Should().Be("virtual-devices");
        parsed.Workloads[0].Image.Ref.Should().BeEmpty();
        parsed.Workloads[0].Image.Digest.Should().BeEmpty();
    }

    private static string NormalizeJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    [Fact]
    public void Matter_example_parses_to_expected_shape()
    {
        var parsed = DeploymentManifestParser.Parse(ParseElement(ReadFixture("deployment-matter.json")), TestBootstrapValidator.ReeveKeys, out var errors);

        errors.Should().BeEmpty();
        parsed.Should().NotBeNull();
        parsed!.SchemaVersion.Should().Be(1);
        var wl = parsed.Workloads.Should().ContainSingle().Subject;
        wl.Id.Should().Be("matter-adapter");
        wl.Kind.Should().Be(WorkloadKind.Service);
        wl.Image.Digest.Should().StartWith("sha256:");
        wl.Image.Platforms.Should().Contain(new[] { "linux/amd64", "linux/arm64" });
        wl.Resources!.Requests!.Cpu.Should().Be("100m");
        wl.Resources.Limits!.Memory.Should().Be("512Mi");
        wl.Network!.HostNetwork.Should().BeTrue();
        wl.Network.Mdns.Should().BeTrue();
        wl.Health.Mode.Should().Be(new WorkloadHealthMode("adapter-session"));
        wl.Health.HeartbeatSeconds.Should().Be(30);
        wl.Extensions.For("reeve").Should().NotBeNull();
        var reeve = wl.Extensions.For("reeve")!.Value;
        reeve.GetProperty("protocol").GetProperty("version").GetString().Should().Be("1");
        reeve.GetProperty("protocol").GetProperty("minSupported").GetString().Should().Be("1");
        wl.HostRequirements.Select(h => h.Kind).Should()
            .BeEquivalentTo(new[] { HostRequirementKind.UdevSerial, HostRequirementKind.Multicast });
        wl.Env.Should().Contain(e => e.Name == "REEVE_SPEC_HASH" && e.From == "bootstrap:specHash");
        wl.Privileges.Should().BeNull();
        wl.Volumes.Should().BeEmpty();
    }

    [Fact]
    public void Bluetooth_example_is_single_node_with_critical_volume_and_caps()
    {
        var parsed = DeploymentManifestParser.Parse(ParseElement(ReadFixture("deployment-bluetooth.json")), TestBootstrapValidator.ReeveKeys, out var errors);

        errors.Should().BeEmpty();
        var wl = parsed!.Workloads.Should().ContainSingle().Subject;
        wl.Kind.Should().Be(WorkloadKind.SingleNode);
        wl.Privileges!.Capabilities.Should()
            .BeEquivalentTo(new[] { WorkloadCapability.NetAdmin, WorkloadCapability.NetRaw });
        wl.Privileges.HostDbus.Should().BeTrue();
        wl.Network!.HostNetwork.Should().BeTrue();
        var vol = wl.Volumes.Should().ContainSingle().Subject;
        vol.Id.Should().Be("bindkeys");
        vol.Criticality.Should().Be(VolumeCriticality.Critical);
        vol.MountPath.Should().Be("/data");
        vol.AccessMode.Should().Be("rwo");
        wl.HostRequirements.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Kind = HostRequirementKind.DbusService, Name = "bluetoothd" },
                opts => opts.ExcludingMissingMembers());
        wl.Parameters.Should().Contain(p => p.Name == "nodeSelector" && p.Type == WorkloadParameterType.NodeSelector);
        wl.Parameters.Single(p => p.Name == "minRssiDbm").Should().BeEquivalentTo(
            new { Type = WorkloadParameterType.Integer, Default = "-90" }, opts => opts.ExcludingMissingMembers());
    }

    [Fact]
    public void Zwave_example_has_device_param_and_recoverable_cache_volume()
    {
        var parsed = DeploymentManifestParser.Parse(ParseElement(ReadFixture("deployment-zwave.json")), TestBootstrapValidator.ReeveKeys, out var errors);

        errors.Should().BeEmpty();
        var wl = parsed!.Workloads.Should().ContainSingle().Subject;
        wl.Kind.Should().Be(WorkloadKind.SingleNode);
        wl.Privileges!.RunAsRoot.Should().BeTrue();
        wl.Privileges.Devices.Should().ContainSingle().Which.Parameter.Should().Be("serialPort");
        var vol = wl.Volumes.Should().ContainSingle().Subject;
        vol.Criticality.Should().Be(VolumeCriticality.Recoverable);
        vol.Size.Should().Be("512Mi");
        wl.HostRequirements.Should().ContainSingle().Which.Kind.Should().Be(HostRequirementKind.UdevSerial);
        wl.Parameters.Single(p => p.Name == "serialPort").Type.Should().Be(WorkloadParameterType.DevicePath);
        wl.Parameters.Count(p => p.Type == WorkloadParameterType.Password).Should().Be(2);
    }

    [Fact]
    public void HttpHealth_example_uses_http_health_and_metrics()
    {
        var parsed = DeploymentManifestParser.Parse(ParseElement(ReadFixture("deployment-http-health-example.json")), TestBootstrapValidator.ReeveKeys, out var errors);

        errors.Should().BeEmpty();
        var wl = parsed!.Workloads.Should().ContainSingle().Subject;
        wl.Kind.Should().Be(WorkloadKind.Service);
        wl.Health.Mode.Should().Be(WorkloadHealthMode.Http);
        wl.Health.Http.Should().NotBeNull();
        wl.Health.Http!.Port.Should().Be(8080);
        wl.Health.Http.LivenessPath.Should().Be("/healthz");
        wl.Health.Http.ReadinessPath.Should().Be("/readyz");
        wl.Health.HeartbeatSeconds.Should().Be(0);
        wl.Observability!.Metrics!.Port.Should().Be(9090);
        wl.Network!.Ports.Should().ContainSingle().Which.Port.Should().Be(5540);
    }

    [Fact]
    public void Non_object_deployment_yields_null_and_a_single_error()
    {
        var parsed = DeploymentManifestParser.Parse(ParseElement("\"nope\""), NoValidators, out var errors);

        parsed.Should().BeNull();
        errors.Should().ContainSingle().Which.Code.Should().Be(DeploymentValidationCodes.NotAnObject);
    }

    [Fact]
    public void Wrong_schema_version_is_reported_not_thrown()
    {
        const string json = """{ "schemaVersion": 2, "workloads": [] }""";

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        parsed.Should().NotBeNull("the parser recovers so it can collect further errors");
        errors.Should().Contain(e => e.Code == DeploymentValidationCodes.UnsupportedSchemaVersion);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("env")]
    public void A_value_with_a_trailing_newline_is_rejected(string field)
    {
        var imageRef = field == "image" ? "registry.example/image\n" : "registry.example/image";
        var envName = field == "env" ? "FOO\n" : "FOO";
        var json = $$"""
        { "schemaVersion": 1, "workloads": [{
          "id": "sample", "displayName": "Sample",
          "image": { "ref": {{JsonSerializer.Serialize(imageRef)}}, "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
          "kind": "service", "health": { "mode": "container" },
          "env": [{ "name": {{JsonSerializer.Serialize(envName)}}, "from": "parameter" }]
        }] }
        """;

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().Contain(e => e.Code == DeploymentValidationCodes.ControlCharacterInString
            && e.Path.EndsWith(field == "image" ? ".image.ref" : ".env[0].name", StringComparison.Ordinal));
    }

    [Fact]
    public void An_image_platform_with_a_trailing_newline_is_rejected_by_control_character_validation()
    {
        const string json = """{ "schemaVersion": 1, "workloads": [{ "id": "sample", "displayName": "Sample", "image": { "ref": "registry.example/image", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000", "platforms": ["linux/amd64\n"] }, "kind": "service", "health": { "mode": "container" } }] }""";

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().Contain(e => e.Code == DeploymentValidationCodes.ControlCharacterInString
            && e.Path == "deployment.workloads[0].image.platforms[0]");
    }

    [Fact]
    public void Missing_required_workload_fields_are_all_collected()
    {
        const string json = """{ "schemaVersion": 1, "workloads": [ { } ] }""";

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        parsed.Should().NotBeNull();
        errors.Should().NotBeEmpty();
        errors.Select(e => e.Path).Should().Contain(new[]
        {
            "deployment.workloads[0].id",
            "deployment.workloads[0].displayName",
            "deployment.workloads[0].image",
            "deployment.workloads[0].kind",
            "deployment.workloads[0].health",
        });
        errors.Count.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData("/dev/serial", false)]
    [InlineData("/dev/serial/by-id", false)]
    [InlineData("/sys/bus/usb", false)]
    [InlineData("/dev/mem", true)]
    [InlineData("/dev", true)]
    [InlineData("/etc/passwd", true)]
    public void HostPath_parameter_default_allows_dev_serial_but_still_rejects_other_dev_paths(string defaultValue, bool expectRejected)
    {
        var json = $$"""
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "scan",
              "displayName": "Scan",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "scan-per-node",
              "privileges": { "hostPaths": [ { "parameter": "serialDir", "mountPath": "/dev/serial" } ] },
              "health": { "mode": "container" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } },
              "parameters": [ { "name": "serialDir", "type": "hostPath", "default": "{{defaultValue}}" } ]
            }
          ]
        }
        """;

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        var hasPrefixError = errors.Any(e =>
            e.Code == DeploymentValidationCodes.DisallowedPathPrefix &&
            e.Path.Contains("parameters"));
        hasPrefixError.Should().Be(expectRejected,
            expectRejected
                ? $"'{defaultValue}' is not a permitted hostPath value"
                : $"'{defaultValue}' is a permitted RO host-inspect mount");
    }

    private static string FsGroupManifest(string fsGroupJson, bool withVolume = true) => $$"""
    {
      "schemaVersion": 1,
      "workloads": [
        {
          "id": "w",
          "displayName": "W",
          "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
          "kind": "single-node",
          "privileges": { "fsGroup": {{fsGroupJson}} },
          {{(withVolume ? "\"volumes\": [ { \"id\": \"v\", \"mountPath\": \"/v\", \"size\": \"1Gi\", \"criticality\": \"cache\" } ]," : "")}}
          "health": { "mode": "container" },
          "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } }
        }
      ]
    }
    """;

    [Theory]
    [InlineData("1")]
    [InlineData("1000")]
    [InlineData("65534")]
    public void A_volume_group_in_range_is_read_for_a_workload_that_has_volumes(string gid)
    {
        var parsed = DeploymentManifestParser.Parse(ParseElement(FsGroupManifest(gid)), NoValidators, out var errors);

        errors.Should().BeEmpty();
        parsed!.Workloads.Single().Privileges!.FsGroup.Should().Be(int.Parse(gid, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Without_a_volume_group_the_field_is_null_so_nothing_changes_for_existing_manifests()
    {
        var parsed = DeploymentManifestParser.Parse(ParseElement(FsGroupManifest("null")), NoValidators, out var errors);

        errors.Should().BeEmpty();
        parsed!.Workloads.Single().Privileges!.FsGroup.Should().BeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("65535")]
    [InlineData("4294967296")]
    public void A_volume_group_outside_1_to_65534_is_refused_and_never_reaches_a_renderer(string gid)
    {
        var parsed = DeploymentManifestParser.Parse(ParseElement(FsGroupManifest(gid)), NoValidators, out var errors);

        errors.Should().ContainSingle(e => e.Path == "deployment.workloads[0].privileges.fsGroup")
            .Which.Code.Should().BeOneOf(DeploymentValidationCodes.InvalidFsGroup, DeploymentValidationCodes.WrongType);
        parsed!.Workloads.Single().Privileges!.FsGroup.Should().BeNull();
    }

    [Theory]
    [InlineData("\"1000\"")]
    [InlineData("10.5")]
    [InlineData("true")]
    public void A_volume_group_that_is_not_an_integer_is_a_type_error(string json)
    {
        DeploymentManifestParser.Parse(ParseElement(FsGroupManifest(json)), NoValidators, out var errors);

        errors.Should().ContainSingle(e => e.Path == "deployment.workloads[0].privileges.fsGroup")
            .Which.Code.Should().Be(DeploymentValidationCodes.WrongType);
    }

    [Fact]
    public void A_volume_group_with_no_volume_to_own_is_refused()
    {
        DeploymentManifestParser.Parse(ParseElement(FsGroupManifest("1000", withVolume: false)), NoValidators, out var errors);

        errors.Should().ContainSingle().Which.Code.Should().Be(DeploymentValidationCodes.FsGroupWithoutVolumes);
    }

    [Fact]
    public void Unknown_enum_values_are_reported_with_stable_code()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "w",
              "displayName": "W",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "cluster-wide",
              "privileges": { "capabilities": ["CAP_SYS_TIME"] },
              "volumes": [ { "id": "v", "mountPath": "/v", "size": "1Gi", "criticality": "priceless" } ],
              "health": { "mode": "telepathy" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } },
              "hostRequirements": [ { "kind": "quantum-entanglement" } ],
              "parameters": [ { "name": "p", "type": "hologram" } ]
            }
          ]
        }
        """;

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        parsed.Should().NotBeNull();
        errors.Where(e => e.Code == DeploymentValidationCodes.UnknownEnumValue)
            .Select(e => e.Path)
            .Should().Contain(new[]
            {
                "deployment.workloads[0].kind",
                "deployment.workloads[0].privileges.capabilities[0]",
                "deployment.workloads[0].volumes[0].criticality",
                    "deployment.workloads[0].hostRequirements[0].kind",
                "deployment.workloads[0].parameters[0].type",
            });
    }

    [Fact]
    public void Scan_per_node_kind_parses_and_forbids_volumes()
    {
        const string validJson = """
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "scan",
              "displayName": "Scan",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "scan-per-node",
              "health": { "mode": "container" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } }
            }
          ]
        }
        """;

        var parsed = DeploymentManifestParser.Parse(ParseElement(validJson), NoValidators, out var errors);

        parsed.Should().NotBeNull();
        parsed!.Workloads.Should().ContainSingle()
            .Which.Kind.Should().Be(WorkloadKind.ScanPerNode);
        errors.Where(e => e.Code == DeploymentValidationCodes.UnknownEnumValue)
            .Select(e => e.Path)
            .Should().NotContain("deployment.workloads[0].kind");

        const string withVolumes = """
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "scan",
              "displayName": "Scan",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "scan-per-node",
              "volumes": [ { "id": "data", "mountPath": "/data", "size": "64Mi", "criticality": "critical" } ],
              "health": { "mode": "container" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } }
            }
          ]
        }
        """;

        DeploymentManifestParser.Parse(ParseElement(withVolumes), NoValidators, out var volErrors);

        volErrors.Where(e => e.Code == DeploymentValidationCodes.PerNodeVolumesForbidden)
            .Select(e => e.Path)
            .Should().Contain("deployment.workloads[0].volumes");
    }

    [Fact]
    public void A_manifest_still_carrying_an_evicted_field_is_refused_with_the_migration()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "w", "displayName": "W",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "service",
              "health": { "mode": "adapter-session", "adapterType": "matter" },
              "protocol": { "version": "1", "minSupported": "1" },
              "apiSchema": "1",
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } }
            }
          ]
        }
        """;

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Where(e => e.Code == DeploymentValidationCodes.EvictedManifestField).Select(e => e.Path)
            .Should().BeEquivalentTo(
            [
                "deployment.workloads[0].protocol",
                "deployment.workloads[0].apiSchema",
                "deployment.workloads[0].health.adapterType",
            ]);
    }

    [Fact]
    public void Http_health_without_probe_is_an_invalid_health_shape()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "w", "displayName": "W",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "service",
              "health": { "mode": "http" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } }
            }
          ]
        }
        """;

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().Contain(e =>
            e.Code == DeploymentValidationCodes.InvalidHealthShape &&
            e.Path == "deployment.workloads[0].health.http");
    }

    [Fact]
    public void AdapterSession_heartbeat_defaults_to_30_when_omitted()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "w", "displayName": "W",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "service",
              "health": { "mode": "adapter-session" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } }
            }
          ]
        }
        """;

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty();
        parsed!.Workloads[0].Health.HeartbeatSeconds.Should().Be(30);
    }

    [Fact]
    public void Unsupported_pod_patch_body_is_preserved_verbatim()
    {
        const string patch = """[{"op":"add","path":"/spec/hostPID","value":true},{"op":"replace","path":"/spec/serviceAccountName","value":"privileged"}]""";
        var json = $$"""
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "w", "displayName": "W",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "service",
              "health": { "mode": "container" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } },
              "unsupportedPodPatch": {{patch}}
            }
          ]
        }
        """;

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty();
        var stored = parsed!.Workloads[0].UnsupportedPodPatch!;
        stored.Should().NotBeNull();
        NormalizeJson(stored.RawJson).Should().Be(NormalizeJson(patch));
        stored.Body.GetArrayLength().Should().Be(2);
    }

    private static string NetworkAttachmentManifest(string attachmentsJson, string parametersJson) => $$"""
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "attach",
              "displayName": "Attach",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "service",
              "network": { "attachments": {{attachmentsJson}} },
              "health": { "mode": "container" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } },
              "parameters": {{parametersJson}}
            }
          ]
        }
        """;

    [Fact]
    public void A_network_attachments_slot_bound_to_a_non_networkAttachment_parameter_is_rejected()
    {
        var json = NetworkAttachmentManifest(
            """[ { "parameter": "netAttach" } ]""",
            """[ { "name": "netAttach", "type": "text" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        var typeError = errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.NetworkAttachmentParameterType)
            .Which;
        typeError.Path.Should().Be("deployment.workloads[0].network.attachments[0].parameter");
        typeError.Message.Should().Contain("netAttach").And.Contain("text");

        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.UnknownKey);
        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.UndeclaredParameterReference);
    }

    [Fact]
    public void A_network_attachments_slot_naming_an_undeclared_parameter_is_rejected()
    {
        var json = NetworkAttachmentManifest(
            """[ { "parameter": "ghost" } ]""",
            """[ { "name": "somethingElse", "type": "text" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        var missing = errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.NetworkAttachmentParameterMissing)
            .Which;
        missing.Path.Should().Be("deployment.workloads[0].network.attachments[0].parameter");
        missing.Message.Should().Contain("ghost");

        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.UndeclaredParameterReference);
        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.UnknownKey);
    }

    [Fact]
    public void Two_network_attachments_slots_naming_the_same_parameter_are_rejected()
    {
        var json = NetworkAttachmentManifest(
            """[ { "parameter": "netAttach" }, { "parameter": "netAttach" } ]""",
            """[ { "name": "netAttach", "type": "networkAttachment" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        var duplicate = errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.NetworkAttachmentDuplicateParameter)
            .Which;
        duplicate.Message.Should().Contain("netAttach");

        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.NetworkAttachmentParameterType);
        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.NetworkAttachmentParameterMissing);
    }

    [Fact]
    public void An_unknown_key_inside_a_network_attachments_slot_is_rejected()
    {
        var json = NetworkAttachmentManifest(
            """[ { "parameter": "netAttach", "descriptionn": "typo" } ]""",
            """[ { "name": "netAttach", "type": "networkAttachment" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.UnknownKey)
            .Which.Path.Should().Be("deployment.workloads[0].network.attachments[0].descriptionn");
    }

    [Fact]
    public void A_valid_network_attachments_slot_parses_into_the_typed_model()
    {
        var json = NetworkAttachmentManifest(
            """[ { "parameter": "netAttach", "description": "the camera VLAN" } ]""",
            """[ { "name": "netAttach", "type": "networkAttachment" } ]""");

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty("a slot bound to a declared networkAttachment parameter is valid");
        var slot = parsed!.Workloads[0].Network!.Attachments.Should().ContainSingle().Which;
        slot.Parameter.Should().Be("netAttach");
        slot.Description.Should().Be("the camera VLAN");
    }

    [Fact]
    public void A_network_object_declaring_no_attachments_parses_to_an_empty_non_null_list()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "legacy",
              "displayName": "Legacy",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "service",
              "network": { "hostNetwork": true, "mdns": true, "ipv6Required": false, "ports": [ { "name": "api", "port": 8080, "protocol": "TCP" } ] },
              "health": { "mode": "container" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } }
            }
          ]
        }
        """;

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty();
        var network = parsed!.Workloads[0].Network!;
        network.Attachments.Should().NotBeNull().And.BeEmpty();
        network.HostNetwork.Should().BeTrue();
        network.Ports.Should().ContainSingle();
    }

    [Theory]
    [InlineData("deployment-matter.json")]
    [InlineData("deployment-bluetooth.json")]
    [InlineData("deployment-zwave.json")]
    [InlineData("deployment-http-health-example.json")]
    public void Example_fixtures_still_parse_with_no_errors_and_no_attachments(string fixture)
    {
        var parsed = DeploymentManifestParser.Parse(ParseElement(ReadFixture(fixture)), TestBootstrapValidator.ReeveKeys, out var errors);

        errors.Should().BeEmpty($"'{fixture}' must be unaffected by the additive attachment slot");
        foreach (var wl in parsed!.Workloads)
            wl.Network?.Attachments.Should().BeEmpty();
    }

    private static string NetworkModeManifest(string? networkJson, string parametersJson)
    {
        var networkLine = networkJson is null ? string.Empty : $"\"network\": {networkJson},";
        return $$"""
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "netmode",
              "displayName": "Net Mode",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "service",
              {{networkLine}}
              "health": { "mode": "container" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } },
              "parameters": {{parametersJson}}
            }
          ]
        }
        """;
    }

    private const string BothReservedNetworkParametersRequired = """
        [
          { "name": "network.mode", "type": "enum", "enumValues": ["cluster","hostNetwork","operatorNetwork"], "required": true },
          { "name": "network.attachments", "type": "networkAttachment", "required": true }
        ]
        """;

    private const string BothReservedNetworkParametersDefaultAndHintOnly = """
        [
          { "name": "network.mode", "type": "enum", "enumValues": ["cluster","hostNetwork","operatorNetwork"], "default": "hostNetwork", "hint": "pick one" },
          { "name": "network.attachments", "type": "networkAttachment", "hint": "the camera VLAN" }
        ]
        """;

    private static void AssertBothNetworkNamesAreReserved(IReadOnlyList<DeploymentValidationError> errors)
    {
        var overrides = errors
            .Where(e => e.Code == DeploymentValidationCodes.ReservedParameterOverride)
            .ToList();

        overrides.Should().HaveCount(2,
            "both network.mode and network.attachments are synthesized TOGETHER under the one "
            + "manifest-derivable condition, so a required:true re-declaration of each violates Rule 16");
        overrides.Should()
            .ContainSingle(e => e.Path == "deployment.workloads[0].parameters[0]")
            .Which.Message.Should().Contain("network.mode");
        overrides.Should()
            .ContainSingle(e => e.Path == "deployment.workloads[0].parameters[1]")
            .Which.Message.Should().Contain("network.attachments");
    }

    [Fact]
    public void A_hostNetwork_workload_reserves_the_two_network_parameter_names()
    {
        var json = NetworkModeManifest("""{ "hostNetwork": true }""", BothReservedNetworkParametersRequired);

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        AssertBothNetworkNamesAreReserved(errors);
    }

    [Fact]
    public void A_slot_declaring_workload_reserves_the_two_network_parameter_names()
    {
        var json = NetworkModeManifest(
            """{ "attachments": [ { "parameter": "netAttach" } ] }""",
            """
            [
              { "name": "network.mode", "type": "enum", "enumValues": ["cluster","hostNetwork","operatorNetwork"], "required": true },
              { "name": "network.attachments", "type": "networkAttachment", "required": true },
              { "name": "netAttach", "type": "networkAttachment" }
            ]
            """);

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        AssertBothNetworkNamesAreReserved(errors);
        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.NetworkAttachmentParameterType);
        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.NetworkAttachmentParameterMissing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{ "hostNetwork": false, "mdns": true, "ipv6Required": false, "ports": [ { "name": "api", "port": 8080, "protocol": "TCP" } ] }""")]
    public void A_plain_workload_reserves_neither_network_name(string? networkJson)
    {
        var json = NetworkModeManifest(networkJson, BothReservedNetworkParametersRequired);

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.ReservedParameterOverride,
            "neither name may be reserved for a workload that declares neither hostNetwork nor a slot — "
            + "here they are ordinary author-declared parameters and required:true is entirely legal");
        errors.Should().BeEmpty("the manifest is otherwise well-formed");
    }

    [Fact]
    public void A_reserved_network_mode_re_declared_as_text_is_rejected()
    {
        var json = NetworkModeManifest(
            """{ "hostNetwork": true }""",
            """[ { "name": "network.mode", "type": "text" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        var err = errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.ReservedParameterOverride)
            .Which;
        err.Path.Should().Be("deployment.workloads[0].parameters[0]");
        err.Message.Should().Contain("network.mode");
    }

    [Fact]
    public void A_reserved_network_attachments_re_declared_as_text_is_rejected()
    {
        var json = NetworkModeManifest(
            """{ "hostNetwork": true }""",
            """[ { "name": "network.attachments", "type": "text" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        var err = errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.ReservedParameterOverride)
            .Which;
        err.Path.Should().Be("deployment.workloads[0].parameters[0]");
        err.Message.Should().Contain("network.attachments");
    }

    [Fact]
    public void A_reserved_network_parameter_re_declaration_overriding_only_default_and_hint_is_accepted()
    {
        var json = NetworkModeManifest(
            """{ "hostNetwork": true }""",
            BothReservedNetworkParametersDefaultAndHintOnly);

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty("overriding only default/hint on a reserved name is expressly legal");
    }

    [Fact]
    public void A_reserved_network_parameter_re_declared_as_required_is_rejected()
    {
        var json = NetworkModeManifest(
            """{ "hostNetwork": true }""",
            """
            [
              { "name": "network.mode", "type": "enum", "enumValues": ["cluster","hostNetwork","operatorNetwork"], "default": "hostNetwork", "hint": "pick one", "required": true }
            ]
            """);

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        var err = errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.ReservedParameterOverride)
            .Which;
        err.Path.Should().Be("deployment.workloads[0].parameters[0]");
        err.Message.Should().Contain("network.mode");
    }

    [Theory]
    [InlineData("netAttach", "iot-macvlan", false)]
    [InlineData("netAttach", "", true)]
    [InlineData("network.attachments", "iot-macvlan", false)]
    public void A_networkAttachment_default_is_refused_because_the_operator_names_the_network(
        string parameter, string declaredDefault, bool expectAccepted)
    {
        var json = NetworkModeManifest(
            """{ "hostNetwork": true }""",
            $$"""[ { "name": "{{parameter}}", "type": "networkAttachment", "default": "{{declaredDefault}}" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        if (expectAccepted)
        {
            errors.Should().BeEmpty("a blank default names no network");
            return;
        }

        var err = errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.InvalidNetworkAttachmentValue)
            .Which;
        err.Path.Should().Be("deployment.workloads[0].parameters[0].default");
        err.Message.Should().Contain(parameter).And.Contain("operator");
    }

    [Fact]
    public void A_host_port_default_is_refused_because_publishing_a_port_is_the_operators_choice()
    {
        var json = NetworkModeManifest(
            """{ "ports": [ { "name": "web", "port": 8080, "protocol": "TCP" } ] }""",
            """[ { "name": "port.web.hostPort", "type": "integer", "default": 8080 } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().ContainSingle(e => e.Code == DeploymentValidationCodes.ReservedParameterOverride)
            .Which.Message.Should().Contain("port.web.hostPort");
    }

    [Fact]
    public void A_host_port_re_declaration_with_only_a_hint_is_accepted()
    {
        var json = NetworkModeManifest(
            """{ "ports": [ { "name": "web", "port": 8080, "protocol": "TCP" } ] }""",
            """[ { "name": "port.web.hostPort", "type": "integer", "hint": "the LAN port" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"default\": \"backend-config\",", false)]
    [InlineData("", true)]
    public void A_configMapMount_parameter_default_is_refused_because_the_operator_names_the_ConfigMap(
        string defaultJson, bool expectAccepted)
    {
        var json = NetworkModeManifest(
            null,
            $$"""[ { "name": "cfg", "type": "text", {{defaultJson}} "hint": "ConfigMap name" } ]""")
            .Replace("\"parameters\":", "\"configMapMount\": [ { \"mountPath\": \"/config\", \"parameter\": \"cfg\" } ],\n      \"parameters\":", StringComparison.Ordinal);

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        if (expectAccepted)
        {
            errors.Should().BeEmpty();
            return;
        }

        errors.Should().ContainSingle(e => e.Code == DeploymentValidationCodes.ConfigMapParameterDefault)
            .Which.Path.Should().Be("deployment.workloads[0].parameters[0].default");
    }

    [Fact]
    public void A_network_attachments_slot_bound_to_the_reserved_network_attachments_name_is_rejected()
    {
        var json = NetworkAttachmentManifest(
            """[ { "parameter": "network.attachments" } ]""",
            """[ { "name": "netAttach", "type": "networkAttachment" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        var err = errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.NetworkAttachmentParameterType)
            .Which;
        err.Path.Should().Be("deployment.workloads[0].network.attachments[0].parameter");
        err.Message.Should().Contain("network.attachments").And.Contain("reserved");

        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.NetworkAttachmentParameterMissing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{ "hostNetwork": false, "mdns": true, "ipv6Required": false, "ports": [ { "name": "api", "port": 8080, "protocol": "TCP" } ] }""")]
    public void A_plain_workload_parses_unchanged_by_the_reserved_network_names(string? networkJson)
    {
        var json = NetworkModeManifest(
            networkJson,
            """
            [
              { "name": "aText", "type": "text", "default": "hello" },
              { "name": "aNumber", "type": "integer", "default": 5 },
              { "name": "aBool", "type": "boolean", "default": true },
              { "name": "anEnum", "type": "enum", "enumValues": ["a","b"], "default": "a" },
              { "name": "aSize", "type": "storageSize", "default": "64Mi" }
            ]
            """);

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty("no network-attachment rule may fire for a workload that declares no network intent");
        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.InvalidNetworkAttachmentValue);
        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.ReservedParameterOverride);
        parsed!.Workloads[0].Parameters.Should().HaveCount(5);
    }

    private static IReadOnlyList<DeploymentValidationError> UserNamespaceRedeclareErrors(string parameterJson)
    {
        var json = NetworkModeManifest(networkJson: null, parametersJson: $"[ {parameterJson} ]");
        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);
        return errors.Where(e => e.Code == DeploymentValidationCodes.ReservedParameterOverride).ToList();
    }

    [Theory]
    [InlineData("""{ "name": "security.userNamespace", "type": "boolean", "hint": "only the hint" }""")]
    [InlineData("""{ "name": "security.userNamespace", "type": "boolean", "default": false }""")]
    [InlineData("""{ "name": "security.userNamespace", "type": "boolean", "default": "false" }""")]
    [InlineData("""{ "name": "security.userNamespace", "type": "boolean", "default": "False" }""")]
    public void A_user_namespace_redeclare_that_keeps_the_false_default_is_legal(string parameterJson)
        => UserNamespaceRedeclareErrors(parameterJson).Should().BeEmpty();

    [Theory]
    [InlineData("""{ "name": "security.userNamespace", "type": "boolean", "default": true }""")]
    [InlineData("""{ "name": "security.userNamespace", "type": "boolean", "default": "true" }""")]
    [InlineData("""{ "name": "security.userNamespace", "type": "boolean", "default": "yes" }""")]
    public void A_user_namespace_redeclare_with_another_default_is_refused(string parameterJson)
        => UserNamespaceRedeclareErrors(parameterJson).Should().ContainSingle()
            .Which.Message.Should().Contain("security.userNamespace");

    [Fact]
    public void A_user_namespace_redeclare_with_another_type_is_refused()
        => UserNamespaceRedeclareErrors("""{ "name": "security.userNamespace", "type": "text" }""")
            .Should().ContainSingle();

    [Fact]
    public void A_user_namespace_redeclare_marked_required_is_refused()
        => UserNamespaceRedeclareErrors("""{ "name": "security.userNamespace", "type": "boolean", "required": true }""")
            .Should().ContainSingle();

    [Fact]
    public void A_user_namespace_redeclare_with_a_switch_key_is_refused_even_when_its_default_stays_false()
        => UserNamespaceRedeclareErrors(
                """{ "name": "security.userNamespace", "type": "boolean", "default": false, "widget": "radio" }""")
            .Should().ContainSingle();

    private static IReadOnlyList<DeploymentValidationError> ExistingClaimRedeclareErrors(string parameterJson)
    {
        var json = NetworkModeManifest(
            networkJson: null, parametersJson: $"[ {parameterJson} ]")
            .Replace(
                "\"health\":",
                "\"volumes\": [ { \"id\": \"data\", \"mountPath\": \"/data\", \"size\": \"64Mi\", \"criticality\": \"critical\" } ], \"health\":",
                StringComparison.Ordinal);
        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);
        return errors.Where(e => e.Code == DeploymentValidationCodes.ReservedParameterOverride).ToList();
    }

    [Theory]
    [InlineData("""{ "name": "existingClaim.data", "type": "text", "default": "reeve_postgres-data" }""")]
    [InlineData("""{ "name": "existingClaim.data", "type": "text", "default": "" }""")]
    public void An_existing_claim_redeclare_with_a_default_is_refused(string parameterJson)
        => ExistingClaimRedeclareErrors(parameterJson).Should().ContainSingle()
            .Which.Message.Should().Contain("existingClaim.data");

    [Fact]
    public void An_existing_claim_redeclare_that_overrides_only_the_hint_is_legal()
        => ExistingClaimRedeclareErrors("""{ "name": "existingClaim.data", "type": "text", "hint": "restore only" }""")
            .Should().BeEmpty();

    [Fact]
    public void A_repeated_port_name_is_reported_not_thrown()
    {
        var json = NetworkModeManifest(
            networkJson: """{ "ports": [ { "name": "api", "port": 80, "protocol": "TCP" }, { "name": "api", "port": 81, "protocol": "TCP" } ] }""",
            parametersJson: "[]");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().Contain(e => e.Code == DeploymentValidationCodes.DuplicatePortName);
    }

    [Fact]
    public void A_repeated_volume_id_is_reported_not_thrown()
    {
        var json = NetworkModeManifest(networkJson: null, parametersJson: "[]").Replace(
            "\"health\":",
            "\"volumes\": [ { \"id\": \"data\", \"mountPath\": \"/a\", \"size\": \"64Mi\", \"criticality\": \"critical\" }, "
            + "{ \"id\": \"data\", \"mountPath\": \"/b\", \"size\": \"64Mi\", \"criticality\": \"critical\" } ], \"health\":",
            StringComparison.Ordinal);

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().Contain(e => e.Code == DeploymentValidationCodes.DuplicateVolumeId);
    }

    [Fact]
    public void A_volume_size_redeclare_must_keep_the_storageSize_type()
        => ExistingClaimRedeclareErrors("""{ "name": "volumeSize.data", "type": "text", "default": "1Ti" }""")
            .Should().ContainSingle().Which.Message.Should().Contain("volumeSize.data");

    [Fact]
    public void A_volume_size_redeclare_with_a_storageSize_default_is_legal()
        => ExistingClaimRedeclareErrors("""{ "name": "volumeSize.data", "type": "storageSize", "default": "1Gi" }""")
            .Should().BeEmpty();

    [Fact]
    public void A_workload_level_description_parses_into_the_typed_model()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "w",
              "displayName": "W",
              "description": "Restreams RTSP cameras so the browser can play them.",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "service",
              "health": { "mode": "container" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } }
            }
          ]
        }
        """;

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty("a workload-level description is a declared, optional key");
        parsed!.Workloads[0].Description.Should().Be("Restreams RTSP cameras so the browser can play them.");
    }

    [Fact]
    public void A_workload_without_a_description_parses_it_as_null()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "w",
              "displayName": "W",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "service",
              "health": { "mode": "container" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } }
            }
          ]
        }
        """;

        var parsed = DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty();
        parsed!.Workloads[0].Description.Should().BeNull();
    }

    [Fact]
    public void A_non_string_workload_description_is_a_wrong_type_error()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "w",
              "displayName": "W",
              "description": 42,
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "service",
              "health": { "mode": "container" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } }
            }
          ]
        }
        """;

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.WrongType)
            .Which.Path.Should().Be("deployment.workloads[0].description");
    }

    private static JsonElement SingleParameterDeployment(string parameterJson) => ParseElement($$"""
    {
      "schemaVersion": 1,
      "workloads": [{
        "id": "w1",
        "displayName": "W1",
        "image": { "ref": "example.com/repo/img", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
        "kind": "service",
        "health": { "mode": "container" },
        "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } },
        "parameters": [ {{parameterJson}} ]
      }]
    }
    """);

    [Fact]
    public void Parameter_section_is_parsed_and_does_not_trip_the_closed_key_allowlist()
    {
        var element = SingleParameterDeployment("""{ "name": "radioTcp", "type": "text", "section": "Thread radio" }""");

        var manifest = DeploymentManifestParser.Parse(element, NoValidators, out var errors);

        errors.Should().BeEmpty("`section` is a legal parameter key — rule 23 must not reject it");
        manifest!.Workloads[0].Parameters[0].Section.Should().Be("Thread radio");
    }

    [Fact]
    public void Parameter_section_is_null_when_absent()
    {
        var element = SingleParameterDeployment("""{ "name": "radioTcp", "type": "text" }""");

        var manifest = DeploymentManifestParser.Parse(element, NoValidators, out var errors);

        errors.Should().BeEmpty();
        manifest!.Workloads[0].Parameters[0].Section.Should().BeNull();
    }

    [Theory]
    [InlineData("\"   \"", null)]
    [InlineData("\"\"", null)]
    [InlineData("\"  Thread radio \"", "Thread radio")]
    [InlineData("null", null)]
    public void Parameter_section_is_trimmed_and_blank_becomes_null(string sectionJson, string? expected)
    {
        var element = SingleParameterDeployment($$"""{ "name": "radioTcp", "type": "text", "section": {{sectionJson}} }""");

        var manifest = DeploymentManifestParser.Parse(element, NoValidators, out var errors);

        errors.Should().BeEmpty("a blank section is normalized away, not an error — its three sibling author-text fields (displayName/description/hint) are unvalidated too");
        manifest!.Workloads[0].Parameters[0].Section.Should().Be(expected);
    }

    [Fact]
    public void Parameter_section_of_the_wrong_type_reports_the_existing_wrong_type_code()
    {
        var element = SingleParameterDeployment("""{ "name": "radioTcp", "type": "text", "section": 7 }""");

        DeploymentManifestParser.Parse(element, NoValidators, out var errors);

        errors.Should().ContainSingle()
            .Which.Code.Should().Be(DeploymentValidationCodes.WrongType, "no new error code is introduced for section");
    }

    private static string DeviceSlotEnvManifest(string envJson, string privilegesJson, string parametersJson) => $$"""
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "slots",
              "displayName": "Slots",
              "image": { "ref": "r", "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000" },
              "kind": "service",
              "health": { "mode": "container" },
              "extensions": { "reeve": { "protocol": { "version": "1", "minSupported": "1" } } },
              "env": {{envJson}},
              "privileges": {{privilegesJson}},
              "parameters": {{parametersJson}}
            }
          ]
        }
        """;

    [Fact]
    public void An_env_bound_to_a_multi_device_slots_declared_parameter_is_rejected()
    {
        var json = DeviceSlotEnvManifest(
            """[ { "name": "ACC", "from": "parameter:acc" } ]""",
            """{ "devices": [ { "parameter": "acc", "maxCount": 3 } ] }""",
            """[ { "name": "acc", "type": "devicePath" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        var err = errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.EnvBoundToMultiDeviceSlot)
            .Which;
        err.Path.Should().Be("deployment.workloads[0].env[0].from");
        err.Message.Should().Contain("acc").And.Contain("3");

        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.UndeclaredParameterReference);
    }

    [Fact]
    public void An_env_bound_to_a_single_device_slots_parameter_stays_legal()
    {
        var json = DeviceSlotEnvManifest(
            """[ { "name": "GPU", "from": "parameter:gpuDevice" } ]""",
            """{ "devices": [ { "parameter": "gpuDevice", "maxCount": 1 } ] }""",
            """[ { "name": "gpuDevice", "type": "devicePath" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void An_env_bound_to_a_declared_parameter_backing_no_device_slot_stays_legal()
    {
        var json = DeviceSlotEnvManifest(
            """[ { "name": "MODE", "from": "parameter:mode" } ]""",
            """{ "devices": [ { "parameter": "acc", "maxCount": 3 } ] }""",
            """[ { "name": "mode", "type": "text" }, { "name": "acc", "type": "devicePath" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void An_env_bound_to_an_undeclared_parameter_still_reports_the_undeclared_code()
    {
        var json = DeviceSlotEnvManifest(
            """[ { "name": "NOPE", "from": "parameter:nope" } ]""",
            """{ "devices": [ { "parameter": "acc", "maxCount": 3 } ] }""",
            """[ { "name": "acc", "type": "devicePath" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        var err = errors.Should()
            .ContainSingle(e => e.Code == DeploymentValidationCodes.UndeclaredParameterReference)
            .Which;
        err.Path.Should().Be("deployment.workloads[0].env[0].from");
        err.Message.Should().Contain("nope");

        errors.Should().NotContain(e => e.Code == DeploymentValidationCodes.EnvBoundToMultiDeviceSlot);
    }

    [Fact]
    public void A_multi_device_slot_with_no_env_binding_stays_legal()
    {
        var json = DeviceSlotEnvManifest(
            """[ ]""",
            """{ "devices": [ { "parameter": "acc", "maxCount": 3 } ] }""",
            """[ { "name": "acc", "type": "devicePath" } ]""");

        DeploymentManifestParser.Parse(ParseElement(json), NoValidators, out var errors);

        errors.Should().BeEmpty("the slot itself is legal — only binding an env to its declared key is not");
    }
}
