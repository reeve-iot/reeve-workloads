using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerImagePullFallbackCredentialTests
{
    private const string Registry = "registry.example.com:5000";
    private const string Image = Registry + "/reeve/virtual-devices@sha256:"
        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static RegistryCredential Cred(string user, string token) => new()
    {
        HostPattern = Registry,
        Username = user,
        Token = token,
        AuthType = RegistryAuthType.Basic,
    };

    [Fact]
    public void Registry_host_glob_rejects_a_trailing_line_terminator()
        => DockerRegistryAuth.HostMatches("registry.example", "registry.example\n").Should().BeFalse();

    [Fact]
    public void Appended_fallback_authenticates_the_pull_when_operator_credentials_are_empty()
    {
        var settings = new DockerProviderSettings
        {
            RegistryCredentials = new[] { Cred("oauth2", "official-index-env-token") },
        };

        var resolution = DockerRegistryAuth.Resolve(Image, settings);

        resolution.IsAnonymous.Should().BeFalse("a matching credential is present");
        resolution.Rejected.Should().BeFalse();
        resolution.Auth!.Username.Should().Be("oauth2");
        resolution.Auth.Password.Should().Be("official-index-env-token");
        resolution.Auth.ServerAddress.Should().Be(Registry);
    }

    [Fact]
    public void Operator_credential_wins_over_an_appended_fallback_for_the_same_host()
    {
        var settings = new DockerProviderSettings
        {
            RegistryCredentials = new[]
            {
                Cred("operator", "operator-token"),
                Cred("oauth2", "official-index-env-token"),
            },
        };

        var resolution = DockerRegistryAuth.Resolve(Image, settings);

        resolution.Auth!.Username.Should().Be("operator");
        resolution.Auth.Password.Should().Be("operator-token", "the operator credential takes precedence");
    }

    [Fact]
    public void No_credential_for_the_image_host_stays_anonymous()
    {
        var settings = new DockerProviderSettings();

        var resolution = DockerRegistryAuth.Resolve(Image, settings);

        resolution.IsAnonymous.Should().BeTrue();
        resolution.Auth.Should().BeNull();
    }
}
