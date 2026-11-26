using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Update;
using Xunit;
using static Reeve.Workloads.Abstractions.Tests.WorkloadUpdateDocumentFixtures;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadUpdateDocumentMapperTests
{
    [Fact]
    public void A_parameter_map_gives_back_every_workloads_parameters()
    {
        var previous = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
        {
            ["zigbee"] = new Dictionary<string, string?> { ["port"] = "5540", ["token"] = null },
            ["matter"] = new Dictionary<string, string?>(),
        };

        var document = WorkloadDocumentMapper.ParameterMap(previous);

        document.Unreadable.Should().BeFalse();
        WorkloadDocumentMapper.FromDocument(document).Should().BeEquivalentTo(previous);
    }

    [Fact]
    public void A_parameter_map_is_stored_sorted_on_both_levels()
    {
        var document = WorkloadDocumentMapper.ParameterMap(
            new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
            {
                ["zigbee"] = new Dictionary<string, string?> { ["port"] = "5540", ["host"] = "a" },
                ["matter"] = new Dictionary<string, string?>(),
            });

        document.Workloads.Select(workload => workload.WorkloadId).Should().Equal("matter", "zigbee");
        document.Workloads[1].Entries.Select(entry => entry.Key).Should().Equal("host", "port");
    }

    [Fact]
    public void A_spec_map_gives_back_every_spec_whole()
    {
        var specs = Specs();

        var document = WorkloadDocumentMapper.SpecMap(specs);

        document.Unreadable.Should().BeFalse();
        WorkloadDocumentMapper.FromDocument(document).Should().BeEquivalentTo(specs);
    }

    [Fact]
    public void A_specs_secret_material_is_not_written_to_the_document()
    {
        var spec = Specs()["matter"] with
        {
            Env = [new ResolvedEnvVar { Name = "MQTT_PASSWORD", Kind = ResolvedEnvKind.SecretRef }],
            SecretMaterial = new Dictionary<string, string> { ["MQTT_PASSWORD"] = "hunter2" },
        };

        var document = WorkloadDocumentMapper.SpecMap(new Dictionary<string, WorkloadDesiredSpec> { ["matter"] = spec });

        JsonSerializer.Serialize(document).Should().NotContain("hunter2");
        WorkloadDocumentMapper.FromDocument(document)["matter"].SecretMaterial.Should().BeEmpty();
    }

    [Fact]
    public void User_namespace_fields_round_trip_and_off_is_not_written()
    {
        var off = Specs()["matter"];
        var on = off with { UserNamespace = true };
        var skipped = off with { UserNamespaceSkippedForHostNetwork = true };
        WorkloadDocumentMapper.SpecMap(new Dictionary<string, WorkloadDesiredSpec> { ["matter"] = off })
            .Workloads.Single().Should().Match<WorkloadSpecDocument>(d => d.UserNamespace == null && d.UserNamespaceSkippedForHostNetwork == null);

        var onBack = WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.SpecMap(new Dictionary<string, WorkloadDesiredSpec> { ["matter"] = on }))["matter"];
        var skippedBack = WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.SpecMap(new Dictionary<string, WorkloadDesiredSpec> { ["matter"] = skipped }))["matter"];
        onBack.UserNamespace.Should().BeTrue();
        onBack.UserNamespaceSkippedForHostNetwork.Should().BeFalse();
        skippedBack.UserNamespace.Should().BeFalse();
        skippedBack.UserNamespaceSkippedForHostNetwork.Should().BeTrue();
    }

    [Fact]
    public void Device_passthrough_fields_round_trip_and_off_is_not_written()
    {
        var off = Specs()["zigbee"];
        var on = off with
        {
            Privileges = off.Privileges! with { DevicePassthroughPrivileged = true },
            UserNamespaceSkippedForDevicePassthrough = true,
        };

        WorkloadDocumentMapper.SpecMap(new Dictionary<string, WorkloadDesiredSpec> { ["zigbee"] = off })
            .Workloads.Single().Should().Match<WorkloadSpecDocument>(d =>
                d.UserNamespaceSkippedForDevicePassthrough == null && d.Privileges!.DevicePassthroughPrivileged == null);

        var back = WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.SpecMap(new Dictionary<string, WorkloadDesiredSpec> { ["zigbee"] = on }))["zigbee"];
        back.Privileges!.DevicePassthroughPrivileged.Should().BeTrue();
        back.UserNamespaceSkippedForDevicePassthrough.Should().BeTrue();
    }

    [Fact]
    public void A_volume_group_round_trips_and_no_group_stays_none()
    {
        var specs = Specs();
        var back = WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.SpecMap(specs));

        specs["zigbee"].Privileges!.FsGroup.Should().Be(1000, "the fixture must carry the field for this to mean anything");
        back["zigbee"].Privileges!.FsGroup.Should().Be(1000);

        var without = specs["zigbee"] with { Privileges = specs["zigbee"].Privileges! with { FsGroup = null } };
        WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.SpecMap(new Dictionary<string, WorkloadDesiredSpec> { ["zigbee"] = without }))
            ["zigbee"].Privileges!.FsGroup.Should().BeNull();
    }

    [Fact]
    public void A_spec_map_is_stored_by_sorted_key_and_keeps_every_list_in_its_order()
    {
        var document = WorkloadDocumentMapper.SpecMap(Specs());

        document.Workloads.Select(workload => workload.Key).Should().Equal("matter", "zigbee");
        var zigbee = document.Workloads[1];
        zigbee.Labels.Select(label => label.Key).Should().Equal("a.io/x", "b.io/y");
        zigbee.Platforms.Should().Equal("linux/arm64", "linux/amd64");
        zigbee.Network!.Attachments.Should().Equal("iot", "cam");
        zigbee.Privileges!.DeviceGroupIds.Should().Equal(20, 5);
        zigbee.Env.Select(env => env.Name).Should().Equal("TOKEN", "MODE");
    }

    [Fact]
    public void A_spec_map_carries_each_envs_credential_id_and_the_provisioned_credentials_in_order()
    {
        var zigbee = WorkloadDocumentMapper.SpecMap(Specs()).Workloads[1];

        zigbee.Env.Select(env => env.CredentialId).Should().Equal("workload-token", null);
        zigbee.ProvisionedCredentials!.Select(credential => (credential.CredentialId, credential.Name))
            .Should().Equal(("workload-token", "REEVE_WORKLOAD_TOKEN"), ("feed", "FEED_TOKEN"));
        WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.SpecMap(Specs()))["zigbee"].ProvisionedCredentials
            .Should().Equal(new ProvisionedCredential("workload-token", "REEVE_WORKLOAD_TOKEN"), new ProvisionedCredential("feed", "FEED_TOKEN"));
    }

    [Fact]
    public void A_spec_map_keeps_the_hash_exclusion_flag_of_an_env_through_a_save_and_a_load()
    {
        var specs = Specs();
        specs["zigbee"] = specs["zigbee"] with
        {
            Env = [.. specs["zigbee"].Env, new ResolvedEnvVar { Name = "EXCLUDED", Kind = ResolvedEnvKind.Literal, Value = "v", ExcludedFromSpecHash = true }],
        };

        var restored = WorkloadDocumentMapper.FromDocument(WorkloadDocumentMapper.SpecMap(specs));

        restored["zigbee"].Env.Should().BeEquivalentTo(specs["zigbee"].Env, options => options.WithStrictOrdering());
        restored["zigbee"].Env.Single(env => env.Name == "EXCLUDED").ExcludedFromSpecHash.Should().BeTrue();
        restored["zigbee"].Env.Where(env => env.Name != "EXCLUDED").Should().OnlyContain(env => !env.ExcludedFromSpecHash);
    }

    [Fact]
    public void A_stored_env_written_without_the_flag_loads_with_it_off()
    {
        var document = WorkloadDocumentMapper.SpecMap(Specs());
        var json = JsonSerializer.Serialize(document);
        json = System.Text.RegularExpressions.Regex.Replace(json, ",\"ExcludedFromSpecHash\":(null|true|false)", string.Empty);
        json.Should().NotContain("ExcludedFromSpecHash");

        var reloaded = JsonSerializer.Deserialize<WorkloadSpecMapDocument>(json)!;

        WorkloadDocumentMapper.FromDocument(reloaded)["zigbee"].Env.Should().OnlyContain(env => !env.ExcludedFromSpecHash);
    }

    [Fact]
    public void An_unflagged_env_stores_no_flag_value()
        => WorkloadDocumentMapper.SpecMap(Specs()).Workloads[1].Env.Should().OnlyContain(env => env.ExcludedFromSpecHash == null);

    [Theory]
    [InlineData("feed", "")]
    [InlineData(" ", "FEED_TOKEN")]
    public void A_stored_provisioned_credential_with_a_blank_id_or_name_reads_as_unreadable(string credentialId, string name)
    {
        var document = WorkloadDocumentMapper.SpecMap(Specs());
        document.Workloads[1].ProvisionedCredentials![1] = new SpecProvisionedCredentialDocument { CredentialId = credentialId, Name = name };

        WorkloadDocumentRead.Decide(document, WorkloadDocumentMapper.FromDocument).Verdict
            .Should().Be(WorkloadDocumentVerdict.Unreadable);
    }

    [Fact]
    public void A_scan_node_list_that_was_null_stays_null_and_an_empty_one_stays_empty()
    {
        var document = WorkloadDocumentMapper.SpecMap(Specs());

        document.Workloads[0].ScanReadyNodeNames.Should().BeNull();
        document.Workloads[1].ScanReadyNodeNames.Should().NotBeNull().And.BeEmpty();
        document.Workloads[1].ScanIneligibleNodeNames.Should().Equal("node-c");
    }

    [Fact]
    public void An_absent_optional_part_of_a_spec_stays_absent()
    {
        var matter = WorkloadDocumentMapper.SpecMap(Specs()).Workloads[0];

        matter.Resources.Should().BeNull();
        matter.Network.Should().BeNull();
        matter.Privileges.Should().BeNull();
        matter.Observability.Should().BeNull();
        matter.Health.Http.Should().BeNull();
        matter.UnsupportedPodPatchJson.Should().BeNull();
    }

    [Fact]
    public void Evidence_gives_back_every_capture_in_order()
    {
        var evidence = new WorkloadUpdateEvidence("2.0.0", At,
        [
            new WorkloadEvidenceCapture("zigbee", ["line 2", "line 1"], ["BackOff"], null),
            new WorkloadEvidenceCapture("matter", [], [], "pod gone before capture"),
        ]);

        var document = WorkloadDocumentMapper.Evidence(evidence);

        document.Workloads.Select(workload => workload.WorkloadId).Should().Equal("zigbee", "matter");
        WorkloadDocumentMapper.FromDocument(document)
            .Should().BeEquivalentTo(evidence, options => options.WithStrictOrdering());
    }

    [Fact]
    public void A_clock_keeps_every_field_through_the_document()
    {
        var clock = new WorkloadGateClockState
        {
            ClockStartedAtUtc = At,
            PausedSinceUtc = null,
            AccumulatedPause = TimeSpan.FromSeconds(95),
            ReconnectGraceUntilUtc = At.AddMinutes(5),
            GraceConsumed = true,
        };

        var document = WorkloadDocumentMapper.GateClock(clock);

        document.Unreadable.Should().BeFalse();
        WorkloadDocumentMapper.FromDocument(document).Should().Be(clock);
    }
}

internal static class WorkloadUpdateDocumentFixtures
{
    public static readonly DateTimeOffset At = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    public static Dictionary<string, WorkloadDesiredSpec> Specs() => new(StringComparer.Ordinal)
    {
        ["zigbee"] = Bare("zigbee") with
        {
            Labels = new Dictionary<string, string> { ["b.io/y"] = "2", ["a.io/x"] = "1" },
            Platforms = ["linux/arm64", "linux/amd64"],
            NodeSelector = new Dictionary<string, string> { ["zone"] = "a" },
            Resources = new ResolvedResources { RequestsCpu = "100m", LimitsMemory = "256Mi" },
            Network = new ResolvedNetwork
            {
                Mode = WorkloadNetworkMode.OperatorNetwork,
                Attachments = ["iot", "cam"],
                Mdns = true,
                Ports = [new ResolvedPort { Name = "http", Port = 8080, Protocol = "TCP", HostPort = 18080 }],
            },
            Privileges = new ResolvedPrivileges
            {
                Capabilities = ["NET_ADMIN"],
                FsGroup = 1000,
                TunDevice = true,
                DbusHostPath = "/run/dbus",
                DevicePaths = ["/dev/ttyUSB0"],
                DeviceGroupIds = [20, 5],
                JoinsDeviceGroups = true,
                HostPaths = [new ResolvedHostPath { HostPath = "/sys/class", MountPath = "/host/sys" }],
                NodeLocalState = new ResolvedNodeLocalState { HostPath = "/var/lib/reeve/zigbee", Criticality = VolumeCriticality.Critical },
            },
            Volumes =
            [
                new ResolvedVolume
                {
                    Id = "state",
                    MountPath = "/data",
                    Size = "1Gi",
                    Criticality = VolumeCriticality.Recoverable,
                    ResourceName = "reeve-zigbee-state",
                    ExistingClaim = true,
                    StorageClassName = "local",
                },
            ],
            ConfigMapMounts = [new ResolvedConfigMapMount { MountPath = "/etc/zigbee", ConfigMapName = "zigbee-config" }],
            Health = new WorkloadHealth
            {
                Mode = new WorkloadHealthMode("adapter-session"),
                HeartbeatSeconds = 30,
                Http = new WorkloadHttpProbe { Port = 8080, LivenessPath = "/live" },
            },
            Observability = new WorkloadObservability { Metrics = new WorkloadMetrics { Port = 9090, Path = "/metrics" } },
            Env =
            [
                new ResolvedEnvVar { Name = "TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "token", CredentialId = "workload-token" },
                new ResolvedEnvVar { Name = "MODE", Kind = ResolvedEnvKind.Literal, Value = "bridge" },
            ],
            ProvisionedCredentials = [new("workload-token", "REEVE_WORKLOAD_TOKEN"), new("feed", "FEED_TOKEN")],
            HostRequirements = [new HostRequirement { Kind = HostRequirementKind.DbusService, Name = "bluetoothd" }],
            UnsupportedPodPatch = new UnsupportedPodPatchSpec { PatchJson = "[{\"op\":\"add\",\"path\":\"/x\",\"value\":1}]" },
            RestartNonce = 7,
            PendingScanRunId = "run-1",
            ScanReadyNodeNames = [],
            ScanIneligibleNodeNames = ["node-c"],
        },
        ["matter"] = Bare("matter"),
    };

    private static WorkloadDesiredSpec Bare(string workloadId) => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "smarthome",
        WorkloadId = workloadId,
        InstanceId = Guid.Parse("12345678-0000-0000-0000-000000000000"),
        Inst8 = "12345678",
        Provider = StandInProviders.KubernetesId,
        ResourceName = $"reeve-{workloadId}",
        SecretName = $"reeve-{workloadId}-secret",
        Labels = new Dictionary<string, string>(),
        Image = "ghcr.io/reeve/" + workloadId + "@sha256:" + new string('a', 64),
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "0123abcd",
    };
}
