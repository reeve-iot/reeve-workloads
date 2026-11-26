using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadSpecCredentialsTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static ResolvedEnvVar Carrying(string name, string credentialId)
        => new() { Name = name, Kind = ResolvedEnvKind.SecretRef, SecretRef = "wl-secret", CredentialId = credentialId };

    private static ResolvedEnvVar Literal(string name)
        => new() { Name = name, Kind = ResolvedEnvKind.Literal, Value = "declared" };

    private static IEnumerable<string> Shape(WorkloadSpecCredentials credentials)
        => credentials.Credentials.Select(c => $"{c.CredentialId}:{string.Join(",", c.Names)}");

    private static WorkloadDesiredSpec Spec(
        IReadOnlyList<ResolvedEnvVar>? env = null,
        IReadOnlyList<ProvisionedCredential>? provisioned = null,
        IReadOnlyDictionary<string, string>? material = null) => new()
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = InstanceId,
            Inst8 = "11111111",
            Provider = WorkloadProviderId.Parse("docker"),
            ResourceName = "wl-matter",
            SecretName = "wl-secret",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = "abc123def456",
            Env = env ?? [],
            ProvisionedCredentials = provisioned ?? [],
            SecretMaterial = material ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    [Fact]
    public void Provisioned_names_come_first_then_declared_ones_in_spec_order_and_each_once()
    {
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [Carrying("FEED", "feed"), Carrying("WORKLOAD_TOKEN", "tok"), Carrying("REEVE_WORKLOAD_TOKEN", "tok")],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]));

        Shape(credentials).Should().Equal("tok:REEVE_WORKLOAD_TOKEN,WORKLOAD_TOKEN", "feed:FEED");
        credentials.Names.Should().Equal("REEVE_WORKLOAD_TOKEN", "WORKLOAD_TOKEN", "FEED");
        credentials.Refusal.Should().BeNull();
    }

    [Fact]
    public void A_declared_literal_keeps_its_name_and_an_id_left_with_none_drops_out()
    {
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [Literal("REEVE_WORKLOAD_TOKEN")],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]));

        credentials.Credentials.Should().BeEmpty();
        credentials.Refusal.Should().BeNull();
    }

    [Fact]
    public void Secret_material_keeps_its_name_and_the_credential_keeps_the_rest()
    {
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [Carrying("REEVE_WORKLOAD_TOKEN", "tok"), Carrying("WORKLOAD_TOKEN", "tok")],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")],
            material: new Dictionary<string, string>(StringComparer.Ordinal) { ["REEVE_WORKLOAD_TOKEN"] = "from-material" }));

        Shape(credentials).Should().Equal("tok:WORKLOAD_TOKEN");
        credentials.UnbackedSecretRefs.Should().BeEmpty("the material backs the one reference the credential lost");
    }

    [Fact]
    public void A_name_two_ids_claim_is_neither_ids_and_the_spec_is_refused()
    {
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [Carrying("REEVE_WORKLOAD_TOKEN", "feed"), Carrying("FEED", "feed")],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]));

        credentials.Conflicts.Should().Equal("REEVE_WORKLOAD_TOKEN");
        Shape(credentials).Should().Equal("feed:FEED");
        credentials.Refusal.Should().Be("name(s) claimed by more than one credential: REEVE_WORKLOAD_TOKEN");
    }

    [Fact]
    public void One_id_claiming_a_name_twice_is_no_conflict()
    {
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [Carrying("REEVE_WORKLOAD_TOKEN", "tok")],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]));

        credentials.Conflicts.Should().BeEmpty();
        Shape(credentials).Should().Equal("tok:REEVE_WORKLOAD_TOKEN");
    }

    [Fact]
    public void A_secret_reference_is_backed_by_material_or_by_its_own_id_and_refused_otherwise()
    {
        var unbackedByAnything = new ResolvedEnvVar { Name = "DB_PASSWORD", Kind = ResolvedEnvKind.SecretRef, SecretRef = "wl-secret" };
        var lostItsName = Carrying("SHADOWED", "tok");
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [Carrying("OWN", "tok"), unbackedByAnything, lostItsName, Carrying("MATERIAL", "tok")],
            provisioned: [new ProvisionedCredential("other", "SHADOWED")],
            material: new Dictionary<string, string>(StringComparer.Ordinal) { ["MATERIAL"] = "m" }));

        credentials.UnbackedSecretRefs.Should().Equal("DB_PASSWORD", "SHADOWED");
        credentials.Refusal.Should().StartWith("name(s) claimed by more than one credential: SHADOWED",
            "a conflict is named before an unbacked reference");
    }

    [Fact]
    public void An_unbacked_secret_reference_alone_is_refused_by_name()
    {
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [new ResolvedEnvVar { Name = "DB_PASSWORD", Kind = ResolvedEnvKind.SecretRef, SecretRef = "wl-secret" }]));

        credentials.Refusal.Should().Be("secret reference(s) with no resolved material: DB_PASSWORD");
    }

    [Fact]
    public async Task Resolve_hands_the_first_non_empty_owned_value_as_existing_and_skips_a_null_answer()
    {
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [Carrying("WORKLOAD_TOKEN", "tok"), Carrying("FEED", "feed")],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]));
        var port = new RecordingCredentialPort { Answer = (_, id) => id == "feed" ? null : "answer" };
        var held = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REEVE_WORKLOAD_TOKEN"] = "",
            ["WORKLOAD_TOKEN"] = "held-tok",
            ["FEED"] = "",
        };

        var resolved = await credentials.ResolveAsync(port, InstanceId, held);

        port.Asks.Should().Equal((InstanceId, "tok", "held-tok"), (InstanceId, "feed", null));
        resolved.Should().Equal(new Dictionary<string, string> { ["tok"] = "answer" });
    }

    [Theory]
    [InlineData(null, "NAME")]
    [InlineData(" ", "NAME")]
    [InlineData("tok", null)]
    [InlineData("tok", "")]
    public void A_provisioned_credential_with_a_blank_id_or_name_is_refused_where_it_is_made(string? id, string? name)
    {
        var make = () => new ProvisionedCredential(id!, name!);

        make.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_provisioned_credential_stays_valid_through_a_with()
    {
        var credential = new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN");

        (credential with { Name = "FEED_TOKEN" }).Name.Should().Be("FEED_TOKEN");
        var blankName = () => credential with { Name = " " };
        var blankId = () => credential with { CredentialId = "" };
        blankName.Should().Throw<ArgumentException>();
        blankId.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_credentials_names_cannot_be_changed_through_a_cast_and_compare_by_value()
    {
        var spec = Spec(env: [Carrying("WORKLOAD_TOKEN", "tok")], provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]);

        var credential = WorkloadSpecCredentials.Of(spec).Credentials.Single();

        ((ICollection<string>)credential.Names).IsReadOnly.Should().BeTrue();
        credential.Should().Be(WorkloadSpecCredentials.Of(spec).Credentials.Single(), "equality is over the names, not the list instance");
        credential.GetHashCode().Should().Be(WorkloadSpecCredentials.Of(spec).Credentials.Single().GetHashCode());
    }

    [Theory]
    [InlineData(ResolvedEnvKind.Literal)]
    [InlineData(ResolvedEnvKind.SpecHash)]
    public void An_entry_that_is_not_a_secret_reference_claims_nothing_with_an_id_and_keeps_its_name(ResolvedEnvKind kind)
    {
        var forced = new ResolvedEnvVar { Name = "FEED_URL", Kind = kind, Value = "v", CredentialId = "feed" };
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [forced],
            provisioned: [new ProvisionedCredential("feed", "FEED_URL"), new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]));

        Shape(credentials).Should().Equal("tok:REEVE_WORKLOAD_TOKEN");
        credentials.CredentialFor(forced).Should().BeNull();
        credentials.Refusal.Should().BeNull();
    }

    [Fact]
    public void A_literal_holding_an_id_is_no_rival_claim_on_its_name()
    {
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [Literal("FEED_URL") with { CredentialId = "other" }, Carrying("FEED_TOKEN", "feed")],
            provisioned: [new ProvisionedCredential("feed", "FEED_URL")]));

        credentials.Conflicts.Should().BeEmpty();
        credentials.Refusal.Should().BeNull();
        Shape(credentials).Should().Equal("feed:FEED_TOKEN");
    }

    [Fact]
    public void A_secret_reference_with_a_blank_id_carries_nothing_and_is_refused_unbacked()
    {
        var blank = Carrying("FEED_TOKEN", " ");
        var credentials = WorkloadSpecCredentials.Of(Spec(env: [blank]));

        credentials.Credentials.Should().BeEmpty();
        credentials.CredentialFor(blank).Should().BeNull();
        credentials.UnbackedSecretRefs.Should().Equal("FEED_TOKEN");
    }

    [Fact]
    public void A_repeated_name_a_literal_also_fills_is_no_credentials_and_its_reference_is_unbacked()
    {
        var reference = Carrying("FEED_URL", "tok");
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [Literal("FEED_URL"), reference],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]));

        Shape(credentials).Should().Equal("tok:REEVE_WORKLOAD_TOKEN");
        credentials.CredentialFor(reference).Should().BeNull();
        credentials.UnbackedSecretRefs.Should().Equal("FEED_URL");
    }

    [Fact]
    public void A_repeated_name_a_password_parameter_also_fills_keeps_its_material_and_carries_no_credential()
    {
        var reference = Carrying("DB_PASSWORD", "tok");
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [new ResolvedEnvVar { Name = "DB_PASSWORD", Kind = ResolvedEnvKind.SecretRef }, reference],
            material: new Dictionary<string, string>(StringComparer.Ordinal) { ["DB_PASSWORD"] = "hunter2" }));

        credentials.Credentials.Should().BeEmpty();
        credentials.CredentialFor(reference).Should().BeNull();
        credentials.Refusal.Should().BeNull("the material backs both entries");
    }

    [Fact]
    public void A_repeated_name_every_entry_of_which_carries_one_id_is_that_credentials_once()
    {
        var first = Carrying("FEED_TOKEN", "feed");
        var second = Carrying("FEED_TOKEN", "feed");
        var credentials = WorkloadSpecCredentials.Of(Spec(env: [first, second]));

        Shape(credentials).Should().Equal("feed:FEED_TOKEN");
        credentials.CredentialFor(first).Should().Be("feed");
        credentials.CredentialFor(second).Should().Be("feed");
        credentials.Refusal.Should().BeNull();
    }

    [Fact]
    public void A_repeated_name_whose_entries_carry_two_ids_is_refused()
    {
        var credentials = WorkloadSpecCredentials.Of(Spec(env: [Carrying("FEED_TOKEN", "feed"), Carrying("FEED_TOKEN", "tok")]));

        credentials.Conflicts.Should().Equal("FEED_TOKEN");
        credentials.Credentials.Should().BeEmpty();
    }

    [Fact]
    public void Reeves_own_shape_is_one_credential_under_one_name_carried_by_its_entry()
    {
        var entry = Carrying("REEVE_WORKLOAD_TOKEN", "tok");
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [entry],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]));

        Shape(credentials).Should().Equal("tok:REEVE_WORKLOAD_TOKEN");
        credentials.CredentialFor(entry).Should().Be("tok");
    }

    [Fact]
    public void An_entry_whose_id_is_not_the_owner_of_its_name_carries_nothing()
    {
        var lost = Carrying("REEVE_WORKLOAD_TOKEN", "feed");
        var credentials = WorkloadSpecCredentials.Of(Spec(
            env: [Carrying("REEVE_WORKLOAD_TOKEN", "tok")],
            provisioned: [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")]));

        credentials.CredentialFor(lost).Should().BeNull("the name's owner is tok, not the id the entry holds");
    }
}
