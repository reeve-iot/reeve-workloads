using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Core.Bootstrap;
using Reeve.Workloads.Core.Tests.Manifest;
using Reeve.Workloads.Manifest;

namespace Reeve.Workloads.Core.Tests.Bootstrap;

public sealed class WorkloadHostBootstrapStepTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static WorkloadHostBootstrapRequest Request() => new()
    {
        InstanceId = InstanceId,
        Provider = WorkloadProviderId.Parse("docker"),
        WorkloadName = "controller",
        Extensions = WorkloadExtensions.None,
        HealthMode = WorkloadHealthMode.Container,
        DeclaredKeys = new HashSet<string>(StringComparer.Ordinal),
        SecretName = "per-instance-secret",
        Audience = WorkloadBootstrapAudience.Runtime,
    };

    private static WorkloadManifest Workload(params WorkloadEnv[] env) => new()
    {
        Id = "controller",
        DisplayName = "Controller",
        Image = new WorkloadImage { Component = "controller" },
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        Env = env,
    };

    private static Task<WorkloadHostBootstrapComposition> Compose(WorkloadHostBootstrapResult answer, WorkloadManifest? workload = null)
        => WorkloadHostBootstrapStep.ComposeAsync(new Answering(answer), Request(), workload ?? Workload(), CancellationToken.None);

    private static void RefusedFor(WorkloadHostBootstrapComposition composed, string name)
    {
        composed.Values.Should().BeNull();
        composed.Refusal.Should().Be(
            $"The host's bootstrap answer names '{name}' for more than one credential on workload 'controller'.");
    }

    [Fact]
    public async Task Two_provisioned_credentials_under_one_name_are_refused()
    {
        var composed = await Compose(new WorkloadHostBootstrapResult
        {
            ProvisionedCredentials = [new ProvisionedCredential("a", "TOKEN_ENV"), new ProvisionedCredential("b", "TOKEN_ENV")],
        });

        RefusedFor(composed, "TOKEN_ENV");
    }

    [Fact]
    public async Task A_provisioned_credential_under_the_name_of_an_env_backed_by_another_credential_is_refused()
    {
        var composed = await Compose(
            new WorkloadHostBootstrapResult
            {
                Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal) { ["other"] = BootstrapValue.HostCredential("b") },
                ProvisionedCredentials = [new ProvisionedCredential("a", "TOKEN_ENV")],
            },
            Workload(new WorkloadEnv { Name = "TOKEN_ENV", From = "bootstrap:other" }));

        RefusedFor(composed, "TOKEN_ENV");
    }

    [Fact]
    public async Task A_provisioned_credential_under_the_name_of_forced_env_backed_by_another_credential_is_refused()
    {
        var composed = await Compose(new WorkloadHostBootstrapResult
        {
            HostEnv = [new ResolvedEnvVar { Name = "TOKEN_ENV", Kind = ResolvedEnvKind.SecretRef, CredentialId = "b" }],
            ProvisionedCredentials = [new ProvisionedCredential("a", "TOKEN_ENV")],
        });

        RefusedFor(composed, "TOKEN_ENV");
    }

    [Fact]
    public async Task One_credential_under_a_provisioned_name_and_a_declared_env_of_that_name_is_not_a_clash()
    {
        var composed = await Compose(
            new WorkloadHostBootstrapResult
            {
                Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal) { ["token"] = BootstrapValue.HostCredential("a") },
                ProvisionedCredentials = [new ProvisionedCredential("a", "TOKEN_ENV")],
            },
            Workload(new WorkloadEnv { Name = "TOKEN_ENV", From = "bootstrap:token" }));

        composed.Refusal.Should().BeNull();
    }

    [Fact]
    public async Task A_declared_credential_env_the_host_replaces_with_forced_env_claims_nothing()
    {
        var composed = await Compose(
            new WorkloadHostBootstrapResult
            {
                Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal) { ["other"] = BootstrapValue.HostCredential("b") },
                HostEnv = [new ResolvedEnvVar { Name = "TOKEN_ENV", Kind = ResolvedEnvKind.Literal, Value = "x" }],
                ProvisionedCredentials = [new ProvisionedCredential("a", "TOKEN_ENV")],
            },
            Workload(new WorkloadEnv { Name = "TOKEN_ENV", From = "bootstrap:other" }));

        composed.Refusal.Should().BeNull();
    }

    [Theory]
    [InlineData("instanceId")]
    [InlineData("specHash")]
    public async Task A_host_value_under_a_library_key_is_refused(string key)
    {
        var composed = await Compose(new WorkloadHostBootstrapResult
        {
            Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal)
            {
                ["gatewayUrl"] = BootstrapValue.Literal("wss://x"),
                [key] = BootstrapValue.Literal("forged"),
            },
        });

        composed.Values.Should().BeNull();
        composed.Refusal.Should().Be(
            $"The host's bootstrap source answered the library's own key '{key}' for workload 'controller'.");
    }

    [Theory]
    [InlineData("REEVE_SPEC_HASH")]
    [InlineData("REEVE_APP_VERSION")]
    public async Task A_host_env_under_a_library_env_name_is_refused(string name)
    {
        var composed = await Compose(new WorkloadHostBootstrapResult
        {
            HostEnv =
            [
                new ResolvedEnvVar { Name = "REEVE_API_SCHEMA", Kind = ResolvedEnvKind.Literal, Value = "2" },
                new ResolvedEnvVar { Name = name, Kind = ResolvedEnvKind.Literal, Value = "forged" },
            ],
        });

        composed.Values.Should().BeNull();
        composed.Refusal.Should().Be(
            $"The host's bootstrap source forced the library's env '{name}' onto workload 'controller'.");
    }

    [Theory]
    [InlineData(ResolvedEnvKind.SecretRef)]
    [InlineData(ResolvedEnvKind.SpecHash)]
    public async Task A_host_env_left_out_of_the_hash_that_is_not_a_literal_is_refused(ResolvedEnvKind kind)
    {
        var composed = await Compose(new WorkloadHostBootstrapResult
        {
            HostEnv = [new ResolvedEnvVar { Name = "SOME_ENV", Kind = kind, SecretRef = "per-instance-secret", ExcludedFromSpecHash = true }],
        });

        composed.Values.Should().BeNull();
        composed.Refusal.Should().Be(
            $"The host's bootstrap source forced env 'SOME_ENV' as a {kind} outside the spec hash onto workload 'controller'; only a literal may be left out of the hash.");
    }

    [Fact]
    public async Task A_host_env_left_out_of_the_hash_that_is_a_literal_is_composed()
    {
        var env = new ResolvedEnvVar { Name = "SOME_ENV", Kind = ResolvedEnvKind.Literal, Value = "v", ExcludedFromSpecHash = true };

        var composed = await Compose(new WorkloadHostBootstrapResult { HostEnv = [env] });

        composed.Refusal.Should().BeNull();
        composed.HostEnv.Should().ContainSingle().Which.Should().Be(env);
    }

    [Theory]
    [InlineData("REEVE_SPEC_HASH")]
    [InlineData("REEVE_APP_VERSION")]
    public async Task A_provisioned_credential_under_a_library_env_name_is_refused(string name)
    {
        var composed = await Compose(new WorkloadHostBootstrapResult
        {
            ProvisionedCredentials = [new ProvisionedCredential("ok", "TOKEN_ENV"), new ProvisionedCredential("tok", name)],
        });

        composed.Values.Should().BeNull();
        composed.Refusal.Should().Be(
            $"The host's bootstrap source provisioned a credential under the library's env '{name}' for workload 'controller'.");
    }

    [Theory]
    [InlineData(ResolvedEnvKind.Literal, "tok")]
    [InlineData(ResolvedEnvKind.SpecHash, "tok")]
    [InlineData(ResolvedEnvKind.SecretRef, " ")]
    public async Task A_forced_env_carrying_an_id_it_cannot_carry_a_credential_under_is_refused(ResolvedEnvKind kind, string credentialId)
    {
        var composed = await Compose(new WorkloadHostBootstrapResult
        {
            HostEnv =
            [
                new ResolvedEnvVar { Name = "FEED_TOKEN", Kind = ResolvedEnvKind.SecretRef, CredentialId = "feed" },
                new ResolvedEnvVar { Name = "FEED_URL", Kind = kind, Value = "v", CredentialId = credentialId },
            ],
        });

        composed.Values.Should().BeNull();
        composed.Refusal.Should().Be(
            $"The host's bootstrap source forced env 'FEED_URL' as a {kind} carrying credential id '{credentialId}' onto workload 'controller'; only a secret reference with a non-blank id carries a credential.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1FEED")]
    [InlineData("FEED-URL")]
    [InlineData("FEED\nX=1")]
    [InlineData("FEED\n")]
    public async Task A_forced_env_outside_the_env_name_grammar_is_refused(string name)
    {
        var composed = await Compose(new WorkloadHostBootstrapResult
        {
            HostEnv =
            [
                new ResolvedEnvVar { Name = "FEED_URL", Kind = ResolvedEnvKind.Literal, Value = "v" },
                new ResolvedEnvVar { Name = name, Kind = ResolvedEnvKind.Literal, Value = "v" },
            ],
        });

        composed.Values.Should().BeNull();
        composed.Refusal.Should().Be(
            $"The host's bootstrap source forced an env named '{name}', which is not a valid env name, onto workload 'controller'.");
    }

    [Theory]
    [InlineData("1FEED")]
    [InlineData("FEED TOKEN")]
    [InlineData("FEED\n")]
    public async Task A_provisioned_credential_outside_the_env_name_grammar_is_refused(string name)
    {
        var composed = await Compose(new WorkloadHostBootstrapResult
        {
            ProvisionedCredentials = [new ProvisionedCredential("ok", "TOKEN_ENV"), new ProvisionedCredential("tok", name)],
        });

        composed.Values.Should().BeNull();
        composed.Refusal.Should().Be(
            $"The host's bootstrap source provisioned a credential under '{name}', which is not a valid env name, for workload 'controller'.");
    }

    [Fact]
    public async Task A_null_answer_breaks_the_sources_contract_and_names_the_source()
    {
        var act = () => Compose(null!);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(typeof(Answering).FullName!).And.Contain("no answer");
    }

    public static TheoryData<WorkloadHostBootstrapResult> NullCollections() =>
    [
        new WorkloadHostBootstrapResult { Values = null! },
        new WorkloadHostBootstrapResult { HostEnv = null! },
        new WorkloadHostBootstrapResult { ProvisionedCredentials = null! },
    ];

    [Theory]
    [MemberData(nameof(NullCollections))]
    public async Task A_null_collection_breaks_the_sources_contract_and_names_the_source(WorkloadHostBootstrapResult answer)
    {
        var act = () => Compose(answer);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(typeof(Answering).FullName!).And.Contain("null collection");
    }

    [Fact]
    public async Task The_cancellation_token_reaches_the_source()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Answering(WorkloadHostBootstrapResult.Empty);

        await WorkloadHostBootstrapStep.ComposeAsync(source, Request(), Workload(), cancellation.Token);

        source.Tokens.Should().Equal(cancellation.Token);
    }

    [Fact]
    public void The_parser_reserves_exactly_the_steps_library_keys()
    {
        WorkloadHostBootstrapStep.LibraryKeys.Should().NotBeEmpty();
        foreach (var key in WorkloadHostBootstrapStep.LibraryKeys)
        {
            var declared = () => ParseWith(new TestBootstrapValidator("acme", required: false, key), env: null);
            declared.Should().Throw<ArgumentException>($"a validator may not declare the library's key '{key}'");

            ParseWith(validator: null, env: $"bootstrap:{key}").Should().BeEmpty(
                $"the library resolves '{key}' itself, so a manifest may reference it undeclared");
        }

        var other = () => ParseWith(new TestBootstrapValidator("acme", required: false, "brokerUrl"), env: null);
        other.Should().NotThrow();
    }

    private static IReadOnlyList<DeploymentValidationError> ParseWith(IWorkloadExtensionValidator? validator, string? env)
    {
        var envJson = env is null ? string.Empty : $$""", "env": [ { "name": "X", "from": "{{env}}" } ]""";
        using var doc = JsonDocument.Parse($$"""
            { "schemaVersion": 1, "workloads": [ {
              "id": "w", "displayName": "W",
              "image": { "ref": "registry.example.com/reeve/w", "digest": "sha256:{{new string('0', 64)}}" },
              "kind": "service",
              "health": { "mode": "http", "http": { "port": 8080 } }{{envJson}}
            } ] }
            """);
        DeploymentManifestParser.Parse(
            doc.RootElement, validator is null ? [] : [validator], out var errors);
        return errors;
    }

    [Fact]
    public async Task A_host_refusal_passes_through_verbatim()
    {
        var composed = await Compose(WorkloadHostBootstrapResult.Refused("the broker is being rotated"));

        composed.Values.Should().BeNull();
        composed.HostEnv.Should().BeEmpty();
        composed.ProvisionedCredentials.Should().BeEmpty();
        composed.Refusal.Should().Be("the broker is being rotated");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_refusal_without_a_reason_cannot_be_built(string? reason)
    {
        var build = () => WorkloadHostBootstrapResult.Refused(reason!);

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task An_empty_answer_yields_exactly_the_librarys_two_keys()
    {
        var composed = await Compose(WorkloadHostBootstrapResult.Empty);

        composed.Refusal.Should().BeNull();
        composed.Values.Should().NotBeNull();
        composed.Values!.Keys.Should().BeEquivalentTo("instanceId", "specHash");
        composed.Values["instanceId"].Kind.Should().Be(ResolvedEnvKind.Literal);
        composed.Values["instanceId"].Value.Should().Be(InstanceId.ToString("D"));
        composed.Values["specHash"].Should().BeSameAs(BootstrapValue.SpecHash);
    }

    [Fact]
    public async Task The_hosts_values_env_and_credentials_pass_through_beside_the_librarys()
    {
        var credential = BootstrapValue.HostCredential("tok");
        var forced = new ResolvedEnvVar { Name = "REEVE_API_SCHEMA", Kind = ResolvedEnvKind.Literal, Value = "2" };
        var provisioned = new ProvisionedCredential("tok", "TOKEN_ENV");

        var composed = await Compose(new WorkloadHostBootstrapResult
        {
            Values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal)
            {
                ["gatewayUrl"] = BootstrapValue.Literal("wss://x"),
                ["workloadToken"] = credential,
            },
            HostEnv = [forced],
            ProvisionedCredentials = [provisioned],
        });

        composed.Refusal.Should().BeNull();
        composed.Values!.Keys.Should().BeEquivalentTo("gatewayUrl", "workloadToken", "instanceId", "specHash");
        composed.Values["gatewayUrl"].Value.Should().Be("wss://x");
        composed.Values["workloadToken"].Should().BeSameAs(credential);
        composed.HostEnv.Should().Equal(forced);
        composed.ProvisionedCredentials.Should().Equal(provisioned);
    }

    [Fact]
    public async Task The_source_is_asked_the_request_as_given()
    {
        var request = Request();
        var source = new Answering(WorkloadHostBootstrapResult.Empty);

        await WorkloadHostBootstrapStep.ComposeAsync(source, request, Workload(), CancellationToken.None);

        source.Asked.Should().ContainSingle().Which.Should().BeSameAs(request);
    }

    [Fact]
    public void Declared_keys_are_the_bootstrap_references_of_named_env_only()
    {
        var workload = new WorkloadManifest
        {
            Id = "controller",
            DisplayName = "Controller",
            Image = new WorkloadImage { Component = "controller" },
            Kind = WorkloadKind.Service,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Env =
            [
                new WorkloadEnv { Name = "A", From = "bootstrap:gatewayUrl" },
                new WorkloadEnv { Name = "B", From = "bootstrap:workloadHost:db" },
                new WorkloadEnv { Name = "C", From = "literal:bootstrap:nope" },
                new WorkloadEnv { Name = "D", From = "parameter:x" },
                new WorkloadEnv { Name = "", From = "bootstrap:unnamed" },
            ],
        };

        WorkloadHostBootstrapStep.DeclaredKeys(workload)
            .Should().BeEquivalentTo("gatewayUrl", "workloadHost:db");
    }

    [Fact]
    public void The_context_carries_the_hosts_forced_env_and_nothing_from_the_extensions()
    {
        var values = new Dictionary<string, BootstrapValue>(StringComparer.Ordinal) { ["specHash"] = BootstrapValue.SpecHash };
        var provisioned = new ProvisionedCredential("tok", "TOKEN_ENV");
        var forced = new ResolvedEnvVar { Name = "REEVE_API_SCHEMA", Kind = ResolvedEnvKind.Literal, Value = "3" };
        var composed = new WorkloadHostBootstrapComposition(values, [forced], [provisioned], Refusal: null);
        using var reeve = JsonDocument.Parse("""{ "apiSchema": "2" }""");
        var request = Request() with
        {
            Extensions = new WorkloadExtensions
            {
                ByNamespace = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["reeve"] = reeve.RootElement.Clone(),
                },
            },
        };

        var context = WorkloadHostBootstrapStep.Context(request, composed);

        context.InstanceId.Should().Be(InstanceId);
        context.SecretName.Should().Be("per-instance-secret");
        context.Values.Should().BeSameAs(values);
        context.HostEnv.Should().Equal(forced);
        context.ProvisionedCredentials.Should().Equal(provisioned);
    }

    private sealed class Answering(WorkloadHostBootstrapResult answer) : IWorkloadHostBootstrapSource
    {
        public List<WorkloadHostBootstrapRequest> Asked { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public Task<WorkloadHostBootstrapResult> GetAsync(
            WorkloadHostBootstrapRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            Tokens.Add(cancellationToken);
            return Task.FromResult(answer);
        }
    }
}
