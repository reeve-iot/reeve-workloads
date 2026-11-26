using Reeve.Workloads.Domain;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class SpecHasherInvariantTests
{
    private const string Digest = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Digest2 = "sha256:" + "2222222222222222222222222222222222222222222222222222222222222222";
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static WorkloadDesiredSpec BaseSpec(
        IReadOnlyList<string>? caps = null,
        IReadOnlyList<ResolvedEnvVar>? env = null,
        IReadOnlyList<ResolvedVolume>? volumes = null,
        IReadOnlyList<string>? platforms = null,
        IReadOnlyList<string>? devicePaths = null)
        => new()
        {
            GroupInstanceId = GroupInstanceId,
            GroupId = "com.reeve.x",
            WorkloadId = "w",
            InstanceId = InstanceId,
            Inst8 = WorkloadInstance.ComputeInst8(InstanceId),
            Provider = StandInProviders.KubernetesId,
            ResourceName = "reeve-wl-x-w-inst",
            SecretName = "reeve-wl-x-w-inst",
            Labels = new Dictionary<string, string>(),
            Image = $"ghcr.io/x@{Digest}",
            Platforms = platforms ?? new[] { "linux/amd64", "linux/arm64" },
            Kind = WorkloadKind.SingleNode,
            Privileges = new ResolvedPrivileges
            {
                Capabilities = caps ?? new[] { "NET_ADMIN", "NET_RAW" },
                DevicePaths = devicePaths ?? new[] { "/dev/ttyUSB0", "/dev/ttyUSB1" },
            },
            Volumes = volumes ?? new[]
            {
                new ResolvedVolume { Id = "a", MountPath = "/a", Size = "1Gi", Criticality = VolumeCriticality.Cache, ResourceName = "pvc-a" },
                new ResolvedVolume { Id = "b", MountPath = "/b", Size = "2Gi", Criticality = VolumeCriticality.Critical, ResourceName = "pvc-b" },
            },
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Env = env ?? new[]
            {
                new ResolvedEnvVar { Name = "A", Kind = ResolvedEnvKind.Literal, Value = "1" },
                new ResolvedEnvVar { Name = "B", Kind = ResolvedEnvKind.Literal, Value = "2" },
            },
            SpecHash = string.Empty,
        };

    [Fact]
    public void UserNamespace_off_hashes_like_a_spec_that_never_had_the_field()
        => SpecHasher.Compute(BaseSpec() with { UserNamespace = false }).Should().Be(SpecHasher.Compute(BaseSpec()));

    [Fact]
    public void UserNamespace_on_changes_the_hash()
        => SpecHasher.Compute(BaseSpec() with { UserNamespace = true }).Should().NotBe(SpecHasher.Compute(BaseSpec()));

    [Fact]
    public void The_skip_report_does_not_move_the_hash()
        => SpecHasher.Compute(BaseSpec() with { UserNamespaceSkippedForHostNetwork = true })
            .Should().Be(SpecHasher.Compute(BaseSpec()));

    private static ResolvedEnvVar Flagged(string name, string? value)
        => new() { Name = name, Kind = ResolvedEnvKind.Literal, Value = value, ExcludedFromSpecHash = true };

    private static ResolvedEnvVar Plain(string name, string? value)
        => new() { Name = name, Kind = ResolvedEnvKind.Literal, Value = value };

    private static readonly ResolvedEnvVar[] DefaultEnv =
    [
        Plain("A", "1"),
        Plain("B", "2"),
    ];

    [Theory]
    [InlineData("first")]
    [InlineData("")]
    [InlineData(null)]
    public void An_env_flagged_out_of_the_hash_hashes_like_a_spec_without_it_whatever_its_value(string? value)
        => SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, Flagged("EXCLUDED", value)]))
            .Should().Be(SpecHasher.Compute(BaseSpec()));

    [Fact]
    public void Changing_only_a_flagged_envs_value_leaves_the_hash_alone()
        => SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, Flagged("EXCLUDED", "first")]))
            .Should().Be(SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, Flagged("EXCLUDED", "second")])));

    [Fact]
    public void A_flagged_envs_name_is_outside_the_hash_too()
        => SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, Flagged("EXCLUDED_ONE", "v")]))
            .Should().Be(SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, Flagged("EXCLUDED_TWO", "v")])));

    [Fact]
    public void A_flagged_env_sorting_between_hashed_ones_does_not_disturb_them()
        => SpecHasher.Compute(BaseSpec(env: [Plain("A", "1"), Flagged("AA", "x"), Plain("B", "2"), Flagged("Z", "y")]))
            .Should().Be(SpecHasher.Compute(BaseSpec()));

    [Fact]
    public void An_ordinary_literal_with_the_same_name_and_value_still_moves_the_hash()
        => SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, Plain("EXCLUDED", "first")]))
            .Should().NotBe(SpecHasher.Compute(BaseSpec()));

    [Fact]
    public void An_ordinary_literal_still_moves_the_hash_when_its_value_changes()
        => SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, Plain("EXCLUDED", "first")]))
            .Should().NotBe(SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, Plain("EXCLUDED", "second")])));

    [Fact]
    public void An_empty_ordinary_literal_is_still_hashed()
        => SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, Plain("EXCLUDED", "")]))
            .Should().NotBe(SpecHasher.Compute(BaseSpec()));

    [Fact]
    public void A_flagged_env_is_skipped_whatever_kind_it_carries()
    {
        var secretRef = new ResolvedEnvVar { Name = "S", Kind = ResolvedEnvKind.SecretRef, SecretRef = "ref", ExcludedFromSpecHash = true };
        SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, secretRef])).Should().Be(SpecHasher.Compute(BaseSpec()));
    }

    [Fact]
    public void The_flag_does_not_disturb_the_hash_of_an_unflagged_literal()
        => SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, Plain("C", "3") with { ExcludedFromSpecHash = false }]))
            .Should().Be(SpecHasher.Compute(BaseSpec(env: [.. DefaultEnv, Plain("C", "3")])));

    [Fact]
    public void Reordering_capabilities_yields_the_same_hash()
    {
        var a = SpecHasher.Compute(BaseSpec(caps: new[] { "NET_ADMIN", "NET_RAW" }));
        var b = SpecHasher.Compute(BaseSpec(caps: new[] { "NET_RAW", "NET_ADMIN" }));
        b.Should().Be(a);
    }

    [Fact]
    public void Reordering_env_yields_the_same_hash()
    {
        var a = SpecHasher.Compute(BaseSpec(env: new[]
        {
            new ResolvedEnvVar { Name = "A", Kind = ResolvedEnvKind.Literal, Value = "1" },
            new ResolvedEnvVar { Name = "B", Kind = ResolvedEnvKind.Literal, Value = "2" },
        }));
        var b = SpecHasher.Compute(BaseSpec(env: new[]
        {
            new ResolvedEnvVar { Name = "B", Kind = ResolvedEnvKind.Literal, Value = "2" },
            new ResolvedEnvVar { Name = "A", Kind = ResolvedEnvKind.Literal, Value = "1" },
        }));
        b.Should().Be(a);
    }

    [Fact]
    public void Reordering_volumes_yields_the_same_hash()
    {
        var a = SpecHasher.Compute(BaseSpec(volumes: new[]
        {
            new ResolvedVolume { Id = "a", MountPath = "/a", Size = "1Gi", Criticality = VolumeCriticality.Cache, ResourceName = "pvc-a" },
            new ResolvedVolume { Id = "b", MountPath = "/b", Size = "2Gi", Criticality = VolumeCriticality.Critical, ResourceName = "pvc-b" },
        }));
        var b = SpecHasher.Compute(BaseSpec(volumes: new[]
        {
            new ResolvedVolume { Id = "b", MountPath = "/b", Size = "2Gi", Criticality = VolumeCriticality.Critical, ResourceName = "pvc-b" },
            new ResolvedVolume { Id = "a", MountPath = "/a", Size = "1Gi", Criticality = VolumeCriticality.Cache, ResourceName = "pvc-a" },
        }));
        b.Should().Be(a);
    }

    [Fact]
    public void Reordering_platforms_and_device_paths_yields_the_same_hash()
    {
        var a = SpecHasher.Compute(BaseSpec(
            platforms: new[] { "linux/amd64", "linux/arm64" },
            devicePaths: new[] { "/dev/ttyUSB0", "/dev/ttyUSB1" }));
        var b = SpecHasher.Compute(BaseSpec(
            platforms: new[] { "linux/arm64", "linux/amd64" },
            devicePaths: new[] { "/dev/ttyUSB1", "/dev/ttyUSB0" }));
        b.Should().Be(a);
    }

    [Fact]
    public void Changing_storage_class_yields_the_same_hash()
    {
        var a = SpecHasher.Compute(BaseSpec(volumes: new[]
        {
            new ResolvedVolume { Id = "a", MountPath = "/a", Size = "1Gi", Criticality = VolumeCriticality.Cache, ResourceName = "pvc-a", StorageClassName = "standard" },
        }));
        var b = SpecHasher.Compute(BaseSpec(volumes: new[]
        {
            new ResolvedVolume { Id = "a", MountPath = "/a", Size = "1Gi", Criticality = VolumeCriticality.Cache, ResourceName = "pvc-a", StorageClassName = "fast-ssd" },
        }));
        b.Should().Be(a, "a default-class change is informational and must not poison the drift loop");
    }

    [Fact]
    public void Rotating_the_token_secret_value_but_not_its_ref_yields_the_same_hash()
    {
        var a = SpecHasher.Compute(BaseSpec(env: new[]
        {
            new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "reeve-wl-token" },
        }));
        var b = SpecHasher.Compute(BaseSpec(env: new[]
        {
            new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "reeve-wl-token" },
        }));
        b.Should().Be(a);
    }

    [Fact]
    public void The_self_referential_spec_hash_env_value_does_not_feed_the_hash()
    {
        var withEmpty = BaseSpec(env: new[]
        {
            new ResolvedEnvVar { Name = "REEVE_SPEC_HASH", Kind = ResolvedEnvKind.SpecHash, Value = null },
        });
        var withValue = BaseSpec(env: new[]
        {
            new ResolvedEnvVar { Name = "REEVE_SPEC_HASH", Kind = ResolvedEnvKind.SpecHash, Value = "deadbeef" },
        });
        SpecHasher.Compute(withValue).Should().Be(SpecHasher.Compute(withEmpty));
    }

    [Fact]
    public void Changing_the_secret_ref_name_does_change_the_hash()
    {
        var a = SpecHasher.Compute(BaseSpec(env: new[]
        {
            new ResolvedEnvVar { Name = "T", Kind = ResolvedEnvKind.SecretRef, SecretRef = "secret-a" },
        }));
        var b = SpecHasher.Compute(BaseSpec(env: new[]
        {
            new ResolvedEnvVar { Name = "T", Kind = ResolvedEnvKind.SecretRef, SecretRef = "secret-b" },
        }));
        b.Should().NotBe(a);
    }

    private static WorkloadManifest Manifest(string digest = Digest)
        => new()
        {
            Id = "matter",
            DisplayName = "Matter",
            Image = new WorkloadImage { Ref = "ghcr.io/reeve/matter", Digest = digest, Platforms = new[] { "linux/amd64" } },
            Kind = WorkloadKind.SingleNode,
            Privileges = new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } } },
            Parameters = new[] { new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath } },
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Env = new[] { new WorkloadEnv { Name = "REEVE_WORKLOAD_TOKEN", From = "bootstrap:workloadToken" } },
        };

    private static WorkloadDesiredSpec Factory(
        WorkloadManifest wl, string devicePath = "/dev/ttyUSB0",
        IReadOnlyList<RegistryMirror>? mirrors = null, string? tokenRef = "reeve-wl-token")
    {
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["serialPort"] = devicePath }, StandInProviders.Kubernetes);
        var settings = new WorkloadOrchestratorSettings { Mirrors = mirrors ?? Array.Empty<RegistryMirror>() };
        var bootstrap = new WorkloadBootstrapContext
        {
            InstanceId = InstanceId,
            SecretName = "reeve-wl-token",
            Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal)
            {
                ["workloadToken"] = BootstrapValue.SecretRef(tokenRef),
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

    [Fact]
    public void Changing_the_image_digest_changes_the_hash()
    {
        Factory(Manifest(Digest2)).SpecHash.Should().NotBe(Factory(Manifest(Digest)).SpecHash);
    }

    [Fact]
    public void Changing_a_resolved_parameter_value_changes_the_hash()
    {
        Factory(Manifest(), devicePath: "/dev/ttyUSB1").SpecHash
            .Should().NotBe(Factory(Manifest(), devicePath: "/dev/ttyUSB0").SpecHash);
    }

    [Fact]
    public void A_mirror_that_rewrites_the_host_changes_the_hash()
    {
        var mirrored = Factory(Manifest(), mirrors: new[]
        {
            new RegistryMirror { MatchRegistry = "ghcr.io", MirrorRegistry = "mirror.example" },
        });
        mirrored.SpecHash.Should().NotBe(Factory(Manifest()).SpecHash);
    }

    [Fact]
    public void A_token_rotation_does_not_change_the_hash_end_to_end()
    {
        Factory(Manifest(), tokenRef: "reeve-wl-token").SpecHash
            .Should().Be(Factory(Manifest(), tokenRef: "reeve-wl-token").SpecHash);
    }

    [Fact]
    public void Recomputing_the_same_inputs_is_deterministic()
    {
        Factory(Manifest()).SpecHash.Should().Be(Factory(Manifest()).SpecHash);
    }

    [Fact]
    public void The_hash_is_lowercase_hex_with_no_sha256_prefix()
    {
        var hash = Factory(Manifest()).SpecHash;
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    private static WorkloadDesiredSpec NetworkSpec(IReadOnlyList<string>? attachments = null)
        => BaseSpec() with
        {
            Network = new ResolvedNetwork
            {
                HostNetwork = true,
                Mdns = true,
                Ipv6Required = false,
                Attachments = attachments ?? [],
                Ports =
                [
                    new ResolvedPort { Name = "api", Port = 8080, Protocol = "TCP" },
                    new ResolvedPort { Name = "mdns", Port = 5353, Protocol = "UDP" },
                ],
            },
        };

    private const string NetworkSpecHash = "6508aeefafed9d094a03aaa79c9fff9fadd218a0ed761aecd5efe7b9e567e5c4";

    [Fact]
    public void A_spec_without_attachments_hashes_byte_identically()
    {
        SpecHasher.Compute(NetworkSpec()).Should().Be(NetworkSpecHash);
    }

    private const string NetworkSpecCanonicalBody = """
        {"pluginTypeId":"com.reeve.x","workloadId":"w","inst8":"11111111","provider":"kubernetes","image":"ghcr.io/x@sha256:1111111111111111111111111111111111111111111111111111111111111111","platforms":["linux/amd64","linux/arm64"],"kind":"single-node","nodeSelector":{},"network":{"hostNetwork":true,"mdns":true,"ipv6Required":false,"ports":[{"name":"api","port":8080,"protocol":"TCP"},{"name":"mdns","port":5353,"protocol":"UDP"}]},"privileges":{"capabilities":["NET_ADMIN","NET_RAW"],"privileged":false,"runAsRoot":false,"tunDevice":false,"dbusHostPath":null,"devicePaths":["/dev/ttyUSB0","/dev/ttyUSB1"]},"volumes":[{"id":"a","mountPath":"/a","size":"1Gi","criticality":"cache","accessMode":"rwo"},{"id":"b","mountPath":"/b","size":"2Gi","criticality":"critical","accessMode":"rwo"}],"configMapMounts":[],"health":{"mode":"container","heartbeatSeconds":0},"env":[{"name":"A","kind":"literal","value":"1"},{"name":"B","kind":"literal","value":"2"}]}
        """;

    [Fact]
    public void The_pinned_hash_is_the_hash_of_that_pinned_body()
    {
        SpecHasher.Compute(NetworkSpec()).Should().Be(Sha256Hex(NetworkSpecCanonicalBody));
    }

    [Fact]
    public void The_base_hash_is_that_body_plus_the_two_evicted_members_and_nothing_else()
    {
        const string beforeTheEviction = "b16691885f9ebf7dbc16cbd4edccd6387689d95fcc9c054c6e327b7b150ce02e";
        var restored = NetworkSpecCanonicalBody.Replace(
            "\"health\":{\"mode\":\"container\",\"heartbeatSeconds\":0}",
            "\"health\":{\"mode\":\"container\",\"adapterType\":null,\"heartbeatSeconds\":0},\"protocol\":{\"version\":\"1\",\"minSupported\":\"1\"}",
            StringComparison.Ordinal);

        restored.Should().NotBe(NetworkSpecCanonicalBody, "the members must be re-inserted where they were written");
        Sha256Hex(restored).Should().Be(beforeTheEviction);
    }

    private static string Sha256Hex(string canonical)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    [Fact]
    public void Reordering_attachments_changes_the_hash()
    {
        var a = SpecHasher.Compute(NetworkSpec(["iot-macvlan", "cams"]));
        var b = SpecHasher.Compute(NetworkSpec(["cams", "iot-macvlan"]));

        b.Should().NotBe(a, "attachment order is materialization-relevant and must never be sorted away");
    }

    [Fact]
    public void Adding_an_attachment_changes_the_hash()
    {
        SpecHasher.Compute(NetworkSpec(["iot-macvlan"]))
            .Should().NotBe(SpecHasher.Compute(NetworkSpec()));
    }
}
