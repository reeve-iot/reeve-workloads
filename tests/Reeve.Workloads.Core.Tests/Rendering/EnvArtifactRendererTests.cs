using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using static Reeve.Workloads.Core.Tests.Rendering.ArtifactSpecFactory;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Core.Rendering;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Rendering;

public sealed class EnvArtifactRendererTests
{
    private static readonly EnvArtifactRenderer Renderer = new();

    private static WorkloadArtifactRenderOptions Reveal(string token = "rwt_secrettoken123") =>
        Revealing(token);

    private static WorkloadArtifactRenderOptions External() =>
        new();

    [Fact]
    public void Format_is_env()
    {
        Renderer.Format.Should().Be(WorkloadArtifactFormatId.Parse("env"));
        Renderer.IsSelfContained.Should().BeFalse();
        Renderer.Render(Spec(), External()).Format.Should().Be(WorkloadArtifactFormatId.Parse("env"));
    }

    [Fact]
    public void External_secret_mode_inlines_no_credential_even_when_one_was_revealed()
    {
        var options = new WorkloadArtifactRenderOptions
        {
            ExternalSecretMode = true,
            RevealedCredentials = Reveal().RevealedCredentials,
        };

        var content = Renderer.Render(Spec(), options).PrimaryContent;

        content.Should().Contain($"REEVE_WORKLOAD_TOKEN={WorkloadEnvRendering.SecretPlaceholder}")
            .And.NotContain("rwt_secrettoken123");
    }

    [Fact]
    public void Literal_and_spec_hash_env_render_key_equals_value()
    {
        var content = Renderer.Render(Spec(), External()).PrimaryContent;

        content.Should().Contain("LOG_LEVEL=info");
        content.Should().Contain($"REEVE_GATEWAY_URL={GatewayUrl}");
        content.Should().Contain($"REEVE_SPEC_HASH={SpecHashValue}");
    }

    [Fact]
    public void An_env_flagged_out_of_the_hash_renders_its_current_value_like_a_literal()
    {
        var spec = Spec(env: [new ResolvedEnvVar { Name = "EXCLUDED", Kind = ResolvedEnvKind.Literal, Value = "current", ExcludedFromSpecHash = true }]);

        Renderer.Render(spec, External()).PrimaryContent.Should().Contain("EXCLUDED=current");
    }

    [Fact]
    public void Gateway_url_is_present()
    {
        Renderer.Render(Spec(), External()).PrimaryContent
            .Should().Contain($"REEVE_GATEWAY_URL={GatewayUrl}");
    }

    [Fact]
    public void Env_lines_are_sorted_by_name_for_stable_output()
    {
        var content = Renderer.Render(Spec(), External()).PrimaryContent;
        var keys = content
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .Select(l => l[..l.IndexOf('=')])
            .ToList();

        keys.Should().BeInAscendingOrder();
        keys.Should().Equal("LOG_LEVEL", "REEVE_GATEWAY_URL", "REEVE_SPEC_HASH", "REEVE_WORKLOAD_TOKEN");
    }

    [Fact]
    public void Revealed_token_is_inlined_for_the_workload_token_secret_ref()
    {
        var content = Renderer.Render(Spec(), Reveal("rwt_abc")).PrimaryContent;

        content.Should().Contain("REEVE_WORKLOAD_TOKEN=rwt_abc");
        content.Should().NotContain("<set-this-secret>");
    }

    [Fact]
    public void External_secret_mode_renders_a_placeholder_and_a_note_never_a_value()
    {
        var result = Renderer.Render(Spec(), External());

        result.PrimaryContent.Should().Contain("REEVE_WORKLOAD_TOKEN=<set-this-secret>");
        result.Notes.Should().Contain(n => n.Contains("REEVE_WORKLOAD_TOKEN"));
    }

    [Fact]
    public void A_chmod_600_note_is_always_present()
    {
        Renderer.Render(Spec(), Reveal()).Notes.Should().Contain(n => n.Contains("chmod 600"));
        Renderer.Render(Spec(), External()).Notes.Should().Contain(n => n.Contains("chmod 600"));
    }

    [Fact]
    public void A_non_credential_secret_ref_renders_a_placeholder_even_when_a_credential_is_revealed()
    {
        var spec = Spec(env: new[]
        {
            new ResolvedEnvVar { Name = "REEVE_GATEWAY_URL", Kind = ResolvedEnvKind.Literal, Value = GatewayUrl },
            new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "REEVE_WORKLOAD_TOKEN", CredentialId = TokenCredentialId },
            new ResolvedEnvVar { Name = "API_KEY", Kind = ResolvedEnvKind.SecretRef, SecretRef = "API_KEY" },
        });

        var content = Renderer.Render(spec, Reveal("rwt_xyz")).PrimaryContent;

        content.Should().Contain("REEVE_WORKLOAD_TOKEN=rwt_xyz");
        content.Should().Contain("API_KEY=<set-this-secret>");
    }

    [Fact]
    public void A_declared_credential_is_inlined_by_its_id_whatever_its_name()
    {
        var spec = Spec(env: new[]
        {
            new ResolvedEnvVar { Name = "FOO_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = ResourceName, CredentialId = "foo" },
        });
        var options = new WorkloadArtifactRenderOptions
        {
            RevealedCredentials = new Dictionary<string, string> { ["foo"] = "foo-value" },
        };

        Renderer.Render(spec, options).PrimaryContent.Should().Contain("FOO_TOKEN=foo-value");
    }

    [Fact]
    public void A_password_is_never_inlined_even_under_a_revealed_key_of_its_own_name()
    {
        var spec = Spec(env: new[]
        {
            new ResolvedEnvVar { Name = "DB_PASSWORD", Kind = ResolvedEnvKind.SecretRef, SecretRef = ResourceName },
        }) with
        {
            SecretMaterial = new Dictionary<string, string> { ["DB_PASSWORD"] = "hunter2" },
        };
        var options = new WorkloadArtifactRenderOptions
        {
            RevealedCredentials = new Dictionary<string, string> { ["DB_PASSWORD"] = "leaked", [TokenCredentialId] = "rwt" },
        };

        var content = Renderer.Render(spec, options).PrimaryContent;

        content.Should().Contain("DB_PASSWORD=<set-this-secret>").And.NotContain("leaked").And.NotContain("hunter2");
    }

    [Fact]
    public void I4_render_adopted_claim_volume_surfaces_a_must_exist_note_naming_the_physical_volume()
    {
        var result = Renderer.Render(Spec(volumes: new[]
        {
            new ResolvedVolume
            {
                Id = "state", MountPath = "/state", Size = "2Gi",
                Criticality = VolumeCriticality.Critical,
                ResourceName = "reeve-wl-pvc-retained-state",
                ExistingClaim = true,
            },
        }), External());

        result.Notes.Should().Contain(n =>
            n.Contains("reeve-wl-pvc-retained-state")
            && n.Contains("/state")
            && n.Contains("must already exist"));
    }

    [Fact]
    public void I4_render_multiple_adopted_claims_are_all_named_in_the_note()
    {
        var result = Renderer.Render(Spec(volumes: new[]
        {
            new ResolvedVolume
            {
                Id = "state", MountPath = "/state", Size = "2Gi",
                Criticality = VolumeCriticality.Critical,
                ResourceName = "reeve-wl-pvc-retained-state", ExistingClaim = true,
            },
            new ResolvedVolume
            {
                Id = "media", MountPath = "/media", Size = "10Gi",
                Criticality = VolumeCriticality.Critical,
                ResourceName = "reeve-wl-pvc-retained-media", ExistingClaim = true,
            },
        }), External());

        result.Notes.Should().Contain(n =>
            n.Contains("reeve-wl-pvc-retained-state") && n.Contains("reeve-wl-pvc-retained-media"));
    }

    [Fact]
    public void I4_render_a_fresh_only_spec_emits_no_restore_note()
    {
        Renderer.Render(Spec(), External()).Notes
            .Should().NotContain(n => n.Contains("must already exist"));

        Renderer.Render(Spec(volumes: new[]
        {
            new ResolvedVolume
            {
                Id = "data", MountPath = "/data", Size = "1Gi",
                Criticality = VolumeCriticality.Critical, ResourceName = "pvc-data",
            },
        }), External()).Notes
            .Should().NotContain(n => n.Contains("must already exist"));
    }

    [Fact]
    public void Golden_env_external_secret_mode()
    {
        var content = Renderer.Render(Spec(), External()).PrimaryContent;

        content.Should().Be(ArtifactGoldens.EnvExternalSecret);
    }

    [Fact]
    public void Golden_env_revealed_token()
    {
        var content = Renderer.Render(Spec(), Reveal("rwt_goldentoken")).PrimaryContent;

        content.Should().Be(ArtifactGoldens.EnvRevealedToken);
    }
}
