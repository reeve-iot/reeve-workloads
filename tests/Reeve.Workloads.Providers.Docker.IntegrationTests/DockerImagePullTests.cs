using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

public sealed class DockerImagePullAuthResolutionTests
{
    private const string Registry = "registry.example.com:5000";
    private const string Secret = "super-secret-token-value-xyz";

    private static DockerProviderSettings SettingsWith(RegistryAuthType authType) => new()
    {
        RegistryCredentials = new[]
        {
            new RegistryCredential
            {
                HostPattern = Registry,
                Username = "reeve",
                Token = Secret,
                AuthType = authType,
            },
        },
    };

    [Fact]
    public void ResolveRegistryHost_extracts_explicit_host_and_defaults_docker_hub()
    {
        DockerRegistryAuth.ResolveRegistryHost($"{Registry}/reeve/thermostat:1.2.3").Should().Be(Registry);
        DockerRegistryAuth.ResolveRegistryHost($"{Registry}/reeve/thermostat@sha256:{new string('a', 64)}")
            .Should().Be(Registry);

        DockerRegistryAuth.ResolveRegistryHost("hello-world:latest")
            .Should().Be(DockerRegistryAuth.ImplicitDockerHubRegistry);
        DockerRegistryAuth.ResolveRegistryHost("library/alpine")
            .Should().Be(DockerRegistryAuth.ImplicitDockerHubRegistry);
    }

    [Fact]
    public void HostMatches_supports_exact_and_wildcard_patterns()
    {
        DockerRegistryAuth.HostMatches(Registry, Registry).Should().BeTrue();
        DockerRegistryAuth.HostMatches("*.example.com", "registry.example.com").Should().BeTrue();
        DockerRegistryAuth.HostMatches("*.example.com", "registry.example.com:5000").Should().BeFalse("the pattern has no port");
        DockerRegistryAuth.HostMatches("registry.example.com:5000", "docker.io").Should().BeFalse();
    }

    [Fact]
    public void Resolve_basic_credential_builds_username_password_serveraddress_authconfig()
    {
        var resolution = DockerRegistryAuth.Resolve(
            $"{Registry}/reeve/thermostat@sha256:{new string('b', 64)}",
            SettingsWith(RegistryAuthType.Basic));

        resolution.Rejected.Should().BeFalse();
        resolution.IsAnonymous.Should().BeFalse();
        resolution.AuthType.Should().Be(RegistryAuthType.Basic);

        resolution.Auth.Should().NotBeNull();
        resolution.Auth!.Username.Should().Be("reeve");
        resolution.Auth.Password.Should().Be(Secret);
        resolution.Auth.ServerAddress.Should().Be(Registry);
        resolution.Auth.RegistryToken.Should().BeNull("basic auth does not use the bearer registry-token field");
    }

    [Fact]
    public void Resolve_bearer_credential_builds_registrytoken_only_authconfig()
    {
        var resolution = DockerRegistryAuth.Resolve(
            $"{Registry}/reeve/thermostat:2.0.0",
            SettingsWith(RegistryAuthType.Bearer));

        resolution.Rejected.Should().BeFalse();
        resolution.IsAnonymous.Should().BeFalse();
        resolution.AuthType.Should().Be(RegistryAuthType.Bearer);

        resolution.Auth.Should().NotBeNull();
        resolution.Auth!.RegistryToken.Should().Be(Secret);
        resolution.Auth.Username.Should().BeNull();
        resolution.Auth.Password.Should().BeNull();
    }

    [Fact]
    public void Resolve_no_matching_credential_is_anonymous()
    {
        var resolution = DockerRegistryAuth.Resolve("hello-world:latest", SettingsWith(RegistryAuthType.Basic));

        resolution.Rejected.Should().BeFalse();
        resolution.IsAnonymous.Should().BeTrue();
        resolution.Auth.Should().BeNull();
        resolution.AuthType.Should().BeNull();
    }

    [Fact]
    public void Resolve_no_credentials_at_all_is_anonymous()
    {
        var resolution = DockerRegistryAuth.Resolve($"{Registry}/reeve/thermostat:1.0.0", new DockerProviderSettings());

        resolution.IsAnonymous.Should().BeTrue();
        resolution.Auth.Should().BeNull();
    }

    [Fact]
    public void Resolve_credentialed_plain_http_loopback_is_rejected_without_leaking_the_secret()
    {
        var settings = new DockerProviderSettings
        {
            RegistryCredentials = new[]
            {
                new RegistryCredential
                {
                    HostPattern = "localhost:5000",
                    Username = "reeve",
                    Token = Secret,
                    AuthType = RegistryAuthType.Basic,
                },
            },
        };

        var resolution = DockerRegistryAuth.Resolve(
            $"localhost:5000/reeve/thermostat@sha256:{new string('c', 64)}",
            settings);

        resolution.Rejected.Should().BeTrue("credentials must never traverse a plain-HTTP loopback registry");
        resolution.Auth.Should().BeNull();
        resolution.RejectionMessage.Should().NotBeNullOrWhiteSpace();
        resolution.RejectionMessage.Should().NotContain(Secret, "the fail-closed message must be credential-free");
    }

    [Fact]
    public void IsSecureRegistry_treats_the_whole_loopback_range_as_insecure()
    {
        DockerRegistryAuth.IsSecureRegistry("127.0.0.2:5000").Should().BeFalse("127.0.0.2 is in the 127.0.0.0/8 loopback range");
        DockerRegistryAuth.IsSecureRegistry("127.5.5.5").Should().BeFalse("127.5.5.5 is in the 127.0.0.0/8 loopback range");
        DockerRegistryAuth.IsSecureRegistry("127.0.0.1").Should().BeFalse();
        DockerRegistryAuth.IsSecureRegistry("::1").Should().BeFalse();
        DockerRegistryAuth.IsSecureRegistry("[::1]:5000").Should().BeFalse("a bracketed IPv6 loopback with a port is still loopback");
        DockerRegistryAuth.IsSecureRegistry("localhost:5000").Should().BeFalse();

        DockerRegistryAuth.IsSecureRegistry("registry.example.com:5000").Should().BeTrue();
        DockerRegistryAuth.IsSecureRegistry("docker.io").Should().BeTrue();
        DockerRegistryAuth.IsSecureRegistry("10.0.0.5:5000").Should().BeTrue("a private LAN address is not loopback");
    }

    [Fact]
    public void IsSecureRegistry_treats_ipv4_mapped_ipv6_loopback_as_insecure()
    {
        DockerRegistryAuth.IsSecureRegistry("::ffff:127.0.0.2").Should().BeFalse("::ffff:127.0.0.2 maps to the 127.0.0.0/8 loopback range");
        DockerRegistryAuth.IsSecureRegistry("[::ffff:127.0.0.2]:5000").Should().BeFalse("a bracketed mapped loopback with a port is still loopback");
        DockerRegistryAuth.IsSecureRegistry("::ffff:127.5.5.5").Should().BeFalse("::ffff:127.5.5.5 maps to the 127.0.0.0/8 loopback range");
        DockerRegistryAuth.IsSecureRegistry("::ffff:127.1.1.1").Should().BeFalse("::ffff:127.1.1.1 maps to the 127.0.0.0/8 loopback range");
        DockerRegistryAuth.IsSecureRegistry("::ffff:7f00:2").Should().BeFalse("::ffff:7f00:2 is the hex form of ::ffff:127.0.0.2");

        DockerRegistryAuth.IsSecureRegistry("127.0.0.2:5000").Should().BeFalse();
        DockerRegistryAuth.IsSecureRegistry("::1").Should().BeFalse();
        DockerRegistryAuth.IsSecureRegistry("127.0.0.1").Should().BeFalse();
        DockerRegistryAuth.IsSecureRegistry("localhost").Should().BeFalse();

        DockerRegistryAuth.IsSecureRegistry("registry.example.com:5000").Should().BeTrue();
        DockerRegistryAuth.IsSecureRegistry("::ffff:8.8.8.8").Should().BeTrue("a mapped non-loopback address (8.8.8.8) is not loopback");
    }

    [Fact]
    public void IsSecureRegistry_treats_trailing_dot_localhost_as_insecure()
    {
        DockerRegistryAuth.IsSecureRegistry("localhost.").Should().BeFalse("'localhost.' is the RFC 6761 root-anchored form of localhost");
        DockerRegistryAuth.IsSecureRegistry("LOCALHOST.").Should().BeFalse("the localhost check is case-insensitive under the trailing-dot strip");
        DockerRegistryAuth.IsSecureRegistry("localhost.:5000").Should().BeFalse("a trailing-dot localhost with a port is still loopback");

        DockerRegistryAuth.IsSecureRegistry("localhost").Should().BeFalse();
        DockerRegistryAuth.IsSecureRegistry("127.0.0.2").Should().BeFalse();
        DockerRegistryAuth.IsSecureRegistry("::ffff:127.0.0.2").Should().BeFalse();
        DockerRegistryAuth.IsSecureRegistry("::1").Should().BeFalse();

        DockerRegistryAuth.IsSecureRegistry("registry.example.com.").Should().BeTrue("a non-loopback FQDN is TLS even with a root-anchoring trailing dot");
        DockerRegistryAuth.IsSecureRegistry("registry.example.com:5000").Should().BeTrue();
        DockerRegistryAuth.IsSecureRegistry("::ffff:8.8.8.8").Should().BeTrue("a mapped non-loopback address (8.8.8.8) is not loopback");
    }

    [Fact]
    public void Resolve_credentialed_plain_http_trailing_dot_localhost_is_rejected_without_leaking_the_secret()
    {
        var settings = new DockerProviderSettings
        {
            RegistryCredentials = new[]
            {
                new RegistryCredential
                {
                    HostPattern = "localhost.:5000",
                    Username = "reeve",
                    Token = Secret,
                    AuthType = RegistryAuthType.Basic,
                },
            },
        };

        var resolution = DockerRegistryAuth.Resolve(
            $"localhost.:5000/reeve/thermostat@sha256:{new string('a', 64)}",
            settings);

        resolution.Rejected.Should().BeTrue(
            "a credential matched against the RFC 6761 trailing-dot form of localhost must fail closed — docker "
                + "DNS-resolves 'localhost.' to plain-HTTP loopback");
        resolution.Auth.Should().BeNull();
        resolution.RejectionMessage.Should().NotBeNullOrWhiteSpace();
        resolution.RejectionMessage.Should().NotContain(Secret, "the fail-closed message must be credential-free");
    }

    [Fact]
    public void Resolve_credentialed_plain_http_ipv4_mapped_ipv6_loopback_is_rejected_without_leaking_the_secret()
    {
        var settings = new DockerProviderSettings
        {
            RegistryCredentials = new[]
            {
                new RegistryCredential
                {
                    HostPattern = "[::ffff:127.0.0.2]:5000",
                    Username = "reeve",
                    Token = Secret,
                    AuthType = RegistryAuthType.Basic,
                },
            },
        };

        var resolution = DockerRegistryAuth.Resolve(
            $"[::ffff:127.0.0.2]:5000/reeve/thermostat@sha256:{new string('a', 64)}",
            settings);

        resolution.Rejected.Should().BeTrue(
            "a credential matched against an IPv4-mapped-IPv6 loopback host must fail closed — docker canonicalises "
                + "the mapped 127.0.0.0/8 to plain-HTTP loopback");
        resolution.Auth.Should().BeNull();
        resolution.RejectionMessage.Should().NotBeNullOrWhiteSpace();
        resolution.RejectionMessage.Should().NotContain(Secret, "the fail-closed message must be credential-free");
    }

    [Fact]
    public void Resolve_credentialed_plain_http_127_0_0_2_is_rejected_without_leaking_the_secret()
    {
        var settings = new DockerProviderSettings
        {
            RegistryCredentials = new[]
            {
                new RegistryCredential
                {
                    HostPattern = "127.0.0.2:5000",
                    Username = "reeve",
                    Token = Secret,
                    AuthType = RegistryAuthType.Basic,
                },
            },
        };

        var resolution = DockerRegistryAuth.Resolve(
            $"127.0.0.2:5000/reeve/thermostat@sha256:{new string('e', 64)}",
            settings);

        resolution.Rejected.Should().BeTrue(
            "a credential matched against a 127.0.0.0/8 loopback host must fail closed — docker treats the whole "
                + "range as plain-HTTP");
        resolution.Auth.Should().BeNull();
        resolution.RejectionMessage.Should().NotBeNullOrWhiteSpace();
        resolution.RejectionMessage.Should().NotContain(Secret, "the fail-closed message must be credential-free");
    }

    [Fact]
    public void Resolve_anonymous_pull_to_a_loopback_host_is_unaffected()
    {
        var resolution = DockerRegistryAuth.Resolve(
            $"127.0.0.2:5000/reeve/thermostat@sha256:{new string('f', 64)}",
            new DockerProviderSettings());

        resolution.Rejected.Should().BeFalse("there is no secret to leak on an anonymous loopback pull");
        resolution.IsAnonymous.Should().BeTrue();
        resolution.Auth.Should().BeNull();
    }

    [Fact]
    public void ParseReference_splits_digest_and_tag_for_the_pull_api()
    {
        var digest = $"{Registry}/reeve/thermostat@sha256:{new string('d', 64)}";
        var byDigest = DockerRegistryAuth.ParseReference(digest);
        byDigest.IsDigest.Should().BeTrue();
        byDigest.FromImage.Should().Be(digest, "a digest pull carries the digest on fromImage with no tag");
        byDigest.Tag.Should().BeNull();
        byDigest.RegistryHost.Should().Be(Registry);

        var byTag = DockerRegistryAuth.ParseReference($"{Registry}/reeve/thermostat:1.4.2");
        byTag.IsDigest.Should().BeFalse();
        byTag.FromImage.Should().Be($"{Registry}/reeve/thermostat");
        byTag.Tag.Should().Be("1.4.2");

        var bareName = DockerRegistryAuth.ParseReference("hello-world");
        bareName.FromImage.Should().Be("hello-world");
        bareName.Tag.Should().Be("latest", "a tagless reference defaults to :latest");

        var parseBlank = () => DockerRegistryAuth.ParseReference("   ");
        parseBlank.Should().Throw<ArgumentException>();
    }
}

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerImagePullTests
{
    private const string PublicImage = "hello-world:latest";

    private readonly DockerDaemonFixture _docker;

    public DockerImagePullTests(DockerDaemonFixture docker) => _docker = docker;

    [SkippableFact]
    public async Task PullAsync_public_image_without_credentials_succeeds_and_image_is_present()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        var puller = new DockerImagePuller();

        var result = await puller.PullAsync(client, PublicImage, new DockerProviderSettings(), null, CancellationToken.None);

        if (!result.Succeeded)
        {
            Skip.If(
                result.FailureKind == DockerFailureKind.Unreachable,
                $"Registry not reachable from this runner: {result.Message}");
        }

        result.Succeeded.Should().BeTrue($"the anonymous pull of '{PublicImage}' should succeed ({result.Message})");
        result.FailureKind.Should().Be(DockerFailureKind.None);

        try
        {
            var inspect = await client.Images.InspectImageAsync(PublicImage, CancellationToken.None);
            inspect.Should().NotBeNull("the pulled image must exist on the daemon");
        }
        finally
        {
            await RemoveImageBestEffortAsync(client, PublicImage);
        }
    }

    [SkippableFact]
    public async Task PullAsync_relays_live_layer_progress_to_the_reporter()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        var puller = new DockerImagePuller();

        await RemoveImageBestEffortAsync(client, PublicImage);

        var snapshots = new List<DockerPullProgress>();
        var reporter = new CollectingProgress(snapshots);

        var result = await puller.PullAsync(client, PublicImage, new DockerProviderSettings(), reporter, CancellationToken.None);

        if (!result.Succeeded)
        {
            Skip.If(
                result.FailureKind == DockerFailureKind.Unreachable,
                $"Registry not reachable from this runner: {result.Message}");
        }

        result.Succeeded.Should().BeTrue($"the anonymous pull of '{PublicImage}' should succeed ({result.Message})");

        snapshots.Should().NotBeEmpty("the puller must relay the real pull's progress to the reporter");
        snapshots.Should().OnlyContain(s => s.Message.Contains("Pulling image"), "every snapshot carries the operator line");
        snapshots.Should().OnlyContain(
            s => s.Percent == null || (s.Percent >= 0 && s.Percent <= 100),
            "a reported percent is always a clamped 0..100 (or null while indeterminate)");

        await RemoveImageBestEffortAsync(client, PublicImage);
    }

    private sealed class CollectingProgress(List<DockerPullProgress> sink) : IProgress<DockerPullProgress>
    {
        public void Report(DockerPullProgress value) => sink.Add(value);
    }

    [SkippableFact]
    public async Task PullAsync_nonexistent_reference_is_rejected_with_a_named_credential_free_message()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        var puller = new DockerImagePuller();

        var missing = $"docker.io/library/reeve-nonexistent-{Guid.NewGuid():N}:latest";

        var result = await puller.PullAsync(client, missing, new DockerProviderSettings(), null, CancellationToken.None);

        result.Succeeded.Should().BeFalse("a nonexistent image cannot be pulled");

        Skip.If(
            result.FailureKind == DockerFailureKind.Unreachable,
            $"Registry not reachable from this runner: {result.Message}");

        result.FailureKind.Should().Be(DockerFailureKind.Rejected);
        result.Message.Should().NotBeNullOrWhiteSpace("the named rejection must carry a helpful, credential-free message");
    }

    private static async Task RemoveImageBestEffortAsync(IDockerClient client, string image)
    {
        try
        {
            await client.Images
                .DeleteImageAsync(image, new ImageDeleteParameters { Force = true }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (DockerApiException)
        {
        }
    }
}
