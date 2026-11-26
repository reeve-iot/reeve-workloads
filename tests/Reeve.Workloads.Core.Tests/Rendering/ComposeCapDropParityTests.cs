using System;
using System.Collections.Generic;
using System.Linq;
using Docker.DotNet.Models;
using FluentAssertions;
using static Reeve.Workloads.Core.Tests.Rendering.ArtifactSpecFactory;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Docker.Rendering;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Rendering;

public sealed class ComposeCapDropParityTests
{
    private static readonly ComposeArtifactRenderer Compose = new();
    private static readonly DockerPrivilegeRenderer Docker = new();

    private static WorkloadArtifactRenderOptions External() =>
        new();

    [Fact]
    public void Consented_caps_render_cap_drop_all_then_exactly_the_consented_cap_add()
    {
        var spec = Spec(privileges: new ResolvedPrivileges
        {
            Capabilities = new[] { "NET_ADMIN", "NET_RAW" },
        });

        var content = Compose.Render(spec, External()).PrimaryContent;
        var (drop, add) = ParseComposeCaps(content);

        drop.Should().ContainSingle().Which.Should().Be("ALL",
            "the hardened baseline drops EVERY capability before layering the consented adds");
        add.Should().BeEquivalentTo(new[] { "NET_ADMIN", "NET_RAW" },
            "cap_add carries EXACTLY the consented capabilities — no more (over-privilege), no less (under-privilege)");
    }

    [Fact]
    public void No_privilege_block_still_emits_cap_drop_all_hardened_baseline()
    {
        var content = Compose.Render(Spec(), External()).PrimaryContent;
        var (drop, add) = ParseComposeCaps(content);

        drop.Should().ContainSingle().Which.Should().Be("ALL");
        add.Should().BeEmpty("with no consented capabilities the compose emits drop-ALL only, no cap_add");
    }

    [Fact]
    public void Privilege_block_without_capabilities_still_emits_cap_drop_all()
    {
        var spec = Spec(privileges: new ResolvedPrivileges { DbusHostPath = "/var/run/dbus" });

        var content = Compose.Render(spec, External()).PrimaryContent;
        var (drop, add) = ParseComposeCaps(content);

        drop.Should().ContainSingle().Which.Should().Be("ALL");
        add.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(CapabilityProfiles))]
    public void Compose_and_hostconfig_yield_the_same_effective_capability_set(ResolvedPrivileges? privileges)
    {
        var spec = Spec(privileges: privileges);

        var (composeDrop, composeAdd) = ParseComposeCaps(Compose.Render(spec, External()).PrimaryContent);

        var host = Docker.RenderPrivileges(spec).HostConfig;
        var hostDrop = host.CapDrop ?? new List<string>();
        var hostAdd = host.CapAdd ?? new List<string>();

        composeDrop.Should().BeEquivalentTo(new[] { "ALL" }, "compose drops ALL");
        hostDrop.Should().BeEquivalentTo(new[] { "ALL" }, "HostConfig drops ALL");

        Effective(composeDrop, composeAdd)
            .SetEquals(Effective(hostDrop, hostAdd))
            .Should().BeTrue(
                "the compose cap_drop:[ALL]+cap_add:[…] must yield the SAME effective capability set as the docker "
                + "HostConfig CapDrop:[\"ALL\"]+CapAdd:[…] for the SAME spec — a privilege-neutral managed↔manual switch");

        var consented = privileges?.Capabilities ?? Array.Empty<string>();
        Effective(composeDrop, composeAdd).Should().BeEquivalentTo(consented);
        Effective(hostDrop, hostAdd).Should().BeEquivalentTo(consented);
    }

    public static IEnumerable<object?[]> CapabilityProfiles() => new[]
    {
        new object?[] { null },
        new object?[] { new ResolvedPrivileges() },
        new object?[] { new ResolvedPrivileges { Capabilities = new[] { "NET_ADMIN" } } },
        new object?[] { new ResolvedPrivileges { Capabilities = new[] { "NET_ADMIN", "NET_RAW" } } },
        new object?[] { new ResolvedPrivileges { Privileged = true, Capabilities = new[] { "SYS_ADMIN" } } },
    };

    private static ISet<string> Effective(IEnumerable<string> drop, IEnumerable<string> add)
    {
        var dropSet = new HashSet<string>(drop, StringComparer.Ordinal);
        dropSet.Should().Contain("ALL", "the parity check assumes the hardened drop-ALL baseline on both sides");
        return new HashSet<string>(add, StringComparer.Ordinal);
    }

    private static (IReadOnlyList<string> Drop, IReadOnlyList<string> Add) ParseComposeCaps(string compose)
    {
        List<string>? drop = null;
        List<string>? add = null;
        List<string>? current = null;

        foreach (var raw in compose.Split('\n'))
        {
            var trimmed = raw.TrimEnd('\r').Trim();
            if (trimmed == "cap_drop:") { drop = new List<string>(); current = drop; continue; }
            if (trimmed == "cap_add:") { add = new List<string>(); current = add; continue; }

            if (current is null)
            {
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                current.Add(trimmed[2..].Trim());
            }
            else if (trimmed.Length > 0)
            {
                current = null;
            }
        }

        return (drop ?? new List<string>(), add ?? new List<string>());
    }
}
