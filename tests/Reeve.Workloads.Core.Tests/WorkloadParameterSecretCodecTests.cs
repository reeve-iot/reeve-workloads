using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core;
using Reeve.Workloads.DeploymentFixtures;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadParameterSecretCodecTests
{
    private readonly WorkloadParameterSecretCodec codec = new(
        new PrefixProtector());

    [Fact]
    public void Protects_password_values_and_round_trips_them()
    {
        var manifest = Workload("password", WorkloadParameterType.Password);
        var encoded = codec.Protect(manifest, Map(("password", "s3cret")));

        encoded["password"].Should().StartWith("enc:v1:").And.NotContain("s3cret");
        codec.Unprotect(manifest, encoded)["password"].Should().Be("s3cret");
    }

    [Fact]
    public void Keeps_refs_mask_sentinel_and_empty_values_unchanged()
    {
        var manifest = Workload("password", WorkloadParameterType.Password);
        codec.Unprotect(manifest, Map(("password", "")))["password"].Should().BeEmpty();
        codec.Unprotect(manifest, Map(("password", "ref:vault:path#key")))["password"].Should().Be("ref:vault:path#key");
        codec.Protect(manifest, Map(("password", "ref:vault:path#key")))["password"]
            .Should().Be("ref:vault:path#key");
        codec.Protect(manifest, Map(("password", "***")))["password"].Should().Be("***");
        codec.Protect(manifest, Map(("password", null)))["password"].Should().BeNull();
    }

    [Fact]
    public void A_password_literal_starting_with_the_storage_marker_is_still_protected_as_plaintext()
    {
        const string literal = "enc:v1:this-is-a-literal-password";
        var manifest = Workload("password", WorkloadParameterType.Password);
        var encoded = codec.Protect(manifest, Map(("password", literal)));

        encoded["password"].Should().NotBe(literal);
        codec.Unprotect(manifest, encoded)["password"].Should().Be(literal);
    }

    [Fact]
    public void A_stored_password_without_the_storage_marker_is_undecryptable_not_plaintext()
    {
        var manifest = Workload("password", WorkloadParameterType.Password);

        FluentActions.Invoking(() => codec.Unprotect(manifest, Map(("password", "plain-secret"))))
            .Should().Throw<Reeve.Workloads.Domain.DesiredSpec.WorkloadParameterResolutionException>()
            .WithMessage("*password*undecryptable*");
    }

    [Fact]
    public void A_non_password_value_starting_with_the_storage_marker_is_never_decoded()
    {
        var manifest = Workload("password", WorkloadParameterType.Password);
        var value = new Dictionary<string, string?> { ["plainText"] = WorkloadParameterSecretCodec.Prefix + "not-ciphertext" };

        codec.Unprotect(manifest, value)["plainText"].Should().Be(value["plainText"]);
    }

    [Fact]
    public void Protect_is_idempotent_for_authenticated_ciphertext_and_key_loss_stays_fail_closed()
    {
        var manifest = Workload("password", WorkloadParameterType.Password);
        var stored = codec.Protect(manifest, Map(("password", "secret")));
        codec.Protect(manifest, stored)["password"].Should().Be(stored["password"]);

        var unavailable = new WorkloadParameterSecretCodec(new RejectingProtector());
        FluentActions.Invoking(() => unavailable.Unprotect(manifest, stored))
            .Should().Throw<Reeve.Workloads.Domain.DesiredSpec.WorkloadParameterResolutionException>()
            .WithMessage("*password*undecryptable*");
    }

    [Fact]
    public void Read_modify_write_preserves_byte_identical_authenticated_ciphertext()
    {
        var manifest = Workload("password", WorkloadParameterType.Password);
        var stored = codec.Protect(manifest, Map(("password", "secret")));
        var plaintext = codec.Unprotect(manifest, stored);

        var rewritten = codec.ProtectAgainstStored(manifest, plaintext, stored);

        rewritten["password"].Should().Be(stored["password"]);
    }

    private static WorkloadManifest Workload(string name, WorkloadParameterType type)
        => DeploymentManifestBuilder.Workload(parameters: [DeploymentManifestBuilder.Param(name, required: false, type: type)]);

    private static IReadOnlyDictionary<string, string?> Map(params (string Key, string? Value)[] values)
        => values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    private sealed class PrefixProtector : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext));
        public byte[] Unprotect(byte[] protectedData)
        {
            try { return Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(protectedData)); }
            catch (FormatException ex) { throw new System.Security.Cryptography.CryptographicException("Invalid test payload.", ex); }
        }
    }

    private sealed class RejectingProtector : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) => throw new System.Security.Cryptography.CryptographicException("Key unavailable.");
        public IDataProtector CreateProtector(string purpose, string subPurpose) => this;
        public byte[] Protect(byte[] plaintext, string purpose, string subPurpose) => Protect(plaintext);
        public byte[] Unprotect(byte[] protectedData, string purpose, string subPurpose) => Unprotect(protectedData);
    }
}
