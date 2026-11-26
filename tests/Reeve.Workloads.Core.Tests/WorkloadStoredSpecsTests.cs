using FluentAssertions;
using FluentAssertions.Execution;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain.DesiredSpec;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadStoredSpecsTests
{
    private const string Token = "REEVE_WORKLOAD_TOKEN";

    private static ResolvedEnvVar Reference(string name, string? credentialId = null)
        => new() { Name = name, Kind = ResolvedEnvKind.SecretRef, SecretRef = "wl-secret", CredentialId = credentialId };

    private static ResolvedEnvVar Literal(string name)
        => new() { Name = name, Kind = ResolvedEnvKind.Literal, Value = "declared" };

    private static ResolvedEnvVar AppVersion(string version)
        => new() { Name = WorkloadDesiredSpecFactory.AppVersionEnv, Kind = ResolvedEnvKind.Literal, Value = version };

    private static WorkloadDesiredSpec Spec(
        IReadOnlyList<ResolvedEnvVar>? env = null,
        IReadOnlyList<ProvisionedCredential>? provisioned = null,
        IReadOnlyDictionary<string, string>? material = null,
        string specHash = "abc123def456") => new()
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Inst8 = "11111111",
            Provider = WorkloadProviderId.Parse("docker"),
            ResourceName = "wl-matter",
            SecretName = "wl-secret",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = specHash,
            Env = env ?? [],
            ProvisionedCredentials = provisioned ?? [],
            SecretMaterial = material ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    [Fact]
    public void A_stored_reference_takes_the_id_the_render_gives_its_name_and_the_hash_does_not_move()
    {
        var stored = Spec(env: [Reference(Token), Literal("MODE")], specHash: "stored-hash");
        var rendered = Spec(
            env: [Reference(Token, "tok"), Literal("MODE")],
            provisioned: [new ProvisionedCredential("tok", Token)],
            specHash: "rendered-hash");

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.Env.Select(e => (e.Name, e.CredentialId)).Should().Equal((Token, "tok"), ("MODE", (string?)null));
        result.ProvisionedCredentials.Should().Equal(new ProvisionedCredential("tok", Token));
        result.SpecHash.Should().Be("stored-hash");
        WorkloadSpecCredentials.Of(result).Refusal.Should().BeNull();
    }

    [Fact]
    public void A_name_the_render_owns_only_through_a_provisioned_credential_gives_the_stored_reference_its_id()
    {
        var stored = Spec(env: [Reference(Token)]);
        var rendered = Spec(provisioned: [new ProvisionedCredential("tok", Token)]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        result.Env.Single().CredentialId.Should().Be("tok");
        WorkloadSpecCredentials.Of(result).Refusal.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_stored_id_is_no_id_and_takes_the_render_s(string blank)
    {
        var result = WorkloadStoredSpecs.WithHostValuesOf(
            Spec(env: [Reference(Token, blank)]), Spec(env: [Reference(Token, "tok")]));

        result.Env.Single().CredentialId.Should().Be("tok");
    }

    [Fact]
    public void A_stored_id_is_kept_and_a_stored_provisioned_list_is_kept()
    {
        var stored = Spec(env: [Reference(Token, "stored")], provisioned: [new ProvisionedCredential("stored", "OTHER")]);
        var rendered = Spec(env: [Reference(Token, "tok")], provisioned: [new ProvisionedCredential("tok", Token)]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.Env.Single().CredentialId.Should().Be("stored");
        result.ProvisionedCredentials.Should().Equal(new ProvisionedCredential("stored", "OTHER"));
    }

    [Fact]
    public void A_stored_spec_that_carries_a_credential_takes_no_provisioned_credential_from_the_render()
    {
        var stored = Spec(env: [Reference("FEED", "feed")]);
        var rendered = Spec(env: [Reference("FEED", "feed")], provisioned: [new ProvisionedCredential("tok", Token)]);

        WorkloadStoredSpecs.WithHostValuesOf(stored, rendered).ProvisionedCredentials.Should().BeEmpty(
            "only a spec with no credential at all was stored without them");
    }

    [Fact]
    public void A_stored_spec_with_no_credential_takes_the_render_s_provisioned_credentials()
    {
        var stored = Spec(env: [Literal("MODE")]);
        var rendered = Spec(env: [Literal("MODE")], provisioned: [new ProvisionedCredential("tok", Token)]);

        WorkloadStoredSpecs.WithHostValuesOf(stored, rendered).ProvisionedCredentials
            .Should().Equal(new ProvisionedCredential("tok", Token));
    }

    [Fact]
    public void A_render_of_another_version_s_provisioned_credential_stays_out_of_a_stored_spec_that_declares_none()
    {
        var stored = Spec(env: [AppVersion("2.5.0"), Literal("MODE")]);
        var rendered = Spec(
            env: [AppVersion("2.6.0"), Literal("MODE"), Reference(Token, "tok")],
            provisioned: [new ProvisionedCredential("tok", Token)]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.ProvisionedCredentials.Should().BeEmpty();
        WorkloadSpecCredentials.Of(result).Credentials.Should().BeEmpty();
    }

    [Fact]
    public void A_render_that_carries_no_version_takes_nothing_from_a_stored_spec_that_carries_one()
    {
        var stored = Spec(env: [AppVersion("2.5.0"), Reference(Token)]);
        var rendered = Spec(
            env: [Reference(Token, "tok")],
            provisioned: [new ProvisionedCredential("tok", Token)]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.ProvisionedCredentials.Should().BeEmpty();
        result.Env.Single(e => e.Name == Token).CredentialId.Should().BeNull();
    }

    [Fact]
    public void A_password_both_versions_declare_keeps_its_material_across_versions()
    {
        var stored = Spec(env: [AppVersion("2.6.0"), Reference("DB_PASSWORD")]);
        var rendered = Spec(
            env: [AppVersion("2.5.0"), Reference("DB_PASSWORD")],
            material: new Dictionary<string, string>(StringComparer.Ordinal) { ["DB_PASSWORD"] = "pw" });

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.SecretMaterial.Should().Equal(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["DB_PASSWORD"] = "pw" });
        WorkloadSpecCredentials.Of(result).Refusal.Should().BeNull();
    }

    [Fact]
    public void The_other_version_s_material_never_shadows_a_name_the_stored_spec_backs_with_a_credential()
    {
        var stored = Spec(env: [AppVersion("2.6.0"), Reference("DB_PASSWORD", "db")]);
        var rendered = Spec(
            env: [AppVersion("2.5.0"), Reference("DB_PASSWORD")],
            material: new Dictionary<string, string>(StringComparer.Ordinal) { ["DB_PASSWORD"] = "pw" });

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.SecretMaterial.Should().BeEmpty();
        WorkloadSpecCredentials.Of(result).CredentialFor(result.Env.Single(e => e.Name == "DB_PASSWORD"))
            .Should().Be("db");
    }

    [Fact]
    public void A_material_key_the_stored_spec_has_no_env_of_takes_nothing_from_another_version()
    {
        var stored = Spec(env: [AppVersion("2.6.0"), Literal("MODE")]);
        var rendered = Spec(
            env: [AppVersion("2.5.0"), Reference("DB_PASSWORD")],
            material: new Dictionary<string, string>(StringComparer.Ordinal) { ["DB_PASSWORD"] = "pw" });

        WorkloadStoredSpecs.WithHostValuesOf(stored, rendered).SecretMaterial.Should().BeEmpty();
    }

    [Fact]
    public void A_name_the_stored_spec_renders_as_a_literal_takes_no_material_from_another_version()
    {
        var stored = Spec(env: [AppVersion("2.6.0"), Literal("DB_PASSWORD")]);
        var rendered = Spec(
            env: [AppVersion("2.5.0"), Reference("DB_PASSWORD")],
            material: new Dictionary<string, string>(StringComparer.Ordinal) { ["DB_PASSWORD"] = "pw" });

        WorkloadStoredSpecs.WithHostValuesOf(stored, rendered).SecretMaterial.Should().BeEmpty();
    }

    [Fact]
    public void A_name_a_provisioned_credential_of_the_stored_spec_writes_takes_no_material_from_another_version()
    {
        var stored = Spec(
            env: [AppVersion("2.6.0"), Reference("DB_PASSWORD")],
            provisioned: [new ProvisionedCredential("db", "DB_PASSWORD")]);
        var rendered = Spec(
            env: [AppVersion("2.5.0"), Reference("DB_PASSWORD")],
            material: new Dictionary<string, string>(StringComparer.Ordinal) { ["DB_PASSWORD"] = "pw" });

        WorkloadStoredSpecs.WithHostValuesOf(stored, rendered).SecretMaterial.Should().BeEmpty();
    }

    [Fact]
    public void A_name_that_is_a_password_in_the_stored_version_does_not_take_the_other_version_s_credential()
    {
        var stored = Spec(env: [AppVersion("2.5.0"), Reference("DB_PASSWORD")]);
        var rendered = Spec(env: [AppVersion("2.6.0"), Reference("DB_PASSWORD", "db")]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.Env.Single(e => e.Name == "DB_PASSWORD").CredentialId.Should().BeNull();
        WorkloadSpecCredentials.Of(result).UnbackedSecretRefs.Should().Equal("DB_PASSWORD");
    }

    [Fact]
    public void A_same_version_render_still_grafts_the_missing_id_onto_a_stored_spec()
    {
        var stored = Spec(env: [AppVersion("2.5.0"), Reference(Token)]);
        var rendered = Spec(
            env: [AppVersion("2.5.0"), Reference(Token, "tok")],
            provisioned: [new ProvisionedCredential("tok", Token)]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.Env.Single(e => e.Name == Token).CredentialId.Should().Be("tok");
        result.ProvisionedCredentials.Should().Equal(new ProvisionedCredential("tok", Token));
        WorkloadSpecCredentials.Of(result).Refusal.Should().BeNull();
    }

    [Fact]
    public void A_reference_the_render_does_not_own_stays_id_less_and_is_refused_by_name()
    {
        var stored = Spec(env: [Reference(Token), Reference("NEW_SECRET")]);
        var rendered = Spec(env: [Reference(Token, "tok")]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.Env.Single(e => e.Name == "NEW_SECRET").CredentialId.Should().BeNull();
        WorkloadSpecCredentials.Of(result).UnbackedSecretRefs.Should().Equal("NEW_SECRET");
    }

    [Fact]
    public void A_name_the_render_fills_with_material_takes_the_material_and_no_id()
    {
        var material = new Dictionary<string, string>(StringComparer.Ordinal) { ["DB_PASSWORD"] = "pw" };
        var stored = Spec(env: [Reference("DB_PASSWORD")]);
        var rendered = Spec(env: [Reference("DB_PASSWORD")], material: material);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.SecretMaterial.Should().BeSameAs(material);
        result.Env.Single().CredentialId.Should().BeNull();
        WorkloadSpecCredentials.Of(result).Refusal.Should().BeNull();
    }

    [Fact]
    public void A_literal_of_a_name_the_render_owns_is_left_alone()
    {
        var result = WorkloadStoredSpecs.WithHostValuesOf(
            Spec(env: [Literal(Token)]), Spec(env: [Reference(Token, "tok")]));

        result.Env.Single().Should().Be(Literal(Token));
    }

    [Fact]
    public void A_stored_name_two_ids_claim_is_still_refused()
    {
        var stored = Spec(env: [Reference(Token, "a")], provisioned: [new ProvisionedCredential("b", Token)]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, Spec(env: [Reference(Token, "tok")]));

        WorkloadSpecCredentials.Of(result).Conflicts.Should().Equal(Token);
    }

    [Fact]
    public void An_unmigrated_host_s_id_less_render_env_leaves_a_same_name_provisioned_credential_unowned()
    {
        var stored = Spec(env: [Reference(Token)]);
        var rendered = Spec(env: [Reference(Token)], provisioned: [new ProvisionedCredential("tok", Token)]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.Env.Single().CredentialId.Should().BeNull();
        WorkloadSpecCredentials.Of(result).UnbackedSecretRefs.Should().Equal(Token);
    }

    [Fact]
    public void A_name_two_ids_claim_in_the_render_leaves_the_stored_reference_unbacked()
    {
        var stored = Spec(env: [Reference(Token)]);
        var rendered = Spec(env: [Reference(Token, "a")], provisioned: [new ProvisionedCredential("b", Token)]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.Env.Single().CredentialId.Should().BeNull();
        WorkloadSpecCredentials.Of(result).UnbackedSecretRefs.Should().Equal(Token);
    }

    private static ResolvedEnvVar Unhashed(string name, string value)
        => new() { Name = name, Kind = ResolvedEnvKind.Literal, Value = value, ExcludedFromSpecHash = true };

    [Fact]
    public void A_stored_spec_takes_the_current_unhashed_value_from_the_render()
    {
        var stored = Spec(env: [AppVersion("1.0.0"), Unhashed("EXCLUDED", "old")]);
        var rendered = Spec(env: [AppVersion("1.0.0"), Unhashed("EXCLUDED", "new")]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.Env.Single(e => e.Name == "EXCLUDED").Should().Be(Unhashed("EXCLUDED", "new"));
        result.Env.Single(e => e.Name == WorkloadDesiredSpecFactory.AppVersionEnv).Value.Should().Be("1.0.0");
    }

    [Fact]
    public void A_stored_spec_of_another_version_takes_the_current_unhashed_value_too()
    {
        var stored = Spec(env: [AppVersion("1.0.0"), Unhashed("EXCLUDED", "old")]);
        var rendered = Spec(env: [AppVersion("2.0.0"), Unhashed("EXCLUDED", "new")]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, rendered);

        using var scope = new AssertionScope();
        result.Env.Single(e => e.Name == "EXCLUDED").Value.Should().Be("new");
        result.Env.Single(e => e.Name == WorkloadDesiredSpecFactory.AppVersionEnv).Value.Should().Be("1.0.0");
    }

    [Fact]
    public void A_stored_unhashed_value_the_render_does_not_supply_is_kept()
    {
        var stored = Spec(env: [AppVersion("1.0.0"), Unhashed("EXCLUDED", "old")]);

        var result = WorkloadStoredSpecs.WithHostValuesOf(stored, Spec(env: [AppVersion("1.0.0")]));

        result.Env.Single(e => e.Name == "EXCLUDED").Value.Should().Be("old");
    }

    [Fact]
    public void A_stored_unhashed_value_is_kept_when_the_render_answers_that_name_as_an_ordinary_literal()
    {
        var stored = Spec(env: [AppVersion("1.0.0"), Unhashed("EXCLUDED", "old")]);
        var rendered = Spec(env: [AppVersion("1.0.0"), new ResolvedEnvVar { Name = "EXCLUDED", Kind = ResolvedEnvKind.Literal, Value = "new" }]);

        WorkloadStoredSpecs.WithHostValuesOf(stored, rendered).Env.Single(e => e.Name == "EXCLUDED").Should().Be(Unhashed("EXCLUDED", "old"));
    }

    [Fact]
    public void An_ordinary_stored_literal_is_never_replaced_by_the_render()
    {
        var stored = Spec(env: [AppVersion("1.0.0"), Literal("PLAIN")]);
        var rendered = Spec(env: [AppVersion("1.0.0"), Unhashed("PLAIN", "new")]);

        WorkloadStoredSpecs.WithHostValuesOf(stored, rendered).Env.Single(e => e.Name == "PLAIN").Should().Be(Literal("PLAIN"));
    }
}
