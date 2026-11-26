using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadDesiredSpecFactoryTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid PluginInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string Digest = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";
    private const string TokenSecret = "reeve-wl-token-secret";

    private static WorkloadOrchestratorSettings Settings(IReadOnlyList<RegistryMirror>? mirrors = null)
        => new() { Mirrors = mirrors ?? Array.Empty<RegistryMirror>() };

    private static WorkloadBootstrapContext Bootstrap(
        IReadOnlyDictionary<string, string>? siblings = null,
        string? appVersion = null,
        IReadOnlyList<ResolvedEnvVar>? hostEnv = null,
        string? mqttBrokerUrl = null,
        string? mqttUsername = null,
        string? mqttPassword = null,
        string secretName = TokenSecret,
        IReadOnlyList<ProvisionedCredential>? provisioned = null)
    {
        var values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal)
        {
            ["gatewayUrl"] = BootstrapValue.Literal($"wss://reeve.local/ws/workloads/{InstanceId:D}"),
            ["backendBaseUrl"] = BootstrapValue.Literal("https://reeve.local"),
            ["instanceId"] = BootstrapValue.Literal(InstanceId.ToString("D")),
            ["workloadToken"] = BootstrapValue.HostCredential("workload-token"),
            ["mqttBrokerUrl"] = BootstrapValue.Literal(mqttBrokerUrl),
            ["mqttUsername"] = BootstrapValue.Literal(mqttUsername),
            ["mqttPassword"] = string.IsNullOrEmpty(mqttPassword)
                ? BootstrapValue.SecretRef(null)
                : BootstrapValue.SecretRef(TokenSecret, mqttPassword),
            ["specHash"] = BootstrapValue.SpecHash,
        };

        foreach (var (siblingWorkloadId, host) in siblings ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            values[$"workloadHost:{siblingWorkloadId}"] = BootstrapValue.Literal(host);
        }

        return new WorkloadBootstrapContext
        {
            InstanceId = InstanceId,
            Values = values,
            AppVersion = appVersion,
            HostEnv = hostEnv ?? [],
            SecretName = secretName,
            ProvisionedCredentials = provisioned ?? [],
        };
    }

    private static WorkloadManifest MqttWorkload()
        => new()
        {
            Id = "discovery-host",
            DisplayName = "Discovery Host",
            Image = new WorkloadImage { Ref = "ghcr.io/reeve/discovery-host", Digest = Digest, Platforms = new[] { "linux/amd64" } },
            Kind = WorkloadKind.Service,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Env = new[]
            {
                new WorkloadEnv { Name = "Discovery__Host__Mqtt__BrokerUrl", From = "bootstrap:mqttBrokerUrl" },
                new WorkloadEnv { Name = "Discovery__Host__Mqtt__UserName", From = "bootstrap:mqttUsername" },
                new WorkloadEnv { Name = "Discovery__Host__Mqtt__Password", From = "bootstrap:mqttPassword" },
                new WorkloadEnv { Name = "REEVE_WORKLOAD_TOKEN", From = "bootstrap:workloadToken" },
            },
        };

    private static WorkloadManifest MatterLike(IReadOnlyList<WorkloadEnv>? env = null)
        => new()
        {
            Id = "matter",
            DisplayName = "Matter",
            Image = new WorkloadImage
            {
                Ref = "ghcr.io/reeve/matter",
                Digest = Digest,
                Platforms = new[] { "linux/arm64", "linux/amd64" },
            },
            Kind = WorkloadKind.SingleNode,
            Network = new WorkloadNetwork { Mdns = true },
            Privileges = new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } },
            },
            Parameters = new[]
            {
                new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath },
            },
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Env = env ?? new[]
            {
                new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" },
                new WorkloadEnv { Name = "REEVE_WORKLOAD_TOKEN", From = "bootstrap:workloadToken" },
            },
        };

    private static WorkloadDesiredSpec Compute(
        WorkloadManifest wl,
        WorkloadOrchestratorSettings? settings = null,
        IReadOnlyDictionary<string, string?>? resolved = null,
        WorkloadBootstrapContext? bootstrap = null,
        WorkloadProviderDescriptor? provider = null,
        IWorkloadResourceNamer? namer = null)
    {
        provider ??= StandInProviders.Kubernetes;
        resolved ??= WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" },
            provider);
        return WorkloadDesiredSpecFactory.Compute(
            wl,
            settings ?? Settings(),
            resolved,
            bootstrap ?? Bootstrap(),
            provider,
            namer ?? DefaultWorkloadResourceNamer.Instance,
            PluginInstanceId,
            "com.reeve.matter",
            InstanceId,
            "install-1");
    }

    private static WorkloadDesiredSpec ComputeWith(WorkloadManifest wl, IReadOnlyDictionary<string, string?> values,
        WorkloadProviderDescriptor? provider = null)
    {
        provider ??= StandInProviders.Kubernetes;
        return Compute(wl, resolved: WorkloadParameterResolver.Resolve(wl, values, provider), provider: provider);
    }

    [Fact]
    public void A_user_namespace_lands_on_the_spec_without_creating_a_privileges_block()
    {
        var spec = ComputeWith(MqttWorkload(), new Dictionary<string, string?> { ["security.userNamespace"] = "true" });
        spec.UserNamespace.Should().BeTrue();
        spec.UserNamespaceSkippedForHostNetwork.Should().BeFalse();
        spec.Privileges.Should().BeNull();
    }

    [Fact]
    public void A_user_namespace_requested_on_a_host_network_is_skipped_and_reported()
    {
        var wl = MqttWorkload() with { Network = new WorkloadNetwork { HostNetwork = true } };
        var on = ComputeWith(wl, new Dictionary<string, string?> { ["security.userNamespace"] = "true" });
        var off = ComputeWith(wl, new Dictionary<string, string?>());
        var movedToCluster = ComputeWith(wl, new Dictionary<string, string?>
        {
            ["security.userNamespace"] = "true",
            ["network.mode"] = "cluster",
        });
        on.UserNamespace.Should().BeFalse();
        on.UserNamespaceSkippedForHostNetwork.Should().BeTrue();
        on.SpecHash.Should().Be(off.SpecHash);
        movedToCluster.UserNamespace.Should().BeTrue();
        movedToCluster.UserNamespaceSkippedForHostNetwork.Should().BeFalse();
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("manual")]
    public void A_stored_user_namespace_on_a_provider_without_the_trait_is_ignored(string providerId)
    {
        var provider = providerId == "docker" ? StandInProviders.Docker : StandInProviders.Manual;
        var spec = Compute(MqttWorkload(), resolved: new Dictionary<string, string?>
        {
            ["security.userNamespace"] = "true",
        }, provider: provider);
        spec.UserNamespace.Should().BeFalse();
        spec.UserNamespaceSkippedForHostNetwork.Should().BeFalse();
    }

    [Fact]
    public void Turning_the_user_namespace_on_moves_the_spec_hash_and_off_restores_it()
    {
        var wl = MqttWorkload();
        var off = ComputeWith(wl, new Dictionary<string, string?>());
        var on = ComputeWith(wl, new Dictionary<string, string?> { ["security.userNamespace"] = "true" });
        var explicitOff = ComputeWith(wl, new Dictionary<string, string?> { ["security.userNamespace"] = "false" });
        on.SpecHash.Should().NotBe(off.SpecHash);
        explicitOff.SpecHash.Should().Be(off.SpecHash);
    }

    [Fact]
    public void Declared_privileges_are_unchanged_by_the_user_namespace_switch()
    {
        var wl = MatterLike();
        var offValues = new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" };
        var onValues = new Dictionary<string, string?>(offValues) { ["security.userNamespace"] = "true" };
        ComputeWith(wl, onValues).Privileges.Should().BeEquivalentTo(ComputeWith(wl, offValues).Privileges);
    }


    private static WorkloadManifest MatterLikeWithExtraEnv()
        => MatterLike(
        [
            new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" },
            new WorkloadEnv { Name = "EXTRA", From = "bootstrap:extra" },
        ]);

    private static WorkloadBootstrapContext WithExtra(BootstrapValue value)
    {
        var bootstrap = Bootstrap();
        return bootstrap with
        {
            Values = new Dictionary<string, BootstrapValue>(bootstrap.Values, StringComparer.Ordinal) { ["extra"] = value },
        };
    }

    [Fact]
    public void An_unhashed_literal_renders_a_literal_env_carrying_the_flag_and_its_value()
    {
        var spec = Compute(MatterLikeWithExtraEnv(), bootstrap: WithExtra(BootstrapValue.UnhashedLiteral("first")));

        var extra = spec.Env.Single(e => e.Name == "EXTRA");
        extra.Kind.Should().Be(ResolvedEnvKind.Literal);
        extra.Value.Should().Be("first");
        extra.ExcludedFromSpecHash.Should().BeTrue();
    }

    [Fact]
    public void An_unhashed_literal_prints_differently_from_the_literal_it_is_not_equal_to()
    {
        var plain = BootstrapValue.Literal("x");
        var unhashed = BootstrapValue.UnhashedLiteral("x");

        plain.Should().NotBe(unhashed);
        plain.ToString().Should().NotBe(unhashed.ToString());
        unhashed.ToString().Should().Contain("ExcludedFromSpecHash");
    }

    [Fact]
    public void An_unhashed_literal_reports_the_literal_kind()
        => BootstrapValue.UnhashedLiteral("v").Kind.Should().Be(ResolvedEnvKind.Literal);

    [Theory]
    [InlineData("first")]
    [InlineData(null)]
    public void Every_other_env_leaves_the_flag_off(string? value)
    {
        var spec = Compute(MatterLikeWithExtraEnv(), bootstrap: WithExtra(BootstrapValue.Literal(value)));

        spec.Env.Should().OnlyContain(e => !e.ExcludedFromSpecHash);
    }

    [Fact]
    public void Changing_only_an_unhashed_literals_value_keeps_the_spec_hash_but_reaches_the_env()
    {
        var first = Compute(MatterLikeWithExtraEnv(), bootstrap: WithExtra(BootstrapValue.UnhashedLiteral("first")));
        var second = Compute(MatterLikeWithExtraEnv(), bootstrap: WithExtra(BootstrapValue.UnhashedLiteral("second")));

        second.SpecHash.Should().Be(first.SpecHash);
        first.Env.Single(e => e.Name == "EXTRA").Value.Should().Be("first");
        second.Env.Single(e => e.Name == "EXTRA").Value.Should().Be("second");
    }

    [Fact]
    public void An_unhashed_literal_leaves_the_hash_of_a_spec_that_does_not_declare_it()
    {
        var declared = Compute(MatterLikeWithExtraEnv(), bootstrap: WithExtra(BootstrapValue.UnhashedLiteral("first")));
        var absent = Compute(MatterLike([new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" }]));

        declared.SpecHash.Should().Be(absent.SpecHash);
    }

    [Fact]
    public void An_ordinary_literal_still_moves_the_spec_hash_when_its_value_changes()
    {
        var first = Compute(MatterLikeWithExtraEnv(), bootstrap: WithExtra(BootstrapValue.Literal("first")));
        var second = Compute(MatterLikeWithExtraEnv(), bootstrap: WithExtra(BootstrapValue.Literal("second")));

        second.SpecHash.Should().NotBe(first.SpecHash);
    }

    [Fact]
    public void An_empty_ordinary_literal_moves_the_spec_hash_against_the_env_being_absent()
    {
        var empty = Compute(MatterLikeWithExtraEnv(), bootstrap: WithExtra(BootstrapValue.Literal("")));
        var absent = Compute(MatterLike([new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" }]));

        empty.SpecHash.Should().NotBe(absent.SpecHash);
    }

    [Fact]
    public void A_flagged_host_env_stays_flagged_and_outside_the_hash()
    {
        var flagged = new ResolvedEnvVar { Name = "HOST_FLAGGED", Kind = ResolvedEnvKind.Literal, Value = "v", ExcludedFromSpecHash = true };

        var with = Compute(MatterLike(), bootstrap: Bootstrap(hostEnv: [flagged]));
        var without = Compute(MatterLike());

        with.Env.Should().Contain(flagged);
        with.SpecHash.Should().Be(without.SpecHash);
    }

    [Fact]
    public void A_credential_with_no_secret_to_name_still_renders_its_env_because_the_preview_has_none()
    {
        var spec = Compute(MatterLike(), bootstrap: Bootstrap(secretName: string.Empty));

        var token = spec.Env.Should().Contain(e => e.Name == "REEVE_WORKLOAD_TOKEN").Which;
        token.Kind.Should().Be(ResolvedEnvKind.SecretRef);
        token.SecretRef.Should().BeNull("a blank secret name renders no ref name, as the preview always has");
        token.CredentialId.Should().Be("workload-token");
    }

    [Fact]
    public void An_unbacked_PLAIN_ref_drops_its_env()
    {
        var spec = Compute(MqttWorkload(), bootstrap: Bootstrap(mqttPassword: null, secretName: string.Empty));

        spec.Env.Should().NotContain(e => e.Name == "Discovery__Host__Mqtt__Password");
        spec.Env.Should().Contain(e => e.Name == "REEVE_WORKLOAD_TOKEN",
            "only a credential is exempt from the drop");
    }

    [Fact]
    public void A_host_credential_renders_a_SecretRef_to_the_context_secret_carrying_its_id()
    {
        var values = new Dictionary<string, BootstrapValue>(Bootstrap().Values, StringComparer.Ordinal)
        {
            ["workloadToken"] = BootstrapValue.HostCredential("tok"),
        };

        var spec = Compute(MatterLike(), bootstrap: Bootstrap(secretName: "per-instance-secret") with { Values = values });

        var token = spec.Env.Single(e => e.Name == "REEVE_WORKLOAD_TOKEN");
        token.Kind.Should().Be(ResolvedEnvKind.SecretRef);
        token.SecretRef.Should().Be("per-instance-secret");
        token.CredentialId.Should().Be("tok");
        token.Value.Should().BeNull();
        spec.SecretMaterial.Should().NotContainKey("REEVE_WORKLOAD_TOKEN", "the provider resolves a credential by id");
    }

    [Fact]
    public void A_plain_secret_ref_carries_no_credential_id()
    {
        var spec = Compute(MqttWorkload(), bootstrap: Bootstrap(mqttPassword: "s3cr3t"));

        spec.Env.Single(e => e.Name == "Discovery__Host__Mqtt__Password").CredentialId.Should().BeNull();
    }

    [Fact]
    public void A_host_env_replacing_a_declared_password_env_drops_its_material()
    {
        var wl = MatterLike(env: new[]
        {
            new WorkloadEnv { Name = "DB_PASSWORD", From = "parameter:dbPassword" },
        }) with
        {
            Parameters = new[]
            {
                new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath },
                new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password },
            },
        };
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["dbPassword"] = "hunter2",
        };

        var declared = Compute(wl, resolved: resolved);
        var forced = Compute(wl, resolved: resolved, bootstrap: Bootstrap(hostEnv: HostEnv("DB_PASSWORD", "forced")));

        declared.SecretMaterial.Should().ContainKey("DB_PASSWORD", "the control: the declared env carries material");
        var env = forced.Env.Single(e => e.Name == "DB_PASSWORD");
        env.Kind.Should().Be(ResolvedEnvKind.Literal);
        env.Value.Should().Be("forced");
        forced.SecretMaterial.Should().NotContainKey("DB_PASSWORD");
    }

    [Fact]
    public void A_password_reference_changes_the_hash_but_its_resolved_value_does_not()
    {
        var wl = MatterLike(env: [new WorkloadEnv { Name = "DB_PASSWORD", From = "parameter:dbPassword" }]) with
        {
            Parameters = [
                new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath },
                new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password },
            ],
        };
        var parameters = new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["dbPassword"] = "resolved-one" };
        var a = WorkloadDesiredSpecFactory.ComputeWithParameterReferences(
            wl, Settings(), parameters, new Dictionary<string, string?> { ["dbPassword"] = "ref:provider:a" },
            Bootstrap(), StandInProviders.Kubernetes, DefaultWorkloadResourceNamer.Instance,
            PluginInstanceId, "com.reeve.matter", InstanceId, "install-1");
        var sameReferenceNewValue = WorkloadDesiredSpecFactory.ComputeWithParameterReferences(
            wl, Settings(), new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["dbPassword"] = "resolved-two" },
            new Dictionary<string, string?> { ["dbPassword"] = "ref:provider:a" },
            Bootstrap(), StandInProviders.Kubernetes, DefaultWorkloadResourceNamer.Instance,
            PluginInstanceId, "com.reeve.matter", InstanceId, "install-1");
        var changedReference = WorkloadDesiredSpecFactory.ComputeWithParameterReferences(
            wl, Settings(), parameters, new Dictionary<string, string?> { ["dbPassword"] = "ref:provider:b" },
            Bootstrap(), StandInProviders.Kubernetes, DefaultWorkloadResourceNamer.Instance,
            PluginInstanceId, "com.reeve.matter", InstanceId, "install-1");

        sameReferenceNewValue.SpecHash.Should().Be(a.SpecHash);
        changedReference.SpecHash.Should().NotBe(a.SpecHash);
        a.SecretMaterial["DB_PASSWORD"].Should().Be("resolved-one");
        sameReferenceNewValue.SecretMaterial["DB_PASSWORD"].Should().Be("resolved-two");
    }

    [Fact]
    public void The_contexts_provisioned_credentials_land_on_the_spec()
    {
        var provisioned = new[] { new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN") };

        var spec = Compute(MatterLike(), bootstrap: Bootstrap(provisioned: provisioned));

        spec.ProvisionedCredentials.Should().Equal(provisioned);
    }

    [Fact]
    public void Neither_a_credential_id_nor_the_provisioned_credentials_move_the_spec_hash()
    {
        var one = Compute(MatterLike(), bootstrap: Bootstrap() with
        {
            Values = new Dictionary<string, BootstrapValue>(Bootstrap().Values, StringComparer.Ordinal)
            {
                ["workloadToken"] = BootstrapValue.HostCredential("id-one"),
            },
        });
        var two = Compute(MatterLike(), bootstrap: Bootstrap(
            provisioned: [new ProvisionedCredential("id-two", "REEVE_WORKLOAD_TOKEN")]) with
        {
            Values = new Dictionary<string, BootstrapValue>(Bootstrap().Values, StringComparer.Ordinal)
            {
                ["workloadToken"] = BootstrapValue.HostCredential("id-two"),
            },
        });

        one.Env.Single(e => e.Name == "REEVE_WORKLOAD_TOKEN").CredentialId.Should().Be("id-one");
        two.Env.Single(e => e.Name == "REEVE_WORKLOAD_TOKEN").CredentialId.Should().Be("id-two");
        two.ProvisionedCredentials.Should().NotBeEmpty();
        two.SpecHash.Should().Be(one.SpecHash);
    }

    [Fact]
    public void Image_is_ref_at_digest_after_mirror_rewrite()
    {
        var spec = Compute(MatterLike(), settings: Settings(
            mirrors: new[] { new RegistryMirror { MatchRegistry = "ghcr.io", MirrorRegistry = "mirror.example.com" } }));

        spec.Image.Should().Be($"mirror.example.com/reeve/matter@{Digest}");
    }

    [Fact]
    public void Image_digest_is_preserved_with_no_mirror()
    {
        var spec = Compute(MatterLike());
        spec.Image.Should().Be($"ghcr.io/reeve/matter@{Digest}");
    }


    private static WorkloadManifest ComponentWorkload(string component)
        => new()
        {
            Id = "vd",
            DisplayName = "Virtual Devices",
            Image = new WorkloadImage { Component = component },
            Kind = WorkloadKind.Service,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        };

    private static ProductImageOptions ProductImages(
        string component, string? @ref, string? tag = null, string? digest = null)
        => new()
        {
            Components =
            {
                [component] = new ProductImageEntry { Ref = @ref, Tag = tag, Digest = digest },
            },
        };

    private static WorkloadOrchestratorSettings SettingsWith(
        bool allowUnpinnedImages = false, IReadOnlyList<RegistryMirror>? mirrors = null)
        => new()
        {
            Mirrors = mirrors ?? Array.Empty<RegistryMirror>(),
            AllowUnpinnedImages = allowUnpinnedImages,
        };

    private static WorkloadDesiredSpec ComputeComponent(
        WorkloadManifest wl, WorkloadOrchestratorSettings settings, ProductImageOptions? productImages)
        => WorkloadDesiredSpecFactory.Compute(
            wl,
            settings,
            new Dictionary<string, string?>(StringComparer.Ordinal),
            Bootstrap(),
            StandInProviders.Kubernetes,
            DefaultWorkloadResourceNamer.Instance,
            PluginInstanceId,
            "com.reeve.virtual-devices",
            InstanceId,
            "install-1",
            productImages);

    [Fact]
    public void Component_with_configured_digest_resolves_to_concrete_ref_at_digest_mirror_rewritten()
    {
        var settings = SettingsWith(
            mirrors: new[] { new RegistryMirror { MatchRegistry = "ghcr.io", MirrorRegistry = "mirror.example.com" } });
        var images = ProductImages("virtual-devices", @ref: "ghcr.io/reeve/virtual-devices", tag: "1.4.0", digest: Digest);

        var spec = ComputeComponent(ComponentWorkload("virtual-devices"), settings, images);

        spec.Image.Should().Be($"mirror.example.com/reeve/virtual-devices@{Digest}");
    }

    [Fact]
    public void Component_without_digest_resolves_to_tag_only_when_unpinned_images_are_allowed()
    {
        var settings = SettingsWith(allowUnpinnedImages: true);
        var images = ProductImages("virtual-devices", @ref: "registry.example.com/reeve/virtual-devices", tag: "1.4.0");

        var spec = ComputeComponent(ComponentWorkload("virtual-devices"), settings, images);

        spec.Image.Should().Be("registry.example.com/reeve/virtual-devices:1.4.0");
    }

    [Fact]
    public void Component_without_digest_is_rejected_when_unpinned_images_are_not_allowed()
    {
        var settings = SettingsWith(allowUnpinnedImages: false);
        var images = ProductImages("virtual-devices", @ref: "registry.example.com/reeve/virtual-devices", tag: "1.4.0");

        var act = () => ComputeComponent(ComponentWorkload("virtual-devices"), settings, images);

        act.Should().Throw<WorkloadImageResolutionException>().WithMessage("*allowUnpinnedImages*");
    }

    [Fact]
    public void Component_with_no_matching_product_image_config_entry_fails_closed()
    {
        var settings = SettingsWith(allowUnpinnedImages: true);
        var images = ProductImages("discovery-host", @ref: "registry.example.com/reeve/discovery-host", tag: "1.4.0");

        var act = () => ComputeComponent(ComponentWorkload("virtual-devices"), settings, images);

        act.Should().Throw<WorkloadImageResolutionException>();
    }

    [Fact]
    public void An_external_or_non_bundled_workload_cannot_resolve_a_component()
    {
        var settings = SettingsWith(allowUnpinnedImages: true);

        var act = () => ComputeComponent(ComponentWorkload("virtual-devices"), settings, productImages: new ProductImageOptions());

        act.Should().Throw<WorkloadImageResolutionException>();
    }

    [Fact]
    public void An_external_ref_digest_image_still_resolves_when_no_product_images_are_supplied()
    {
        var spec = WorkloadDesiredSpecFactory.Compute(
            MatterLike(),
            Settings(),
            WorkloadParameterResolver.Resolve(MatterLike(), new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, StandInProviders.Kubernetes),
            Bootstrap(),
            StandInProviders.Kubernetes,
            DefaultWorkloadResourceNamer.Instance,
            PluginInstanceId,
            "com.reeve.matter",
            InstanceId,
            "install-1");

        spec.Image.Should().Be($"ghcr.io/reeve/matter@{Digest}");
    }


    [Fact]
    public void Identity_carries_inst8_and_resolved_names()
    {
        var spec = Compute(MatterLike());

        spec.Inst8.Should().Be(WorkloadInstance.ComputeInst8(InstanceId));
        spec.ResourceName.Should().StartWith("reeve-wl-com-reeve-matter-matter-");
        spec.SecretName.Should().Be(spec.ResourceName);
        spec.Labels.Should().ContainKey(WorkloadResourceLabels.SpecHashLabelKey);
        spec.SpecHash.Length.Should().Be(64);
        spec.Labels[WorkloadResourceLabels.SpecHashLabelKey].Should()
            .Be(WorkloadResourceNaming.TruncateForDns(WorkloadResourceNaming.Sanitize(spec.SpecHash)));
    }


    [Fact]
    public void Gateway_url_env_composes_the_full_per_instance_ws_endpoint_from_the_base()
    {
        var spec = Compute(MatterLike());
        var e = spec.Env.Single(x => x.Name == "REEVE_GATEWAY_URL");
        e.Kind.Should().Be(ResolvedEnvKind.Literal);
        e.Value.Should().Be($"wss://reeve.local/ws/workloads/{InstanceId:D}");
    }

    [Fact]
    public void Workload_token_env_is_a_secret_reference_never_a_literal_value()
    {
        var spec = Compute(MatterLike());
        var e = spec.Env.Single(x => x.Name == "REEVE_WORKLOAD_TOKEN");
        e.Kind.Should().Be(ResolvedEnvKind.SecretRef);
        e.SecretRef.Should().Be("reeve-wl-token-secret");
        e.Value.Should().BeNull();
    }


    [Fact]
    public void Mqtt_broker_url_and_username_resolve_as_literal_env_from_the_broker_config()
    {
        var spec = Compute(MqttWorkload(), bootstrap: Bootstrap(
            mqttBrokerUrl: "mqtt://reeve-mosquitto:1883", mqttUsername: "reeve", mqttPassword: "s3cr3t"));

        var url = spec.Env.Single(e => e.Name == "Discovery__Host__Mqtt__BrokerUrl");
        url.Kind.Should().Be(ResolvedEnvKind.Literal);
        url.Value.Should().Be("mqtt://reeve-mosquitto:1883");

        var user = spec.Env.Single(e => e.Name == "Discovery__Host__Mqtt__UserName");
        user.Kind.Should().Be(ResolvedEnvKind.Literal);
        user.Value.Should().Be("reeve");
    }

    [Fact]
    public void Mqtt_password_env_is_a_secret_ref_never_a_literal_pod_env_value()
    {
        var spec = Compute(MqttWorkload(), bootstrap: Bootstrap(
            mqttBrokerUrl: "mqtt://reeve-mosquitto:1883", mqttUsername: "reeve", mqttPassword: "s3cr3t"));

        var pw = spec.Env.Single(e => e.Name == "Discovery__Host__Mqtt__Password");
        pw.Kind.Should().Be(ResolvedEnvKind.SecretRef);
        pw.Value.Should().BeNull("a secret value must never appear as a plaintext env literal");
        pw.SecretRef.Should().Be("reeve-wl-token-secret");
    }

    [Fact]
    public void The_real_mqtt_password_lands_in_the_per_instance_secret_material_not_the_pod_env()
    {
        var spec = Compute(MqttWorkload(), bootstrap: Bootstrap(
            mqttBrokerUrl: "mqtt://reeve-mosquitto:1883", mqttUsername: "reeve", mqttPassword: "s3cr3t"));

        spec.SecretMaterial.Should().ContainKey("Discovery__Host__Mqtt__Password")
            .WhoseValue.Should().Be("s3cr3t");
        spec.Env.Should().NotContain(e => e.Value == "s3cr3t");
    }

    [Fact]
    public void Anonymous_broker_omits_the_mqtt_password_env_entirely()
    {
        var spec = Compute(MqttWorkload(), bootstrap: Bootstrap(
            mqttBrokerUrl: "mqtt://reeve-mosquitto:1883", mqttUsername: "reeve", mqttPassword: null));

        spec.Env.Should().NotContain(e => e.Name == "Discovery__Host__Mqtt__Password");
        spec.SecretMaterial.Should().NotContainKey("Discovery__Host__Mqtt__Password");
    }

    [Fact]
    public void Empty_broker_password_omits_the_mqtt_password_env_entirely()
    {
        var spec = Compute(MqttWorkload(), bootstrap: Bootstrap(
            mqttBrokerUrl: "mqtt://reeve-mosquitto:1883", mqttUsername: "reeve", mqttPassword: string.Empty));

        spec.Env.Should().NotContain(e => e.Name == "Discovery__Host__Mqtt__Password");
        spec.SecretMaterial.Should().NotContainKey("Discovery__Host__Mqtt__Password");
    }

    [Fact]
    public void Anonymous_broker_still_emits_the_url_and_username_and_token_env()
    {
        var spec = Compute(MqttWorkload(), bootstrap: Bootstrap(
            mqttBrokerUrl: "mqtt://reeve-mosquitto:1883", mqttUsername: "reeve", mqttPassword: null));

        var url = spec.Env.Single(e => e.Name == "Discovery__Host__Mqtt__BrokerUrl");
        url.Kind.Should().Be(ResolvedEnvKind.Literal);
        url.Value.Should().Be("mqtt://reeve-mosquitto:1883");

        var user = spec.Env.Single(e => e.Name == "Discovery__Host__Mqtt__UserName");
        user.Kind.Should().Be(ResolvedEnvKind.Literal);
        user.Value.Should().Be("reeve");

        var token = spec.Env.Single(e => e.Name == "REEVE_WORKLOAD_TOKEN");
        token.Kind.Should().Be(ResolvedEnvKind.SecretRef);
        token.SecretRef.Should().Be("reeve-wl-token-secret");
    }

    [Fact]
    public void Rotating_only_the_mqtt_password_value_does_not_change_the_spec_hash()
    {
        var a = Compute(MqttWorkload(), bootstrap: Bootstrap(
            mqttBrokerUrl: "mqtt://reeve-mosquitto:1883", mqttUsername: "reeve", mqttPassword: "old-password"));
        var b = Compute(MqttWorkload(), bootstrap: Bootstrap(
            mqttBrokerUrl: "mqtt://reeve-mosquitto:1883", mqttUsername: "reeve", mqttPassword: "brand-new-password"));

        b.SpecHash.Should().Be(a.SpecHash);
    }

    [Fact]
    public void Every_workload_implicitly_gets_REEVE_SPEC_HASH_env_equal_to_the_spec_hash()
    {
        var spec = Compute(MatterLike());
        var e = spec.Env.Single(x => x.Name == "REEVE_SPEC_HASH");
        e.Kind.Should().Be(ResolvedEnvKind.SpecHash);
        e.Value.Should().Be(spec.SpecHash);
    }

    [Fact]
    public void A_parameter_env_resolving_blank_is_omitted_not_emitted_as_empty()
    {
        var wl = MatterLike(env: new[]
        {
            new WorkloadEnv { Name = "Bluetooth__MinRssiDbm", From = "parameter:minRssiDbm" },
            new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" },
            new WorkloadEnv { Name = "REEVE_WORKLOAD_TOKEN", From = "bootstrap:workloadToken" },
        });

        var spec = Compute(wl, resolved: new Dictionary<string, string?>(StringComparer.Ordinal) { ["minRssiDbm"] = "" });

        spec.Env.Should().NotContain(x => x.Name == "Bluetooth__MinRssiDbm");
        spec.Env.Should().Contain(x => x.Name == "REEVE_GATEWAY_URL");
    }

    [Fact]
    public void An_author_declared_spec_hash_env_is_not_duplicated()
    {
        var wl = MatterLike(env: new[]
        {
            new WorkloadEnv { Name = "REEVE_SPEC_HASH", From = "bootstrap:specHash" },
        });
        var spec = Compute(wl);

        spec.Env.Count(x => x.Name == "REEVE_SPEC_HASH").Should().Be(1);
        spec.Env.Single(x => x.Name == "REEVE_SPEC_HASH").Value.Should().Be(spec.SpecHash);
    }

    [Fact]
    public void An_author_literal_spec_hash_env_cannot_poison_the_hash_or_HELLO_value()
    {
        var wl = MatterLike(env: new[]
        {
            new WorkloadEnv { Name = "REEVE_SPEC_HASH", From = "literal:bogus-author-value" },
        });
        var spec = Compute(wl);

        var e = spec.Env.Single(x => x.Name == "REEVE_SPEC_HASH");
        e.Kind.Should().Be(ResolvedEnvKind.SpecHash);
        e.Value.Should().Be(spec.SpecHash);
        e.Value.Should().NotBe("bogus-author-value");
    }

    [Fact]
    public void An_author_literal_spec_hash_env_does_not_change_the_spec_hash()
    {
        var withoutDecl = Compute(MatterLike());
        var withBogusLiteral = Compute(MatterLike(env: new[]
        {
            new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" },
            new WorkloadEnv { Name = "REEVE_WORKLOAD_TOKEN", From = "bootstrap:workloadToken" },
            new WorkloadEnv { Name = "REEVE_SPEC_HASH", From = "literal:bogus-author-value" },
        }));

        withBogusLiteral.SpecHash.Should().Be(withoutDecl.SpecHash);
    }


    private static IReadOnlyList<ResolvedEnvVar> HostEnv(string name, string value)
        => [new ResolvedEnvVar { Name = name, Kind = ResolvedEnvKind.Literal, Value = value }];

    [Fact]
    public void Host_contributed_env_is_rendered_as_a_literal()
    {
        var spec = Compute(MatterLike(), bootstrap: Bootstrap(hostEnv: HostEnv("REEVE_API_SCHEMA", "2")));

        var e = spec.Env.Single(x => x.Name == "REEVE_API_SCHEMA");
        e.Kind.Should().Be(ResolvedEnvKind.Literal);
        e.Value.Should().Be("2");
    }

    [Fact]
    public void No_host_env_is_invented_when_the_host_contributes_none()
    {
        var spec = Compute(MatterLike());

        spec.Env.Should().NotContain(x => x.Name == "REEVE_API_SCHEMA");
    }

    [Fact]
    public void An_author_declared_env_is_forced_to_the_host_contributed_value()
    {
        var wl = MatterLike(
            env: new[]
            {
                new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" },
                new WorkloadEnv { Name = "REEVE_API_SCHEMA", From = "literal:9" },
            });

        var spec = Compute(wl, bootstrap: Bootstrap(hostEnv: HostEnv("REEVE_API_SCHEMA", "2")));

        var e = spec.Env.Single(x => x.Name == "REEVE_API_SCHEMA");
        e.Value.Should().Be("2");
        e.Value.Should().NotBe("9");
    }

    [Fact]
    public void Changing_a_host_contributed_env_moves_the_spec_hash()
    {
        var one = Compute(MatterLike(), bootstrap: Bootstrap(hostEnv: HostEnv("REEVE_API_SCHEMA", "1")));
        var two = Compute(MatterLike(), bootstrap: Bootstrap(hostEnv: HostEnv("REEVE_API_SCHEMA", "2")));

        two.SpecHash.Should().NotBe(one.SpecHash);
    }

    [Fact]
    public void App_version_env_is_injected_as_a_literal_from_the_bootstrap_version()
    {
        var spec = Compute(MatterLike(), bootstrap: Bootstrap(appVersion: "1.3.2"));

        var e = spec.Env.Single(x => x.Name == "REEVE_APP_VERSION");
        e.Kind.Should().Be(ResolvedEnvKind.Literal);
        e.Value.Should().Be("1.3.2");
    }

    [Fact]
    public void No_app_version_env_when_the_bootstrap_version_is_absent()
    {
        var spec = Compute(MatterLike(), bootstrap: Bootstrap(appVersion: null));

        spec.Env.Should().NotContain(x => x.Name == "REEVE_APP_VERSION");
    }

    [Fact]
    public void An_author_declared_app_version_env_is_forced_to_the_backend_version()
    {
        var wl = MatterLike(env: new[]
        {
            new WorkloadEnv { Name = "REEVE_APP_VERSION", From = "literal:9.9.9-bogus" },
            new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" },
        });
        var spec = Compute(wl, bootstrap: Bootstrap(appVersion: "1.3.2"));

        spec.Env.Count(x => x.Name == "REEVE_APP_VERSION").Should().Be(1);
        spec.Env.Single(x => x.Name == "REEVE_APP_VERSION").Value.Should().Be("1.3.2");
    }

    [Fact]
    public void The_app_version_env_does_not_change_the_spec_hash()
    {
        var withoutVersion = Compute(MatterLike(), bootstrap: Bootstrap(appVersion: null));
        var withVersion = Compute(MatterLike(), bootstrap: Bootstrap(appVersion: "1.3.2"));

        withVersion.SpecHash.Should().Be(withoutVersion.SpecHash);
    }

    [Fact]
    public void Two_different_app_versions_hash_identically()
    {
        var v1 = Compute(MatterLike(), bootstrap: Bootstrap(appVersion: "1.3.1"));
        var v2 = Compute(MatterLike(), bootstrap: Bootstrap(appVersion: "1.3.2"));

        v2.SpecHash.Should().Be(v1.SpecHash);
    }

    [Fact]
    public void Parameter_env_resolves_from_the_resolved_parameters()
    {
        var wl = MatterLike(env: new[]
        {
            new WorkloadEnv { Name = "ADAPTER", From = "parameter:serialPort" },
        });
        var spec = Compute(wl);

        var e = spec.Env.Single(x => x.Name == "ADAPTER");
        e.Kind.Should().Be(ResolvedEnvKind.Literal);
        e.Value.Should().Be("/dev/ttyUSB0");
    }

    [Fact]
    public void Literal_env_carries_the_literal_value_verbatim()
    {
        var wl = MatterLike(env: new[]
        {
            new WorkloadEnv { Name = "LOG_LEVEL", From = "literal:debug" },
        });
        var spec = Compute(wl);

        spec.Env.Single(x => x.Name == "LOG_LEVEL").Value.Should().Be("debug");
    }

    [Fact]
    public void Workload_host_env_resolves_the_sibling_service_dns()
    {
        var wl = MatterLike(env: new[]
        {
            new WorkloadEnv { Name = "OTBR_HOST", From = "bootstrap:workloadHost:otbr" },
        });
        var spec = Compute(wl, bootstrap: Bootstrap(
            siblings: new Dictionary<string, string>(StringComparer.Ordinal) { ["otbr"] = "reeve-wl-x-otbr-abcd1234" }));

        spec.Env.Single(x => x.Name == "OTBR_HOST").Value.Should().Be("reeve-wl-x-otbr-abcd1234");
    }


    [Fact]
    public void Device_paths_resolve_from_their_parameters()
    {
        var spec = Compute(MatterLike());
        spec.Privileges!.DevicePaths.Should().BeEquivalentTo(new[] { "/dev/ttyUSB0" });
    }

    [Fact]
    public void A_bound_device_runs_privileged_where_the_provider_needs_it_for_passthrough()
    {
        var kubernetes = Compute(MatterLike(), provider: StandInProviders.Kubernetes).Privileges!;
        kubernetes.EffectivePrivileged.Should().BeTrue(
            "the runtime's device cgroup refuses a hostPath-mounted device to an unprivileged container");
        kubernetes.Privileged.Should().BeFalse("the manifest does not declare privileged mode");
        kubernetes.DevicePassthroughPrivileged.Should().BeTrue();

        var docker = Compute(MatterLike(), provider: StandInProviders.Docker).Privileges!;
        docker.EffectivePrivileged.Should().BeFalse("Docker grants the device itself");
        docker.DevicePassthroughPrivileged.Should().BeFalse();
    }

    [Fact]
    public void A_declared_privileged_device_workload_is_not_marked_privileged_for_passthrough()
    {
        var wl = MatterLike() with { Privileges = MatterLike().Privileges! with { Privileged = true } };

        var privileges = Compute(wl, provider: StandInProviders.Kubernetes).Privileges!;

        privileges.Privileged.Should().BeTrue();
        privileges.DevicePassthroughPrivileged.Should().BeFalse();
    }

    [Fact]
    public void A_device_slot_left_unbound_does_not_make_the_container_privileged()
    {
        var wl = MatterLike();
        var resolved = WorkloadParameterResolver.Resolve(wl, new Dictionary<string, string?>(), StandInProviders.Kubernetes);

        Compute(wl, resolved: resolved).Privileges!.EffectivePrivileged.Should().BeFalse();
    }

    [Fact]
    public void A_user_namespace_requested_for_a_bound_device_is_skipped_and_reported()
    {
        var wl = MatterLike();
        var on = ComputeWith(wl, new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["security.userNamespace"] = "true",
        });
        var off = ComputeWith(wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" });
        var unbound = ComputeWith(wl, new Dictionary<string, string?> { ["security.userNamespace"] = "true" });

        on.UserNamespace.Should().BeFalse("a privileged container in a user namespace holds no host privilege to open the device");
        on.UserNamespaceSkippedForDevicePassthrough.Should().BeTrue();
        on.UserNamespaceSkippedForHostNetwork.Should().BeFalse();
        on.SpecHash.Should().Be(off.SpecHash);
        unbound.UserNamespace.Should().BeTrue();
        unbound.UserNamespaceSkippedForDevicePassthrough.Should().BeFalse();
    }

    [Theory]
    [InlineData("kubernetes", true)]
    [InlineData("manual", true)]
    [InlineData("docker", false)]
    public void The_requirements_judged_before_a_write_say_privileged_exactly_when_the_spec_renders_it(string providerId, bool privileged)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        var wl = MatterLike();
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, provider);

        var judged = WorkloadPrivilegeRequirements.FromWorkload(
            wl,
            resolved.Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value!, StringComparer.Ordinal),
            provider);
        var applied = WorkloadPrivilegeRequirements.FromDesiredSpec(Compute(wl, resolved: resolved, provider: provider));

        judged.Privileged.Should().Be(privileged);
        applied.Privileged.Should().Be(privileged);
        judged.Covers(applied, out var violations).Should().BeTrue(string.Join("; ", violations));
    }

    private static WorkloadManifest MultiDeviceWorkload(int maxCount, WorkloadDeviceGroup group = WorkloadDeviceGroup.None)
        => MatterLike() with
        {
            Privileges = new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = maxCount, Group = group } },
            },
            Parameters = new[]
            {
                new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath },
            },
        };

    [Fact]
    public void Device_paths_resolve_every_bound_index_and_skip_blanks()
    {
        var wl = MultiDeviceWorkload(maxCount: 3);
        var resolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?> { ["acc[0]"] = "/dev/ttyUSB0", ["acc[1]"] = "   ", ["acc[2]"] = "/dev/ttyUSB2" },
            StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DevicePaths.Should().BeEquivalentTo(new[] { "/dev/ttyUSB0", "/dev/ttyUSB2" });
    }

    [Fact]
    public void Device_paths_never_include_a_stale_declared_value_on_a_multidevice_slot()
    {
        var wl = MultiDeviceWorkload(maxCount: 2);
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["acc"] = "/dev/ttyUSB9" }, StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DevicePaths.Should().BeEmpty();
    }

    [Fact]
    public void Device_paths_still_read_the_declared_key_when_maxCount_is_one()
    {
        var wl = MultiDeviceWorkload(maxCount: 1);
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["acc"] = "/dev/ttyUSB0" }, StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DevicePaths.Should().BeEquivalentTo(new[] { "/dev/ttyUSB0" });
    }

    [Fact]
    public void Resolved_gid_on_a_group_auto_slot_becomes_a_deduplicated_device_group_id()
    {
        var wl = MultiDeviceWorkload(maxCount: 2, group: WorkloadDeviceGroup.Auto);
        var baseResolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?> { ["acc[0]"] = "/dev/ttyUSB0", ["acc[1]"] = "/dev/ttyUSB1" },
            StandInProviders.Kubernetes);
        var resolved = new Dictionary<string, string?>(baseResolved) { ["acc[0]:gid"] = "5", ["acc[1]:gid"] = "5" };
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DeviceGroupIds.Should().BeEquivalentTo(new[] { 5 });
    }

    [Fact]
    public void Resolved_gid_on_a_non_auto_slot_is_ignored()
    {
        var wl = MultiDeviceWorkload(maxCount: 2, group: WorkloadDeviceGroup.None);
        var baseResolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["acc[0]"] = "/dev/ttyUSB0" }, StandInProviders.Kubernetes);
        var resolved = new Dictionary<string, string?>(baseResolved) { ["acc[0]:gid"] = "5" };
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DeviceGroupIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void A_non_positive_or_unparsable_gid_grants_nothing(string gidValue)
    {
        var wl = MultiDeviceWorkload(maxCount: 1, group: WorkloadDeviceGroup.Auto);
        var baseResolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["acc"] = "/dev/ttyUSB0" }, StandInProviders.Kubernetes);
        var resolved = new Dictionary<string, string?>(baseResolved) { ["acc:gid"] = gidValue };
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DeviceGroupIds.Should().BeEmpty();
    }

    [Fact]
    public void A_gid_on_an_index_that_bound_no_device_grants_nothing()
    {
        var wl = MultiDeviceWorkload(maxCount: 2, group: WorkloadDeviceGroup.Auto);
        var baseResolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["acc[0]"] = "/dev/ttyUSB0" }, StandInProviders.Kubernetes);
        var resolved = new Dictionary<string, string?>(baseResolved) { ["acc[1]:gid"] = "44" };
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DeviceGroupIds.Should().BeEmpty();
    }

    [Fact]
    public void JoinsDeviceGroups_is_true_for_an_auto_slot_that_bound_a_device_even_with_no_gid()
    {
        var wl = MultiDeviceWorkload(maxCount: 1, group: WorkloadDeviceGroup.Auto);
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["acc"] = "/dev/dri/renderD128" }, StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.JoinsDeviceGroups.Should().BeTrue();
        spec.Privileges!.DeviceGroupIds.Should().BeEmpty();
    }

    [Fact]
    public void JoinsDeviceGroups_is_false_for_a_none_slot_that_bound_a_device()
    {
        var wl = MultiDeviceWorkload(maxCount: 1, group: WorkloadDeviceGroup.None);
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["acc"] = "/dev/dri/renderD128" }, StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.JoinsDeviceGroups.Should().BeFalse();
    }

    [Fact]
    public void JoinsDeviceGroups_is_false_when_an_auto_slot_bound_nothing()
    {
        var wl = MultiDeviceWorkload(maxCount: 1, group: WorkloadDeviceGroup.Auto);
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["acc"] = "" }, StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.JoinsDeviceGroups.Should().BeFalse();
    }

    [Fact]
    public void JoinsDeviceGroups_is_true_when_only_a_later_index_of_a_multi_slot_bound_a_device()
    {
        var wl = MultiDeviceWorkload(maxCount: 4, group: WorkloadDeviceGroup.Auto);
        var resolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?> { ["acc[0]"] = "", ["acc[2]"] = "/dev/dri/renderD129" },
            StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.JoinsDeviceGroups.Should().BeTrue();
    }

    [Fact]
    public void Two_indices_resolving_the_same_path_render_one_device_path()
    {
        var wl = MultiDeviceWorkload(maxCount: 2);
        var resolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?> { ["acc[0]"] = "/dev/ttyUSB0", ["acc[1]"] = "/dev/ttyUSB0" },
            StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DevicePaths.Should().BeEquivalentTo(new[] { "/dev/ttyUSB0" });
    }

    [Fact]
    public void Device_paths_keep_first_binding_order_rather_than_sorting()
    {
        var wl = MultiDeviceWorkload(maxCount: 2);
        var resolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?> { ["acc[0]"] = "/dev/b", ["acc[1]"] = "/dev/a" },
            StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DevicePaths.Should().Equal("/dev/b", "/dev/a");
    }

    [Fact]
    public void A_duplicate_path_still_contributes_its_group_grant()
    {
        var wl = MultiDeviceWorkload(maxCount: 2, group: WorkloadDeviceGroup.Auto);
        var baseResolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?>
            {
                ["acc[0]"] = "/dev/dri/renderD128",
                ["acc[1]"] = "/dev/dri/renderD128",
            },
            StandInProviders.Kubernetes);
        var resolved = new Dictionary<string, string?>(baseResolved) { ["acc[1]:gid"] = "44" };
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DevicePaths.Should().Equal("/dev/dri/renderD128");
        spec.Privileges!.JoinsDeviceGroups.Should().BeTrue();
        spec.Privileges!.DeviceGroupIds.Should().BeEquivalentTo(new[] { 44 });
    }

    private static WorkloadManifest TwoDeviceSlotWorkload()
        => MatterLike() with
        {
            Privileges = new WorkloadPrivileges
            {
                Devices = new[]
                {
                    new WorkloadDevice { Parameter = "acc", MaxCount = 1, Group = WorkloadDeviceGroup.None },
                    new WorkloadDevice { Parameter = "render", MaxCount = 1, Group = WorkloadDeviceGroup.Auto },
                },
            },
            Parameters = new[]
            {
                new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath },
                new WorkloadParameter { Name = "render", Type = WorkloadParameterType.DevicePath },
            },
        };

    [Fact]
    public void Two_slots_binding_the_same_path_render_it_once_and_keep_the_second_slots_grant()
    {
        var wl = TwoDeviceSlotWorkload();
        var baseResolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?>
            {
                ["acc"] = "/dev/dri/renderD128",
                ["render"] = "/dev/dri/renderD128",
            },
            StandInProviders.Kubernetes);
        var resolved = new Dictionary<string, string?>(baseResolved) { ["render:gid"] = "44" };
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DevicePaths.Should().Equal("/dev/dri/renderD128");
        spec.Privileges!.JoinsDeviceGroups.Should().BeTrue();
        spec.Privileges!.DeviceGroupIds.Should().BeEquivalentTo(new[] { 44 });
    }

    [Fact]
    public void Dbus_path_resolves_to_the_default_when_hostDbus_and_unspecified()
    {
        var wl = MatterLike();
        wl = wl with { Privileges = new WorkloadPrivileges { HostDbus = true } };
        var resolved = WorkloadParameterResolver.Resolve(wl, new Dictionary<string, string?>(), StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.Privileges!.DbusHostPath.Should().Be(WorkloadPrivilegeRequirements.DefaultDbusHostPath);
    }

    [Fact]
    public void NodeLocalState_hostpath_is_root_slash_plugintype_slash_workload_slash_subdir()
    {
        var wl = MatterLike();
        wl = wl with
        {
            Privileges = new WorkloadPrivileges
            {
                NodeLocalState = new NodeLocalState { Subdir = "bindkeys", Criticality = VolumeCriticality.Critical },
            },
        };
        var provider = StandInProviders.Kubernetes with { NodeStateRoot = "/srv/reeve-state" };
        var resolved = WorkloadParameterResolver.Resolve(wl, new Dictionary<string, string?>(), provider);
        var spec = Compute(wl, resolved: resolved, provider: provider);

        spec.Privileges!.NodeLocalState!.HostPath.Should().Be("/srv/reeve-state/com.reeve.matter/matter/bindkeys");
    }


    [Fact]
    public void Volume_carries_a_resolved_pvc_name_and_NO_storage_class()
    {
        var wl = MatterLike();
        wl = wl with
        {
            Volumes = new[]
            {
                new WorkloadVolume { Id = "data", MountPath = "/data", Size = "64Mi", Criticality = VolumeCriticality.Critical },
            },
        };
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        var vol = spec.Volumes.Single();
        vol.ResourceName.Should().Contain("data");
        vol.ExistingClaim.Should().BeFalse("a fresh install computes a new PVC name — it does not adopt an existing claim");
        vol.StorageClassName.Should().BeNull(
            "the default class is a kubernetes setting resolved where the PVC is rendered; a provider-agnostic "
            + "spec factory has no business reading one provider\'s section, and the field is spec-hash-excluded "
            + "so moving it changes no hash");
    }


    private static WorkloadManifest MatterWithVolume()
        => MatterLike() with
        {
            Volumes = new[]
            {
                new WorkloadVolume { Id = "state", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Critical },
            },
        };

    [Fact]
    public void ExistingClaim_param_binds_the_volume_to_the_retained_pvc_name_instead_of_a_fresh_one()
    {
        const string retainedPvc = "reeve-wl-com-reeve-matter-matter-state-deadbeef";
        var wl = MatterWithVolume();
        var resolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?>
            {
                ["serialPort"] = "/dev/ttyUSB0",
                ["existingClaim.state"] = retainedPvc,
            },
            StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        var vol = spec.Volumes.Single();
        vol.ResourceName.Should().Be(retainedPvc, "the volume references the retained PVC's physical name");
        vol.ExistingClaim.Should().BeTrue("the volume is marked as adopting an existing claim");
    }

    [Fact]
    public void No_existingClaim_param_leaves_the_volume_on_a_fresh_computed_pvc_name()
    {
        var wl = MatterWithVolume();
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        var vol = spec.Volumes.Single();
        vol.ResourceName.Should().Be(
            DefaultWorkloadResourceNamer.Instance.VolumeName(
                new WorkloadNamingSubject
                {
                    GroupId = "com.reeve.matter",
                    WorkloadId = "matter",
                    Inst8 = spec.Inst8,
                },
                "state"),
            "a fresh install uses the derived PVC name");
        vol.ExistingClaim.Should().BeFalse();
    }

    [Fact]
    public void A_blank_existingClaim_param_is_ignored_and_the_volume_stays_fresh()
    {
        var wl = MatterWithVolume();
        var resolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["existingClaim.state"] = "  " },
            StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        var vol = spec.Volumes.Single();
        vol.ResourceName.Should().Be(
            DefaultWorkloadResourceNamer.Instance.VolumeName(
                new WorkloadNamingSubject
                {
                    GroupId = "com.reeve.matter",
                    WorkloadId = "matter",
                    Inst8 = spec.Inst8,
                },
                "state"));
        vol.ExistingClaim.Should().BeFalse();
    }

    [Fact]
    public void ExistingClaim_does_NOT_change_the_spec_hash_no_rollout_churn()
    {
        var wl = MatterWithVolume();
        var fresh = Compute(wl, resolved: WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, StandInProviders.Kubernetes));
        var restored = Compute(wl, resolved: WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?>
            {
                ["serialPort"] = "/dev/ttyUSB0",
                ["existingClaim.state"] = "reeve-wl-com-reeve-matter-matter-state-deadbeef",
            },
            StandInProviders.Kubernetes));

        restored.SpecHash.Should().Be(fresh.SpecHash,
            "existingClaim rebinds only the (hash-excluded) PVC name — it must not move the SpecHash");
    }


    private static IReadOnlyDictionary<string, string?> ResolvedWithSizeOverride(WorkloadManifest wl, string? sizeOverride)
    {
        var resolved = new Dictionary<string, string?>(
            WorkloadParameterResolver.Resolve(wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, StandInProviders.Kubernetes),
            StringComparer.Ordinal);
        if (sizeOverride is not null)
        {
            resolved[$"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}state"] = sizeOverride;
        }

        return resolved;
    }

    [Fact]
    public void A_volumeSize_override_replaces_the_declared_size_in_the_desired_spec()
    {
        var wl = MatterWithVolume();
        var spec = Compute(wl, resolved: ResolvedWithSizeOverride(wl, "4Gi"));

        spec.Volumes.Single().Size.Should().Be("4Gi", "the operator's desired upward-resize override replaces the declared 1Gi");
    }

    [Fact]
    public void A_blank_or_absent_volumeSize_override_keeps_the_declared_manifest_size()
    {
        var wl = MatterWithVolume();
        Compute(wl, resolved: ResolvedWithSizeOverride(wl, sizeOverride: null)).Volumes.Single().Size.Should().Be("1Gi");
        Compute(wl, resolved: ResolvedWithSizeOverride(wl, "   ")).Volumes.Single().Size.Should().Be("1Gi", "a blank override is ignored");
    }

    [Fact]
    public void A_volumeSize_override_MOVES_the_spec_hash_so_the_reconciler_re_applies()
    {
        var wl = MatterWithVolume();
        var before = Compute(wl, resolved: ResolvedWithSizeOverride(wl, sizeOverride: null));
        var after = Compute(wl, resolved: ResolvedWithSizeOverride(wl, "4Gi"));

        after.SpecHash.Should().NotBe(before.SpecHash, "a size bump changes the SpecHash → the reconciler re-applies");
    }


    private static WorkloadManifest WithDeclaredStateSize(string declaredSize)
        => MatterLike() with
        {
            Volumes = new[]
            {
                new WorkloadVolume { Id = "state", MountPath = "/data", Size = declaredSize, Criticality = VolumeCriticality.Critical },
            },
        };

    [Fact]
    public void A_volumeSize_override_SMALLER_than_a_grown_declared_size_yields_the_declared_minimum()
    {
        var wl = WithDeclaredStateSize("50Gi");
        var spec = Compute(wl, resolved: ResolvedWithSizeOverride(wl, "20Gi"));

        spec.Volumes.Single().Size.Should().Be("50Gi",
            "a stale 20Gi override must not defeat the manifest's larger 50Gi declared minimum — max(override, declared) wins");
    }

    [Fact]
    public void A_volumeSize_override_LARGER_than_the_declared_size_grows_the_volume()
    {
        var wl = WithDeclaredStateSize("10Gi");
        var spec = Compute(wl, resolved: ResolvedWithSizeOverride(wl, "20Gi"));

        spec.Volumes.Single().Size.Should().Be("20Gi", "an override larger than the declared minimum grows the volume");
    }

    [Fact]
    public void A_manifest_grown_past_a_smaller_override_MOVES_the_spec_hash_to_reconcile_the_grow()
    {
        var effective20 = Compute(WithDeclaredStateSize("10Gi"), resolved: ResolvedWithSizeOverride(WithDeclaredStateSize("10Gi"), "20Gi"));
        var effective50 = Compute(WithDeclaredStateSize("50Gi"), resolved: ResolvedWithSizeOverride(WithDeclaredStateSize("50Gi"), "20Gi"));

        effective50.SpecHash.Should().NotBe(effective20.SpecHash,
            "the manifest growing past the override changes the effective size → the SpecHash moves → reconcile grows to 50Gi");
    }


    [Fact]
    public void Every_name_and_label_comes_from_the_namer_the_factory_is_given()
    {
        var wl = MatterWithVolume();
        var spec = Compute(wl, namer: OtherSchemeNamer.Instance);

        var vol = spec.Volumes.Single();
        vol.ResourceName.Should().Be($"other-vol-com-reeve-matter-state-{spec.Inst8}");
        vol.ExistingClaim.Should().BeFalse();
        spec.ResourceName.Should().Be($"other-obj-com-reeve-matter-matter-{spec.Inst8}");
        spec.SecretName.Should().Be($"other-secret-com-reeve-matter-matter-{spec.Inst8}");
        spec.Labels.Should().Equal(new Dictionary<string, string>
        {
            ["other/instance"] = InstanceId.ToString("D"),
            ["other/spec"] = spec.SpecHash,
        });
    }

    [Theory]
    [InlineData("kubernetes")]
    [InlineData("manual")]
    public void Kubernetes_and_manual_instances_keep_the_pvc_scheme_for_a_fresh_volume(string providerId)
    {
        var provider = StandInProviders.For(WorkloadProviderId.Parse(providerId));
        var wl = MatterWithVolume();
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, provider);
        var spec = Compute(wl, resolved: resolved, provider: provider);

        spec.Volumes.Single().ResourceName.Should().Be(
            DefaultWorkloadResourceNamer.Instance.VolumeName(
                new WorkloadNamingSubject
                {
                    GroupId = "com.reeve.matter",
                    WorkloadId = "matter",
                    Inst8 = spec.Inst8,
                },
                "state"));
    }

    [Fact]
    public void Docker_fresh_install_and_docker_restore_agree_on_the_physical_volume_name_round_trip()
    {
        var wl = MatterWithVolume();

        var fresh = Compute(
            wl,
            resolved: WorkloadParameterResolver.Resolve(
                wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, StandInProviders.Docker),
            provider: StandInProviders.Docker,
            namer: OtherSchemeNamer.Instance);
        var freshName = fresh.Volumes.Single().ResourceName;

        var dockerInstance = WorkloadInstance.Create(InstanceId, PluginInstanceId, "com.reeve.matter", "matter", DateTimeOffset.UtcNow);
        dockerInstance.SetProvider(StandInProviders.DockerId, DateTimeOffset.UtcNow);
        var retainedName = WorkloadResourceNaming.ResolveRetainedResourceName(dockerInstance, "state", OtherSchemeNamer.Instance);
        freshName.Should().Be(retainedName, "the fresh docker install name must equal what docker retain records — else restore orphans the data");

        var restore = Compute(
            wl,
            resolved: WorkloadParameterResolver.Resolve(
                wl,
                new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["existingClaim.state"] = retainedName },
                StandInProviders.Docker),
            provider: StandInProviders.Docker,
            namer: OtherSchemeNamer.Instance);
        var restoredVol = restore.Volumes.Single();
        restoredVol.ResourceName.Should().Be(freshName, "the restore adopts the exact volume the fresh install created");
        restoredVol.ExistingClaim.Should().BeTrue();
    }

    [Fact]
    public void Docker_fresh_vs_restore_hash_identically_resource_name_is_not_hashed()
    {
        var wl = MatterWithVolume();
        var fresh = Compute(
            wl,
            resolved: WorkloadParameterResolver.Resolve(
                wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, StandInProviders.Docker),
            provider: StandInProviders.Docker);
        var restored = Compute(
            wl,
            resolved: WorkloadParameterResolver.Resolve(
                wl,
                new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["existingClaim.state"] = "reeve-com-reeve-matter-state-deadbeef" },
                StandInProviders.Docker),
            provider: StandInProviders.Docker);

        restored.SpecHash.Should().Be(fresh.SpecHash);
    }


    [Fact]
    public void NodeSelector_param_value_flows_into_the_desired_spec_as_a_label_map()
    {
        var wl = MatterLike() with
        {
            Parameters = new[]
            {
                new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath },
                new WorkloadParameter { Name = "nodeSelector", Type = WorkloadParameterType.NodeSelector, Default = "reeve-iot.dev/ble=true" },
            },
        };
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" }, StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.NodeSelector.Should().Equal(new Dictionary<string, string> { ["reeve-iot.dev/ble"] = "true" });
    }

    [Fact]
    public void NodeSelector_bare_key_defaults_the_value_to_true()
    {
        var wl = MatterLike() with
        {
            Parameters = new[]
            {
                new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath },
                new WorkloadParameter { Name = "nodeSelector", Type = WorkloadParameterType.NodeSelector },
            },
        };
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["nodeSelector"] = "reeve-iot.dev/zwave" },
            StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.NodeSelector.Should().Equal(new Dictionary<string, string> { ["reeve-iot.dev/zwave"] = "true" });
    }

    [Fact]
    public void No_nodeSelector_param_leaves_the_map_empty()
    {
        Compute(MatterLike()).NodeSelector.Should().BeEmpty();
    }

    [Fact]
    public void A_workload_declaring_no_placement_parameter_still_takes_one_from_the_operator()
    {
        var wl = MatterLike();
        var resolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?>
            {
                ["serialPort"] = "/dev/ttyUSB0",
                ["nodeSelector"] = "intel.feature.node.kubernetes.io/gpu=true",
            },
            StandInProviders.Kubernetes);

        Compute(wl, resolved: resolved).NodeSelector.Should().Equal(
            new Dictionary<string, string> { ["intel.feature.node.kubernetes.io/gpu"] = "true" },
            "which node runs a workload is the operator's call, not something the author grants by declaring a parameter");
    }

    [Fact]
    public void A_placement_carries_every_label_it_names_not_just_the_first()
    {
        var wl = MatterLike();
        var resolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?>
            {
                ["serialPort"] = "/dev/ttyUSB0",
                ["nodeSelector"] = "intel.feature.node.kubernetes.io/gpu=true, topology.kubernetes.io/zone=rack-b",
            },
            StandInProviders.Kubernetes);

        Compute(wl, resolved: resolved).NodeSelector.Should().Equal(new Dictionary<string, string>
        {
            ["intel.feature.node.kubernetes.io/gpu"] = "true",
            ["topology.kubernetes.io/zone"] = "rack-b",
        });
    }

    [Fact]
    public void The_operators_placement_wins_a_label_the_author_also_set()
    {
        var wl = MatterLike() with
        {
            Parameters = new[]
            {
                new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath },
                new WorkloadParameter { Name = "authorPlacement", Type = WorkloadParameterType.NodeSelector, Default = "reeve-iot.dev/ble=true" },
            },
        };
        var resolved = WorkloadParameterResolver.Resolve(
            wl,
            new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["nodeSelector"] = "reeve-iot.dev/ble=false" },
            StandInProviders.Kubernetes);

        Compute(wl, resolved: resolved).NodeSelector["reeve-iot.dev/ble"].Should().Be("false");
    }

    [Fact]
    public void An_author_declared_placement_still_applies_when_the_operator_sets_none()
    {
        var wl = MatterLike() with
        {
            Parameters = new[]
            {
                new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath },
                new WorkloadParameter { Name = "authorPlacement", Type = WorkloadParameterType.NodeSelector, Default = "reeve-iot.dev/ble=true" },
            },
        };

        Compute(wl).NodeSelector.Should().Equal(new Dictionary<string, string> { ["reeve-iot.dev/ble"] = "true" });
    }

    [Fact]
    public void Different_nodeSelectors_hash_differently_but_the_same_one_is_stable()
    {
        WorkloadDesiredSpec WithSelector(string? value)
        {
            var wl = MatterLike() with
            {
                Parameters = new[]
                {
                    new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath },
                    new WorkloadParameter { Name = "nodeSelector", Type = WorkloadParameterType.NodeSelector },
                },
            };
            var ops = new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" };
            if (value is not null)
                ops["nodeSelector"] = value;
            var resolved = WorkloadParameterResolver.Resolve(wl, ops, StandInProviders.Kubernetes);
            return Compute(wl, resolved: resolved);
        }

        var ble = WithSelector("reeve-iot.dev/ble=true");
        var ble2 = WithSelector("reeve-iot.dev/ble=true");
        var zwave = WithSelector("reeve-iot.dev/zwave=true");
        var none = WithSelector(null);

        ble.SpecHash.Should().Be(ble2.SpecHash, "the same selector must hash identically (deterministic)");
        ble.SpecHash.Should().NotBe(zwave.SpecHash, "a different node-selector must move the hash → forces a rollout");
        ble.SpecHash.Should().NotBe(none.SpecHash, "adding a node-selector must move the hash");
    }


    [Fact]
    public void Operator_resource_override_wins_over_author_default()
    {
        var wl = MatterLike();
        wl = wl with
        {
            Resources = new WorkloadResources { Limits = new ResourceQuantities { Memory = "256Mi" } },
        };
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?>
            {
                ["serialPort"] = "/dev/ttyUSB0",
                ["resources.limits.memory"] = "512Mi",
            },
            StandInProviders.Kubernetes);
        var spec = Compute(wl, resolved: resolved);

        spec.Resources!.LimitsMemory.Should().Be("512Mi");
    }


    private static UnsupportedPodPatch PodPatch(string rawJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
        var body = doc.RootElement.Clone();
        return new UnsupportedPodPatch { RawJson = body.GetRawText(), Body = body };
    }

    private const string EnvPatch = """[{"op":"add","path":"/spec/template/spec/containers/0/env/-","value":{"name":"EXTRA","value":"1"}}]""";

    [Fact]
    public void No_manifest_patch_leaves_UnsupportedPodPatch_null()
    {
        Compute(MatterLike()).UnsupportedPodPatch.Should().BeNull();
    }

    [Fact]
    public void Manifest_patch_is_carried_verbatim_onto_the_desired_spec()
    {
        var wl = MatterLike() with { UnsupportedPodPatch = PodPatch(EnvPatch) };

        var spec = Compute(wl);

        spec.UnsupportedPodPatch.Should().NotBeNull();
        spec.UnsupportedPodPatch!.PatchJson.Should().Be(wl.UnsupportedPodPatch!.Body.GetRawText());
        spec.UnsupportedPodPatch.PatchJson.Should().Contain("\"op\":\"add\"");
    }

    [Fact]
    public void A_workload_with_a_patch_hashes_differently_from_the_same_workload_without()
    {
        var without = Compute(MatterLike());
        var with = Compute(MatterLike() with { UnsupportedPodPatch = PodPatch(EnvPatch) });

        with.SpecHash.Should().NotBe(without.SpecHash,
            "adding an unsupportedPodPatch changes what materializes → must move the hash");
    }

    [Fact]
    public void Two_different_patch_bodies_hash_differently()
    {
        const string otherPatch = """[{"op":"add","path":"/spec/template/spec/hostPID","value":true}]""";

        var a = Compute(MatterLike() with { UnsupportedPodPatch = PodPatch(EnvPatch) });
        var b = Compute(MatterLike() with { UnsupportedPodPatch = PodPatch(otherPatch) });

        a.SpecHash.Should().NotBe(b.SpecHash, "a different patch body must move the hash");
    }

    private const string NetworkModeParameter = "network.mode";
    private const string ReservedAttachmentsParameter = "network.attachments";

    private static WorkloadManifest LegacyHostNetworkWorkload()
        => new()
        {
            Id = "legacy-adapter",
            DisplayName = "Legacy Adapter",
            Image = new WorkloadImage { Ref = "ghcr.io/reeve/legacy", Digest = Digest, Platforms = new[] { "linux/amd64" } },
            Kind = WorkloadKind.SingleNode,
            Network = new WorkloadNetwork { HostNetwork = true },
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        };

    private static WorkloadManifest TwoSlotWorkload()
        => new()
        {
            Id = "camera-bridge",
            DisplayName = "Camera Bridge",
            Image = new WorkloadImage { Ref = "ghcr.io/reeve/camera", Digest = Digest, Platforms = new[] { "linux/amd64" } },
            Kind = WorkloadKind.SingleNode,
            Network = new WorkloadNetwork
            {
                Attachments = new[]
                {
                    new WorkloadNetworkAttachmentSlot { Parameter = "slotA" },
                    new WorkloadNetworkAttachmentSlot { Parameter = "slotB" },
                },
            },
            Parameters = new[]
            {
                new WorkloadParameter { Name = "slotA", Type = WorkloadParameterType.NetworkAttachment },
                new WorkloadParameter { Name = "slotB", Type = WorkloadParameterType.NetworkAttachment },
            },
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        };

    private static WorkloadDesiredSpec ComputeWithResolved(
        WorkloadManifest wl, IReadOnlyDictionary<string, string?> resolved,
        WorkloadProviderDescriptor? provider = null)
        => WorkloadDesiredSpecFactory.Compute(
            wl,
            Settings(),
            resolved,
            Bootstrap(),
            provider ?? StandInProviders.Kubernetes,
            DefaultWorkloadResourceNamer.Instance,
            PluginInstanceId,
            "com.reeve.legacy",
            InstanceId,
            "install-1");

    [Fact]
    public void A_legacy_hostNetwork_manifest_with_no_slots_or_parameters_resolves_to_the_operator_network()
    {
        var spec = ComputeWithResolved(LegacyHostNetworkWorkload(), new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [NetworkModeParameter] = "operatorNetwork",
            [ReservedAttachmentsParameter] = "iot-macvlan",
        });

        spec.Network.Should().NotBeNull();
        spec.Network!.Mode.Should().Be(WorkloadNetworkMode.OperatorNetwork);
        spec.Network.Attachments.Should().Equal("iot-macvlan");
        spec.Network.HostNetwork.Should().BeFalse(
            "the operator moved the workload OFF the host namespace; the boolean must follow the resolved mode");
    }

    [Fact]
    public void Attachments_preserve_declared_order_reserved_slot_first_then_author_slots()
    {
        var spec = ComputeWithResolved(TwoSlotWorkload(), new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [NetworkModeParameter] = "operatorNetwork",
            [ReservedAttachmentsParameter] = "zulu",
            ["slotA"] = "bravo,alpha",
            ["slotB"] = "delta",
        });

        spec.Network!.Attachments.Should().Equal("zulu", "bravo", "alpha", "delta");
    }

    [Fact]
    public void Attachments_are_trimmed_and_de_duplicated_across_slots_preserving_first_occurrence()
    {
        var spec = ComputeWithResolved(TwoSlotWorkload(), new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [NetworkModeParameter] = "operatorNetwork",
            [ReservedAttachmentsParameter] = " iot-macvlan , cams ",
            ["slotA"] = "cams",
            ["slotB"] = "iot-macvlan,lab",
        });

        spec.Network!.Attachments.Should().Equal("iot-macvlan", "cams", "lab");
    }

    [Theory]
    [InlineData("cluster", WorkloadNetworkMode.Cluster)]
    [InlineData("hostNetwork", WorkloadNetworkMode.HostNetwork)]
    public void Attachments_are_empty_outside_operator_network_even_when_the_raw_parameter_carried_names(
        string rawMode, WorkloadNetworkMode expected)
    {
        var spec = ComputeWithResolved(TwoSlotWorkload(), new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [NetworkModeParameter] = rawMode,
            [ReservedAttachmentsParameter] = "iot-macvlan",
            ["slotA"] = "cams",
            ["slotB"] = "lab",
        });

        spec.Network!.Mode.Should().Be(expected);
        spec.Network.Attachments.Should().BeEmpty();
    }

    [Theory]
    [InlineData("cluster", WorkloadNetworkMode.Cluster, false)]
    [InlineData("hostNetwork", WorkloadNetworkMode.HostNetwork, true)]
    [InlineData("operatorNetwork", WorkloadNetworkMode.OperatorNetwork, false)]
    public void HostNetwork_is_true_exactly_when_the_resolved_mode_is_hostNetwork(
        string rawMode, WorkloadNetworkMode expectedMode, bool expectedHostNetwork)
    {
        var spec = ComputeWithResolved(LegacyHostNetworkWorkload(), new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [NetworkModeParameter] = rawMode,
            [ReservedAttachmentsParameter] = rawMode == "operatorNetwork" ? "iot-macvlan" : null,
        });

        spec.Network!.Mode.Should().Be(expectedMode);
        spec.Network.HostNetwork.Should().Be(expectedHostNetwork);
    }

    [Theory]
    [InlineData(true, WorkloadNetworkMode.HostNetwork, true)]
    [InlineData(false, WorkloadNetworkMode.Cluster, false)]
    public void With_no_resolved_network_mode_the_authors_declaration_is_kept_unchanged(
        bool declaredHostNetwork, WorkloadNetworkMode expectedMode, bool expectedHostNetwork)
    {
        var wl = LegacyHostNetworkWorkload() with { Network = new WorkloadNetwork { HostNetwork = declaredHostNetwork } };

        var spec = ComputeWithResolved(wl, new Dictionary<string, string?>(StringComparer.Ordinal));

        spec.Network!.Mode.Should().Be(expectedMode);
        spec.Network.HostNetwork.Should().Be(expectedHostNetwork);
        spec.Network.Attachments.Should().BeEmpty();
    }

    private sealed class OtherSchemeNamer : IWorkloadResourceNamer
    {
        public static OtherSchemeNamer Instance { get; } = new();

        public WorkloadProviderId Provider => WorkloadProviderId.Parse("other");

        public RetainedVolumeSchemeId VolumeScheme => RetainedVolumeSchemeId.Parse("other");

        public string ObjectName(WorkloadNamingSubject subject) => $"other-obj-{Segments(subject)}";

        public string SecretName(WorkloadNamingSubject subject) => $"other-secret-{Segments(subject)}";

        public string VolumeName(WorkloadNamingSubject subject, string volumeId)
            => $"other-vol-{WorkloadResourceNaming.Sanitize(subject.GroupId)}-{volumeId}-{subject.Inst8}";

        public IReadOnlyDictionary<string, string> Labels(WorkloadLabelSubject subject) => new Dictionary<string, string>
        {
            ["other/instance"] = subject.InstanceId.ToString("D"),
            ["other/spec"] = subject.SpecHash,
        };

        private static string Segments(WorkloadNamingSubject subject)
            => $"{WorkloadResourceNaming.Sanitize(subject.GroupId)}-{subject.WorkloadId}-{subject.Inst8}";

        public IReadOnlyDictionary<string, string> InstanceSelectorLabels(Guid instanceId)
            => DefaultWorkloadResourceNamer.Instance.InstanceSelectorLabels(instanceId);
    }

    [Fact]
    public void A_ConfigMap_mount_the_operator_left_blank_is_not_rendered()
    {
        var wl = MqttWorkload() with
        {
            Parameters = [new WorkloadParameter { Name = "cfg", Type = WorkloadParameterType.Text }, new WorkloadParameter { Name = "named", Type = WorkloadParameterType.Text }],
            ConfigMapMounts =
            [
                new ConfigMapMount { MountPath = "/blank", Parameter = "cfg" },
                new ConfigMapMount { MountPath = "/named", Parameter = "named" },
            ],
        };
        var resolved = WorkloadParameterResolver.Resolve(
            wl, new Dictionary<string, string?> { ["named"] = " matter-paa " }, StandInProviders.Kubernetes);

        var spec = Compute(wl, resolved: resolved);

        spec.ConfigMapMounts.Should().ContainSingle()
            .Which.Should().Be(new ResolvedConfigMapMount { MountPath = "/named", ConfigMapName = "matter-paa" });
    }
}

