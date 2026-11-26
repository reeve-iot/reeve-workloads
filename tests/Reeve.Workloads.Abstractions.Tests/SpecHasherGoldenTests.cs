using Reeve.Workloads.Domain;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class SpecHasherGoldenTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string MatterDigest = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";
    private const string BleDigest = "sha256:" + "2222222222222222222222222222222222222222222222222222222222222222";

    private const string MatterGoldenHash = "0f34d52ab3d2dadffd9a3370798e6b5ed2bd2b4effa1d666a8122fa8bb0c1ba4";

    private const string BluetoothGoldenHash = "4d0dde112ae43defb64b013c5b1105ba77eb22f3734548f1bff5d73801568f03";

    private const string ScanPerNodeGoldenHash = "7193b2c7314be9140e876a1757964e2e22998639c751dec6c3df5488b1717e2f";

    [Fact]
    public void The_canonical_key_for_the_owning_plugin_type_is_frozen()
    {
        SpecHasher.GroupIdCanonicalKey.Should().Be("pluginTypeId");
    }

    [Fact]
    public void Matter_like_spec_hash_is_stable()
    {
        ComputeMatter().SpecHash.Should().Be(MatterGoldenHash);
    }

    [Fact]
    public void Bluetooth_like_spec_hash_is_stable()
    {
        ComputeBluetooth().SpecHash.Should().Be(BluetoothGoldenHash);
    }

    [Fact]
    public void The_two_golden_specs_hash_differently()
    {
        ComputeMatter().SpecHash.Should().NotBe(ComputeBluetooth().SpecHash);
    }

    [Fact]
    public void ScanPerNode_spec_hash_is_stable()
    {
        ComputeScanPerNode().SpecHash.Should().Be(ScanPerNodeGoldenHash);
    }

    [Fact]
    public void ScanPerNode_golden_differs_from_matter_and_bluetooth()
    {
        ComputeScanPerNode().SpecHash.Should().NotBe(ComputeMatter().SpecHash);
        ComputeScanPerNode().SpecHash.Should().NotBe(ComputeBluetooth().SpecHash);
    }

    [Fact]
    public void Empty_device_group_ids_hash_identically_to_the_field_being_absent()
    {
        var notSet = ComputeMatter();
        var explicitlyEmpty = notSet with { Privileges = notSet.Privileges! with { DeviceGroupIds = new List<int>() } };

        SpecHasher.Compute(notSet).Should().Be(MatterGoldenHash);
        SpecHasher.Compute(explicitlyEmpty).Should().Be(MatterGoldenHash);
    }

    [Fact]
    public void Granting_a_device_group_id_moves_the_hash()
    {
        var withoutGroup = ComputeMatter();
        var withGroup = withoutGroup with { Privileges = withoutGroup.Privileges! with { DeviceGroupIds = new[] { 5 } } };

        SpecHasher.Compute(withGroup).Should().NotBe(SpecHasher.Compute(withoutGroup));
    }

    [Fact]
    public void JoinsDeviceGroups_does_not_change_the_spec_hash()
    {
        var withoutFlag = ComputeMatter();
        var withFlag = withoutFlag with { Privileges = withoutFlag.Privileges! with { JoinsDeviceGroups = true } };

        SpecHasher.Compute(withFlag).Should().Be(MatterGoldenHash);
        SpecHasher.Compute(withFlag).Should().Be(SpecHasher.Compute(withoutFlag));
    }

    [Fact]
    public void An_env_flagged_out_of_the_hash_leaves_the_pinned_golden_unchanged()
    {
        var plain = ComputeMatter();
        var flagged = plain with
        {
            Env = [.. plain.Env, new ResolvedEnvVar { Name = "EXCLUDED", Kind = ResolvedEnvKind.Literal, Value = "v", ExcludedFromSpecHash = true }],
        };

        SpecHasher.Compute(flagged).Should().Be(MatterGoldenHash);
    }

    private static WorkloadDesiredSpec ComputeMatter()
    {
        var wl = new WorkloadManifest
        {
            Id = "matter",
            DisplayName = "Matter",
            Image = new WorkloadImage
            {
                Ref = "ghcr.io/reeve/matter",
                Digest = MatterDigest,
                Platforms = new[] { "linux/amd64", "linux/arm64" },
            },
            Kind = WorkloadKind.SingleNode,
            Network = new WorkloadNetwork { Mdns = true },
            Privileges = new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } } },
            Parameters = new[] { new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath } },
            Resources = new WorkloadResources
            {
                Requests = new ResourceQuantities { Cpu = "100m", Memory = "256Mi" },
                Limits = new ResourceQuantities { Memory = "512Mi" },
            },
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Env = new[]
            {
                new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" },
                new WorkloadEnv { Name = "REEVE_WORKLOAD_TOKEN", From = "bootstrap:workloadToken" },
            },
        };
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/serial/by-id/usb-Thread" }, StandInProviders.Kubernetes);
        // the workload token is not part of the hash (spec-hash-excluded); it is resolved at render
        var settings = new WorkloadOrchestratorSettings();
        var bootstrap = new WorkloadBootstrapContext
        {
            InstanceId = InstanceId,
            SecretName = "reeve-wl-matter-token",
            Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal)
            {
                ["gatewayUrl"] = BootstrapValue.Literal($"wss://reeve.local/ws/workloads/{InstanceId:D}"),
                ["workloadToken"] = BootstrapValue.HostCredential("workload-token"),
                ["specHash"] = BootstrapValue.SpecHash,
            },
        };
        return WorkloadDesiredSpecFactory.Compute(
            wl,
            settings,
            resolved,
            bootstrap,
            StandInProviders.Kubernetes,
            DefaultWorkloadResourceNamer.Instance,
            GroupInstanceId,
            "com.reeve.matter",
            InstanceId,
            "install-1");
    }

    private static WorkloadDesiredSpec ComputeScanPerNode()
    {
        var wl = new WorkloadManifest
        {
            Id = "matter",
            DisplayName = "Matter",
            Image = new WorkloadImage
            {
                Ref = "ghcr.io/reeve/matter",
                Digest = MatterDigest,
                Platforms = new[] { "linux/amd64", "linux/arm64" },
            },
            Kind = WorkloadKind.ScanPerNode,
            Network = new WorkloadNetwork { Mdns = true },
            Privileges = new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } } },
            Parameters = new[] { new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath } },
            Resources = new WorkloadResources
            {
                Requests = new ResourceQuantities { Cpu = "100m", Memory = "256Mi" },
                Limits = new ResourceQuantities { Memory = "512Mi" },
            },
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Env = new[]
            {
                new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" },
                new WorkloadEnv { Name = "REEVE_WORKLOAD_TOKEN", From = "bootstrap:workloadToken" },
            },
        };
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/serial/by-id/usb-Thread" }, StandInProviders.Kubernetes);
        // the workload token is not part of the hash (spec-hash-excluded); it is resolved at render
        var settings = new WorkloadOrchestratorSettings();
        var bootstrap = new WorkloadBootstrapContext
        {
            InstanceId = InstanceId,
            SecretName = "reeve-wl-matter-token",
            Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal)
            {
                ["gatewayUrl"] = BootstrapValue.Literal($"wss://reeve.local/ws/workloads/{InstanceId:D}"),
                ["workloadToken"] = BootstrapValue.HostCredential("workload-token"),
                ["specHash"] = BootstrapValue.SpecHash,
            },
        };
        return WorkloadDesiredSpecFactory.Compute(
            wl,
            settings,
            resolved,
            bootstrap,
            StandInProviders.Kubernetes,
            DefaultWorkloadResourceNamer.Instance,
            GroupInstanceId,
            "com.reeve.matter",
            InstanceId,
            "install-1");
    }

    private static WorkloadDesiredSpec ComputeBluetooth()
    {
        var wl = new WorkloadManifest
        {
            Id = "bluetooth",
            DisplayName = "Bluetooth",
            Image = new WorkloadImage
            {
                Ref = "ghcr.io/reeve/bluetooth",
                Digest = BleDigest,
                Platforms = new[] { "linux/arm64" },
            },
            Kind = WorkloadKind.SingleNode,
            Network = new WorkloadNetwork { HostNetwork = true },
            Privileges = new WorkloadPrivileges
            {
                Capabilities = new[] { WorkloadCapability.NetAdmin, WorkloadCapability.NetRaw },
                HostDbus = true,
            },
            Volumes = new[]
            {
                new WorkloadVolume { Id = "bindkeys", MountPath = "/data", Size = "64Mi", Criticality = VolumeCriticality.Critical },
            },
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Env = new[]
            {
                new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" },
                new WorkloadEnv { Name = "REEVE_WORKLOAD_TOKEN", From = "bootstrap:workloadToken" },
            },
        };
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?>(), StandInProviders.Kubernetes);
        var settings = new WorkloadOrchestratorSettings();
        var bootstrap = new WorkloadBootstrapContext
        {
            InstanceId = InstanceId,
            SecretName = "reeve-wl-ble-token",
            Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal)
            {
                ["gatewayUrl"] = BootstrapValue.Literal($"wss://reeve.local/ws/workloads/{InstanceId:D}"),
                ["workloadToken"] = BootstrapValue.HostCredential("workload-token"),
                ["specHash"] = BootstrapValue.SpecHash,
            },
        };
        return WorkloadDesiredSpecFactory.Compute(
            wl,
            settings,
            resolved,
            bootstrap,
            StandInProviders.Kubernetes,
            DefaultWorkloadResourceNamer.Instance,
            GroupInstanceId,
            "com.reeve.bluetooth",
            InstanceId,
            "install-1");
    }
}
