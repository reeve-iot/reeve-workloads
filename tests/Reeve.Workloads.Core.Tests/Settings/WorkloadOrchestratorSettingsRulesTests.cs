using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadOrchestratorSettingsRulesTests
{
    private readonly IWorkloadOrchestratorSettingsRules rules = SettingsKit.ShippedRules();

    private static WorkloadOrchestratorSettings DockerWith(params (string Host, string Token)[] credentials)
        => new WorkloadOrchestratorSettings { Provider = DockerProvider.Id }
            .WithProvider(DockerProvider.Id, new DockerProviderSettings
            {
                RegistryCredentials = [.. credentials.Select(c => new RegistryCredential { HostPattern = c.Host, Username = "u", Token = c.Token })],
            });

    private static IReadOnlyList<RegistryCredential> Credentials(WorkloadOrchestratorSettings settings)
        => DockerProviderSettings.From(settings).RegistryCredentials;

    [Fact]
    public void Mask_replaces_every_set_secret_and_leaves_an_unset_one_empty()
    {
        var masked = rules.Mask(DockerWith(("a.example", "one"), ("b.example", "")));

        Credentials(masked).Select(c => c.Token).Should().Equal(WorkloadSecretMask.Sentinel, string.Empty);
    }

    [Fact]
    public void Mask_hides_a_kubeconfig()
    {
        var masked = rules.Mask(SettingsKit.Kubernetes(kubeconfig: "apiVersion: v1"));

        KubernetesProviderSettings.From(masked).Kubeconfig.Should().Be(WorkloadSecretMask.Sentinel);
    }

    [Fact]
    public void ResolveMasked_keeps_the_stored_docker_token_of_the_same_host_pattern()
    {
        var stored = DockerWith(("a.example", "stored-a"), ("b.example", "stored-b"));
        var submitted = DockerWith(("b.example", WorkloadSecretMask.Sentinel), ("a.example", WorkloadSecretMask.Sentinel));

        var resolved = rules.ResolveMasked(submitted, stored);

        Credentials(resolved).Select(c => (c.HostPattern, c.Token))
            .Should().Equal(("b.example", "stored-b"), ("a.example", "stored-a"));
    }

    [Fact]
    public void ResolveMasked_takes_a_new_docker_token_and_keeps_the_other_stored_one()
    {
        var stored = DockerWith(("a.example", "stored-a"), ("b.example", "stored-b"));
        var submitted = DockerWith(("a.example", "rotated"), ("b.example", WorkloadSecretMask.Sentinel));

        var resolved = rules.ResolveMasked(submitted, stored);

        Credentials(resolved).Select(c => c.Token).Should().Equal("rotated", "stored-b");
    }

    [Fact]
    public void ResolveMasked_refuses_a_masked_docker_token_for_a_host_pattern_with_nothing_stored()
    {
        var stored = DockerWith(("a.example", "stored-a"));
        var submitted = DockerWith(("a.example", WorkloadSecretMask.Sentinel), ("new.example", WorkloadSecretMask.Sentinel));

        var act = () => rules.ResolveMasked(submitted, stored);

        act.Should().Throw<WorkloadOrchestratorSettingsValidationException>().WithMessage("*new.example*");
    }

    [Fact]
    public void ResolveMasked_refuses_a_masked_token_when_nothing_is_stored_at_all()
    {
        var act = () => rules.ResolveMasked(DockerWith(("a.example", WorkloadSecretMask.Sentinel)), new WorkloadOrchestratorSettings());

        act.Should().Throw<WorkloadOrchestratorSettingsValidationException>();
    }

    [Fact]
    public void ResolveMasked_keeps_the_stored_kubeconfig_and_takes_the_submitted_fields_around_it()
    {
        var stored = SettingsKit.Kubernetes(ns: "old", kubeconfig: "stored-kubeconfig");
        var submitted = SettingsKit.Kubernetes(ns: "new", kubeconfig: WorkloadSecretMask.Sentinel);

        var resolved = KubernetesProviderSettings.From(rules.ResolveMasked(submitted, stored));

        resolved.Kubeconfig.Should().Be("stored-kubeconfig");
        resolved.Namespace.Should().Be("new");
    }

    [Fact]
    public void ResolveMasked_refuses_a_masked_kubeconfig_with_nothing_stored()
    {
        var submitted = SettingsKit.Kubernetes(kubeconfig: WorkloadSecretMask.Sentinel);

        var act = () => rules.ResolveMasked(submitted, SettingsKit.Kubernetes());

        act.Should().Throw<WorkloadOrchestratorSettingsValidationException>().WithMessage("*kubeconfig*");
    }

    [Fact]
    public void ResolveMasked_takes_the_non_secret_fields_of_the_document_from_the_submission()
    {
        var submitted = new WorkloadOrchestratorSettings { Provider = ManualProvider.Id, AllowUnpinnedImages = true, ReconciliationPaused = true };

        var resolved = rules.ResolveMasked(submitted, SettingsKit.Docker());

        resolved.Provider.Should().Be(ManualProvider.Id);
        resolved.AllowUnpinnedImages.Should().BeTrue();
        resolved.ReconciliationPaused.Should().BeTrue();
    }

    [Fact]
    public void Validate_accepts_a_document_with_real_secrets()
        => rules.Validate(SettingsKit.Docker()).Should().BeEmpty();

    [Fact]
    public void Validate_refuses_a_masked_secret_the_document_could_not_resolve()
    {
        var problems = rules.Validate(DockerWith(("a.example", WorkloadSecretMask.Sentinel)));

        problems.Should().ContainSingle().Which.Message.Should().Contain("a.example");
    }

    [Fact]
    public void Validate_refuses_a_provider_that_is_not_compiled_in()
        => rules.Validate(new WorkloadOrchestratorSettings { Provider = WorkloadProviderId.Parse("nomad") })
            .Should().ContainSingle().Which.Message.Should().Contain("nomad");

    [Fact]
    public void Validate_refuses_an_empty_mirror_rule()
        => rules.Validate(new WorkloadOrchestratorSettings { Mirrors = [new RegistryMirror { MatchRegistry = "", MirrorRegistry = "m.example" }] })
            .Should().ContainSingle().Which.Message.Should().Contain("mirrors[0].matchRegistry");

    [Fact]
    public void Validate_reports_every_problem_not_only_the_first()
    {
        var document = DockerWith(("", "t")) with { Mirrors = [new RegistryMirror { MatchRegistry = "", MirrorRegistry = "" }] };

        rules.Validate(document).Should().HaveCountGreaterThan(2);
    }

    [Fact]
    public void Secrets_lists_each_non_empty_secret_with_its_provider_path_and_value()
    {
        var document = DockerWith(("a.example", "one"), ("b.example", ""), ("c.example", "three"))
            .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings
            {
                ConnectionMode = KubernetesConnectionMode.RemoteKubeconfig,
                Kubeconfig = "kc",
            });

        var secrets = rules.Secrets(document);

        secrets.Select(s => (s.Provider, s.Path, s.Value)).Should().BeEquivalentTo(
        [
            (DockerProvider.Id, "/registryCredentials/0/token", "one"),
            (DockerProvider.Id, "/registryCredentials/2/token", "three"),
            (KubernetesProvider.Id, "/kubeconfig", "kc"),
        ]);
    }

    private const string SelfContainedKubeconfig = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            client-certificate-data: ZmFrZS1jZXJ0LWRhdGE=
            client-key-data: ZmFrZS1rZXktZGF0YQ==
        """;

    private const string FileReferenceKubeconfig = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            client-certificate: /home/op/.kube/client.crt
            client-key: /home/op/.kube/client.key
        """;

    private static WorkloadOrchestratorSettings Remote(string kubeconfig, string? context = null)
        => new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id }
            .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings
            {
                ConnectionMode = KubernetesConnectionMode.RemoteKubeconfig,
                Kubeconfig = kubeconfig,
                KubeconfigContext = context,
            });

    [Fact]
    public void Validate_accepts_a_self_contained_remote_kubeconfig()
        => rules.Validate(Remote(SelfContainedKubeconfig, "remote")).Should().BeEmpty();

    [Fact]
    public void Validate_refuses_a_remote_kubeconfig_that_references_files()
        => rules.Validate(Remote(FileReferenceKubeconfig)).Should().ContainSingle().Which.Message.Should().Contain("kubeconfig");

    [Fact]
    public void Validate_refuses_a_remote_kubeconfig_with_an_unknown_context()
        => rules.Validate(Remote(SelfContainedKubeconfig, "nope")).Should().ContainSingle();

    [Fact]
    public void Validate_refuses_a_masked_kubeconfig_even_for_an_in_cluster_entry()
        => rules.Validate(SettingsKit.Kubernetes(kubeconfig: null).WithProvider(
                KubernetesProvider.Id, new KubernetesProviderSettings { Kubeconfig = WorkloadSecretMask.Sentinel }))
            .Should().ContainSingle();

    [Theory]
    [InlineData("a.example", "", "t")]
    [InlineData("a.example", "u", "")]
    public void Validate_refuses_a_credential_with_an_empty_username_or_token(string host, string username, string token)
    {
        var document = new WorkloadOrchestratorSettings { Provider = DockerProvider.Id }
            .WithProvider(DockerProvider.Id, new DockerProviderSettings
            {
                RegistryCredentials = [new RegistryCredential { HostPattern = host, Username = username, Token = token }],
            });

        rules.Validate(document).Should().ContainSingle();
    }

    [Fact]
    public void Validate_refuses_a_blank_mirror_registry()
        => rules.Validate(new WorkloadOrchestratorSettings
        {
            Mirrors = [new RegistryMirror { MatchRegistry = "  ", MirrorRegistry = "m.example" }, new RegistryMirror { MatchRegistry = "a", MirrorRegistry = " " }],
        }).Should().HaveCount(2);

    [Fact]
    public void ResolveMasked_refuses_a_masked_token_whose_host_pattern_is_blank()
    {
        var submitted = DockerWith(("", WorkloadSecretMask.Sentinel));

        var act = () => rules.ResolveMasked(submitted, DockerWith(("a.example", "stored")));

        act.Should().Throw<WorkloadOrchestratorSettingsValidationException>().WithMessage("*hostPattern*");
    }

    [Fact]
    public void ResolveMasked_refuses_a_masked_token_when_the_stored_one_is_empty()
    {
        var act = () => rules.ResolveMasked(DockerWith(("a.example", WorkloadSecretMask.Sentinel)), DockerWith(("a.example", "")));

        act.Should().Throw<WorkloadOrchestratorSettingsValidationException>();
    }

    [Theory]
    [InlineData("docker", """{"RegistryCredentials":[{"hostPattern":"r.example.com","username":"u","token":"plain"}]}""")]
    [InlineData("kubernetes", """{"Kubeconfig":"apiVersion: v1 plain"}""")]
    public void A_mis_cased_secret_key_in_a_submission_is_not_kept(string provider, string entry)
    {
        var submitted = new WorkloadOrchestratorSettings().WithProvider(
            WorkloadProviderId.Parse(provider), System.Text.Json.JsonDocument.Parse(entry).RootElement);

        var resolved = rules.ResolveMasked(submitted, new WorkloadOrchestratorSettings());

        resolved.Providers.Values.Single().GetRawText().Should().NotContain("plain");
    }

    [Fact]
    public void ResolveMasked_hands_a_codec_null_when_the_stored_document_has_no_entry_for_it()
    {
        var nomad = WorkloadProviderId.Parse("nomad");
        var codec = new ScriptedCodec(nomad);
        var scripted = new WorkloadOrchestratorSettingsRules(SettingsKit.ShippedCatalog(), [codec]);
        var submitted = new WorkloadOrchestratorSettings().WithProvider(nomad, System.Text.Json.JsonDocument.Parse("{}").RootElement);

        scripted.ResolveMasked(submitted, new WorkloadOrchestratorSettings());

        codec.Resolved.Should().BeTrue();
        codec.SeenStored.Should().BeNull();
    }

    [Fact]
    public void Entries_of_a_provider_without_a_codec_pass_every_rule_untouched()
    {
        var nomad = WorkloadProviderId.Parse("nomad");
        var entry = System.Text.Json.JsonDocument.Parse("""{"token":"***"}""").RootElement;
        var document = new WorkloadOrchestratorSettings().WithProvider(nomad, entry);

        rules.Validate(document).Should().BeEmpty();
        rules.Mask(document).Providers[nomad].GetRawText().Should().Be(entry.GetRawText());
        rules.ResolveMasked(document, document).Providers[nomad].GetRawText().Should().Be(entry.GetRawText());
        rules.Secrets(document).Should().BeEmpty();
    }

    [Fact]
    public void Every_rule_refuses_a_null_document()
    {
        var any = new WorkloadOrchestratorSettings();

        FluentActions.Invoking(() => rules.Validate(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => rules.Mask(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => rules.Secrets(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => rules.ResolveMasked(null!, any)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => rules.ResolveMasked(any, null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Docker_placement_is_the_socket_and_an_unset_socket_equals_the_platform_default()
    {
        var codec = new DockerProviderSettingsCodec();
        var unset = codec.Placement(System.Text.Json.JsonDocument.Parse("{}").RootElement);
        var blank = codec.Placement(System.Text.Json.JsonDocument.Parse("""{"socketUri":" "}""").RootElement);
        var explicitDefault = codec.Placement(System.Text.Json.JsonDocument.Parse(
            $$"""{"socketUri":"{{unset["socketUri"]}}"}""").RootElement);

        unset.Keys.Should().Equal("socketUri");
        blank["socketUri"].Should().Be(unset["socketUri"]);
        explicitDefault["socketUri"].Should().Be(unset["socketUri"]);
    }

    [Fact]
    public void Kubernetes_placement_is_namespace_mode_and_the_resolved_server_of_a_remote_entry()
    {
        var codec = new KubernetesProviderSettingsCodec();
        var plain = codec.Placement(System.Text.Json.JsonSerializer.SerializeToElement(new KubernetesProviderSettings { Namespace = "a" }));
        var withLeftovers = codec.Placement(System.Text.Json.JsonSerializer.SerializeToElement(
            new KubernetesProviderSettings { Namespace = "a", Kubeconfig = SettingsKit.Kubeconfig(), KubeconfigContext = "c" }));
        var remote = codec.Placement(System.Text.Json.JsonSerializer.SerializeToElement(new KubernetesProviderSettings
        {
            Namespace = "a",
            ConnectionMode = KubernetesConnectionMode.RemoteKubeconfig,
            Kubeconfig = SettingsKit.Kubeconfig(),
        }));

        withLeftovers.Should().BeEquivalentTo(plain);
        plain.Keys.Should().BeEquivalentTo("namespace", "connectionMode");
        remote.Keys.Should().BeEquivalentTo("namespace", "connectionMode", "server");
        remote["server"].Should().Be("https://10.0.0.1:6443");
    }

    [Fact]
    public void Kubernetes_placement_of_an_unresolvable_kubeconfig_leaves_the_server_out_instead_of_throwing()
    {
        var codec = new KubernetesProviderSettingsCodec();

        var placement = codec.Placement(System.Text.Json.JsonSerializer.SerializeToElement(new KubernetesProviderSettings
        {
            ConnectionMode = KubernetesConnectionMode.RemoteKubeconfig,
            Kubeconfig = "not a kubeconfig: [",
        }));

        placement.Keys.Should().BeEquivalentTo("namespace", "connectionMode");
    }

    [Fact]
    public void Validate_refuses_a_remote_kubeconfig_that_cannot_be_parsed_as_a_shape_error()
        => rules.Validate(Remote("::: not yaml [")).Should().ContainSingle().Which.Message.Should().Contain("kubeconfig");

    [Fact]
    public void Validate_refuses_a_remote_kubeconfig_with_no_effective_context_as_a_shape_error()
        => rules.Validate(Remote(SelfContainedKubeconfig.Replace("current-context: remote", string.Empty)))
            .Should().ContainSingle().Which.Message.Should().Contain("context");

    [Fact]
    public void Validate_accepts_an_in_cluster_kubernetes_entry_with_no_kubeconfig()
        => rules.Validate(SettingsKit.Kubernetes(kubeconfig: null)).Should().BeEmpty();

    [Fact]
    public void Mask_leaves_an_empty_kubeconfig_empty()
        => KubernetesProviderSettings.From(rules.Mask(SettingsKit.Kubernetes(kubeconfig: null))).Kubeconfig.Should().BeNullOrEmpty();

    [Fact]
    public void Selecting_a_provider_that_is_not_compiled_in_gives_the_reason_the_build_states_for_that_provider()
    {
        var catalog = NSubstitute.Substitute.For<Abstractions.ICompiledProviderCatalog>();
        catalog.IsCompiledIn(Arg.Any<Abstractions.Contracts.WorkloadProviderId>()).Returns(false);
        catalog.GetProviders().Returns(
        [
            new WorkloadProviderInfo { Provider = KubernetesProvider.Id, CompiledIn = false, UnavailableReason = "other reason" },
            new WorkloadProviderInfo { Provider = DockerProvider.Id, CompiledIn = false, UnavailableReason = "vendor reason" },
        ]);
        var own = new WorkloadOrchestratorSettingsRules(catalog, []);

        own.Validate(new WorkloadOrchestratorSettings { Provider = DockerProvider.Id })
            .Should().ContainSingle().Which.Message.Should().Contain("vendor reason").And.NotContain("other reason");
    }
}
