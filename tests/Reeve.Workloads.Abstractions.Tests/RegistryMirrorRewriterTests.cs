using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class RegistryMirrorRewriterTests
{
    private const string Digest = "sha256:" + "abc123def456" + "0000000000000000000000000000000000000000000000000000";

    private static IReadOnlyList<RegistryMirror> Mirrors(params (string match, string mirror)[] rules)
        => rules.Select(r => new RegistryMirror { MatchRegistry = r.match, MirrorRegistry = r.mirror }).ToList();

    [Fact]
    public void No_mirrors_returns_ref_unchanged()
    {
        var result = RegistryMirrorRewriter.Rewrite("docker.io/library/redis", Digest, mirrors: null);
        result.Should().Be("docker.io/library/redis");
    }

    [Fact]
    public void Empty_mirrors_returns_ref_unchanged()
    {
        var result = RegistryMirrorRewriter.Rewrite("registry.example.com/x/y", Digest, Mirrors());
        result.Should().Be("registry.example.com/x/y");
    }

    [Fact]
    public void No_matching_mirror_is_a_passthrough()
    {
        var result = RegistryMirrorRewriter.Rewrite(
            "registry.example.com/x/y", Digest, Mirrors(("docker.io", "mirror.internal")));
        result.Should().Be("registry.example.com/x/y");
    }

    [Fact]
    public void Matching_mirror_rewrites_only_the_host_keeping_the_repository_path()
    {
        var result = RegistryMirrorRewriter.Rewrite(
            "docker.io/library/redis", Digest, Mirrors(("docker.io", "mirror.example.com")));
        result.Should().Be("mirror.example.com/library/redis");
    }

    [Fact]
    public void Explicit_host_with_port_matches_exactly()
    {
        var result = RegistryMirrorRewriter.Rewrite(
            "registry.example.com:5000/team/app", Digest,
            Mirrors(("registry.example.com:5000", "mirror.example.com:5000")));
        result.Should().Be("mirror.example.com:5000/team/app");
    }

    [Fact]
    public void First_matching_mirror_wins()
    {
        var result = RegistryMirrorRewriter.Rewrite(
            "docker.io/library/redis", Digest,
            Mirrors(("docker.io", "first.mirror"), ("docker.io", "second.mirror")));
        result.Should().Be("first.mirror/library/redis");
    }

    [Fact]
    public void Bare_official_repo_normalizes_to_library_before_mirroring()
    {
        var result = RegistryMirrorRewriter.Rewrite(
            "redis", Digest, Mirrors(("docker.io", "mirror.internal")));
        result.Should().Be("mirror.internal/library/redis");
    }

    [Fact]
    public void Owner_repo_without_dotted_host_is_implicit_docker_io_user_path()
    {
        var result = RegistryMirrorRewriter.Rewrite(
            "grafana/grafana", Digest, Mirrors(("docker.io", "mirror.internal")));
        result.Should().Be("mirror.internal/grafana/grafana");
    }

    [Fact]
    public void Docker_io_host_match_is_case_insensitive()
    {
        var result = RegistryMirrorRewriter.Rewrite(
            "docker.io/library/redis", Digest, Mirrors(("Docker.IO", "mirror.internal")));
        result.Should().Be("mirror.internal/library/redis");
    }

    [Fact]
    public void Index_docker_io_is_equivalent_to_docker_io_for_matching()
    {
        var refWithIndex = RegistryMirrorRewriter.Rewrite(
            "index.docker.io/library/redis", Digest, Mirrors(("docker.io", "mirror.internal")));
        refWithIndex.Should().Be("mirror.internal/library/redis");

        var ruleWithIndex = RegistryMirrorRewriter.Rewrite(
            "redis", Digest, Mirrors(("index.docker.io", "mirror.internal")));
        ruleWithIndex.Should().Be("mirror.internal/library/redis");
    }

    [Fact]
    public void Explicit_docker_io_official_ref_is_mirrored_keeping_library_path()
    {
        var result = RegistryMirrorRewriter.Rewrite(
            "docker.io/library/redis", Digest, Mirrors(("docker.io", "mirror.internal")));
        result.Should().Be("mirror.internal/library/redis");
    }

    [Fact]
    public void Non_docker_registry_host_match_is_case_insensitive_and_only_host_changes()
    {
        var result = RegistryMirrorRewriter.Rewrite(
            "GHCR.io/owner/app", Digest, Mirrors(("ghcr.io", "mirror.internal")));
        result.Should().Be("mirror.internal/owner/app");
    }

    [Theory]
    [InlineData("docker.io/library/redis")]
    [InlineData("registry.example.com:5000/x/y")]
    [InlineData("ghcr.io/owner/app")]
    [InlineData("redis")]
    public void Rewrite_never_embeds_or_alters_the_digest(string imageRef)
    {
        var result = RegistryMirrorRewriter.Rewrite(
            imageRef, Digest, Mirrors(("docker.io", "m"), ("registry.example.com:5000", "m2"), ("ghcr.io", "m3")));
        result.Should().NotContain("@");
        result.Should().NotContain(Digest);
    }

    [Fact]
    public void A_host_rewrite_changes_the_effective_reference_so_a_mirrors_edit_is_a_spec_change()
    {
        var before = RegistryMirrorRewriter.Rewrite("docker.io/library/redis", Digest, mirrors: null);
        var after = RegistryMirrorRewriter.Rewrite(
            "docker.io/library/redis", Digest, Mirrors(("docker.io", "mirror.example.com")));
        after.Should().NotBe(before);
    }
}
