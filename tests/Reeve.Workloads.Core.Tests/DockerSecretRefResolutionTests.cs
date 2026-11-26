using Docker.DotNet.Models;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Core.Tests;

public sealed class DockerSecretRefResolutionTests
{
    private static readonly IReadOnlyDictionary<string, string> Resolved =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["tok"] = "the-token", ["foo"] = "foo-value" };

    [Fact]
    public void A_declared_credential_env_resolves_to_its_credential_under_any_name()
    {
        var parameters = Build([Credential("FOO_TOKEN", "foo")]);

        parameters.Env.Should().Contain("FOO_TOKEN=foo-value");
    }

    [Fact]
    public void A_secret_ref_with_neither_material_nor_a_credential_id_refuses_whatever_its_name()
    {
        var act = () => Build([new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef }]);

        act.Should().Throw<WorkloadSecretMaterialMissingException>()
            .WithMessage("*REEVE_WORKLOAD_TOKEN*");
    }

    [Fact]
    public void AnyOtherSecretRefWithNoMaterial_RefusesInsteadOfReceivingTheToken()
    {
        var act = () => Build([new ResolvedEnvVar { Name = "DB_PASSWORD", Kind = ResolvedEnvKind.SecretRef }]);

        act.Should().Throw<WorkloadSecretMaterialMissingException>()
            .WithMessage("*DB_PASSWORD*");
    }

    [Fact]
    public void ASecretRefWithMaterial_ResolvesToItsOwnValueAndNotTheToken()
    {
        var parameters = Build(
            [new ResolvedEnvVar { Name = "DB_PASSWORD", Kind = ResolvedEnvKind.SecretRef }],
            material: new Dictionary<string, string> { ["DB_PASSWORD"] = "hunter2" });

        parameters.Env.Should().Contain("DB_PASSWORD=hunter2");
        parameters.Env.Should().NotContain("DB_PASSWORD=the-token");
    }

    [Fact]
    public void Material_under_a_credential_envs_name_wins_over_the_credential_as_on_kubernetes()
    {
        var parameters = Build(
            [Credential("FOO_TOKEN", "foo")],
            material: new Dictionary<string, string> { ["FOO_TOKEN"] = "from-material" });

        parameters.Env.Should().Contain("FOO_TOKEN=from-material").And.NotContain("FOO_TOKEN=foo-value");
    }

    [Fact]
    public void A_provisioned_credential_is_added_to_the_container_env_as_the_token_env()
    {
        var parameters = Build(
            [Literal("LOG_LEVEL", "info")],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]);

        parameters.Env.Should().Contain("REEVE_WORKLOAD_TOKEN=the-token");
        parameters.Env.Should().ContainSingle(e => e.StartsWith("REEVE_WORKLOAD_TOKEN=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_provisioned_credential_is_not_added_when_an_env_of_that_name_is_already_there(bool declaredAsCredential)
    {
        var declared = declaredAsCredential
            ? Credential("REEVE_WORKLOAD_TOKEN", "tok")
            : Literal("REEVE_WORKLOAD_TOKEN", "author-wrote-this");

        var parameters = Build([declared], provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]);

        parameters.Env.Should().ContainSingle(e => e.StartsWith("REEVE_WORKLOAD_TOKEN=", StringComparison.Ordinal))
            .Which.Should().Be(declaredAsCredential ? "REEVE_WORKLOAD_TOKEN=the-token" : "REEVE_WORKLOAD_TOKEN=author-wrote-this");
    }

    [Fact]
    public void A_credential_env_whose_name_another_credential_also_claims_refuses_rather_than_carry_either()
    {
        var act = () => Build(
            [Credential("WORKLOAD_TOKEN", "foo")],
            provisioned: [new ProvisionedCredential("tok", "WORKLOAD_TOKEN")]);

        act.Should().Throw<WorkloadSecretMaterialMissingException>()
            .WithMessage("*WORKLOAD_TOKEN*");
    }

    [Fact]
    public void A_repeated_env_name_renders_one_container_env_entry_each_in_order()
    {
        var parameters = Build([Literal("FEED_URL", "first"), Literal("FEED_URL", "second")]);

        parameters.Env.Should().Equal("FEED_URL=first", "FEED_URL=second");
    }

    [Fact]
    public void A_repeated_name_every_entry_of_which_carries_one_credential_writes_it_into_each()
    {
        var parameters = Build([Credential("FOO_TOKEN", "foo"), Credential("FOO_TOKEN", "foo")]);

        parameters.Env.Should().Equal("FOO_TOKEN=foo-value", "FOO_TOKEN=foo-value");
    }

    [Fact]
    public void A_credential_entry_whose_name_a_literal_also_fills_refuses_rather_than_carry_the_credential()
    {
        var act = () => Build(
            [Literal("FOO_URL", "declared"), Credential("FOO_URL", "foo")],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]);

        act.Should().Throw<WorkloadSecretMaterialMissingException>().WithMessage("*FOO_URL*");
    }

    [Fact]
    public void A_literal_holding_an_id_is_written_as_the_literal_and_nothing_else_under_its_name()
    {
        var parameters = Build([Literal("FOO_URL", "declared") with { CredentialId = "foo" }]);

        parameters.Env.Should().Equal("FOO_URL=declared");
    }

    [Fact]
    public void One_credential_is_written_under_every_name_that_carries_its_id()
    {
        var parameters = Build(
            [Credential("WORKLOAD_TOKEN", "tok")],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]);

        parameters.Env.Should().Contain("WORKLOAD_TOKEN=the-token").And.Contain("REEVE_WORKLOAD_TOKEN=the-token");
    }

    [Fact]
    public void With_no_credential_provisioned_or_declared_no_token_env_is_added()
    {
        var parameters = Build([Literal("LOG_LEVEL", "info")]);

        parameters.Env.Should().Equal("LOG_LEVEL=info");
    }

    private static ResolvedEnvVar Credential(string name, string credentialId)
        => new() { Name = name, Kind = ResolvedEnvKind.SecretRef, SecretRef = "res", CredentialId = credentialId };

    private static ResolvedEnvVar Literal(string name, string value)
        => new() { Name = name, Kind = ResolvedEnvKind.Literal, Value = value };

    private static CreateContainerParameters Build(
        IReadOnlyList<ResolvedEnvVar> env,
        IReadOnlyDictionary<string, string>? material = null,
        IReadOnlyList<ProvisionedCredential>? provisioned = null)
    {
        var spec = new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.NewGuid(),
            GroupId = "mod",
            WorkloadId = "w",
            InstanceId = Guid.NewGuid(),
            Inst8 = "abcd1234",
            Provider = DockerProvider.Id,
            ResourceName = "res",
            SecretName = "res",
            Labels = new Dictionary<string, string>(),
            Image = "reg/img@sha256:" + new string('a', 64),
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = "unset",
            Env = env,
            SecretMaterial = material ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ProvisionedCredentials = provisioned ?? [],
        };

        return new DockerContainerManager().BuildContainerParameters(new DockerContainerRenderContext
        {
            Spec = spec,
            ContainerName = "res",
            Credentials = Resolved,
            SharedNetworkName = "reeve",
            Privilege = new DockerPrivilegeRenderer().RenderPrivileges(spec),
            Resource = new DockerResourceRenderer().RenderResources(spec),
            NetworkHostConfig = new HostConfig(),
        });
    }
}
