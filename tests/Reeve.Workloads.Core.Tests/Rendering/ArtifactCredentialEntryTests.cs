using System.Collections.Generic;
using FluentAssertions;
using static Reeve.Workloads.Core.Tests.Rendering.ArtifactSpecFactory;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Core.Rendering;
using Reeve.Workloads.Providers.Docker.Rendering;
using Reeve.Workloads.Providers.Kubernetes.Rendering;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Rendering;

public sealed class ArtifactCredentialEntryTests
{
    private const string Live = "rwt_live_credential";

    public static TheoryData<string> Formats => new() { "env", "compose", "helm-values", "kubernetes" };

    private static IWorkloadArtifactRenderer Renderer(string format) => format switch
    {
        "env" => new EnvArtifactRenderer(),
        "compose" => new ComposeArtifactRenderer(),
        "helm-values" => new HelmValuesArtifactRenderer(),
        _ => new KubernetesArtifactRenderer(),
    };

    private static ResolvedEnvVar Carrying(string name, string credentialId = TokenCredentialId)
        => new() { Name = name, Kind = ResolvedEnvKind.SecretRef, SecretRef = ResourceName, CredentialId = credentialId };

    private static ResolvedEnvVar Literal(string name, string value, string? credentialId = null)
        => new() { Name = name, Kind = ResolvedEnvKind.Literal, Value = value, CredentialId = credentialId };

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_credential_entry_whose_name_a_literal_also_fills_is_not_given_the_credential(string format)
    {
        var spec = Spec(env: [Literal("FEED_URL", "declared"), Carrying("FEED_URL")], provisioned: []);

        var artifact = Everything(Renderer(format).Render(spec, Revealing(Live)));

        artifact.Should().NotContain(Live);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_credential_entry_whose_name_a_password_parameter_also_fills_is_not_given_the_credential(string format)
    {
        var spec = Spec(env: [new ResolvedEnvVar { Name = "DB_PASSWORD", Kind = ResolvedEnvKind.SecretRef }, Carrying("DB_PASSWORD")], provisioned: []) with
        {
            SecretMaterial = new Dictionary<string, string>(StringComparer.Ordinal) { ["DB_PASSWORD"] = "hunter2" },
        };

        var artifact = Everything(Renderer(format).Render(spec, Revealing(Live)));

        artifact.Should().NotContain(Live).And.NotContain("hunter2");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_literal_carrying_an_id_reveals_no_credential_under_its_name(string format)
    {
        var spec = Spec(env: [Literal("FEED_URL", "declared", credentialId: TokenCredentialId)], provisioned: []);

        var artifact = Renderer(format).Render(spec, Revealing(Live));

        Everything(artifact).Should().NotContain(Live);
        artifact.SecretOneLiner.Should().BeNull();
    }

    [Fact]
    public void The_env_entries_a_duplicate_name_renders_are_the_literal_and_a_placeholder()
    {
        var spec = Spec(env: [Literal("FEED_URL", "declared"), Carrying("FEED_URL")]);

        var entries = WorkloadEnvRendering.Resolve(spec, Revealing(Live));

        entries.Should().ContainInOrder(
            new WorkloadEnvRendering.RenderedEnvEntry("FEED_URL", "declared", IsSecretPlaceholder: false),
            new WorkloadEnvRendering.RenderedEnvEntry("FEED_URL", WorkloadEnvRendering.SecretPlaceholder, IsSecretPlaceholder: true));
    }

    [Fact]
    public void Two_entries_of_one_name_carrying_the_same_credential_both_inline_it()
    {
        var spec = Spec(env: [Carrying("FEED_TOKEN"), Carrying("FEED_TOKEN")]);

        var entries = WorkloadEnvRendering.Resolve(spec, Revealing(Live));

        entries.Should().Contain(new WorkloadEnvRendering.RenderedEnvEntry("FEED_TOKEN", Live, IsSecretPlaceholder: false))
            .And.HaveCount(2);
        entries.Should().OnlyContain(e => e.Value == Live);
    }

    [Fact]
    public void Reeves_own_shape_inlines_the_credential_into_its_one_entry()
    {
        var entries = WorkloadEnvRendering.Resolve(Spec(), Revealing(Live));

        entries.Should().ContainSingle(e => e.Name == "REEVE_WORKLOAD_TOKEN")
            .Which.Should().Be(new WorkloadEnvRendering.RenderedEnvEntry("REEVE_WORKLOAD_TOKEN", Live, IsSecretPlaceholder: false));
    }
}
