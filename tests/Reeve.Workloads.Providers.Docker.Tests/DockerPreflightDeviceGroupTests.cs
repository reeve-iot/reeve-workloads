using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Xunit;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerPreflightDeviceGroupTests
{
    private const string DeviceGroupCheckId = "docker-device-group-resolved";

    private const string RenderNode = "/dev/dri/renderD128";

    [Fact]
    public async Task A_group_auto_slot_with_no_resolved_group_id_warns_that_the_device_may_be_unopenable()
    {
        var result = await ProbeAsync(joins: true, gids: Array.Empty<int>(), runAsRoot: false);

        var check = result.Checks.Should().ContainSingle(c => c.Id == DeviceGroupCheckId).Which;
        check.Passed.Should().BeFalse("a grant that resolved to nothing is not a silent success");
        check.Severity.Should().Be(PreflightSeverity.Warning);
        check.Message.Should().Contain(RenderNode, "a generic warning does not tell the operator which device breaks");
        check.Message.Should().Contain("GroupAdd", "on docker the missing rendering is HostConfig.GroupAdd");
        check.Message.Should().Contain("root");
        (check.Message + " " + check.FixSnippet).Should().Contain("discovery scan");

        result.Checks.Where(c => !c.Passed && c.Severity == PreflightSeverity.Error).Select(c => c.Id)
            .Should().NotContain(DeviceGroupCheckId, "an unresolved group id must never block a deploy");
    }

    [Fact]
    public async Task A_group_auto_slot_with_a_resolved_group_id_warns_about_nothing()
    {
        var result = await ProbeAsync(joins: true, gids: new[] { 993 }, runAsRoot: false);

        result.Checks.Should().NotContain(c => c.Id == DeviceGroupCheckId);
    }

    [Fact]
    public async Task A_workload_that_declares_no_group_grant_warns_about_nothing()
    {
        var result = await ProbeAsync(joins: false, gids: Array.Empty<int>(), runAsRoot: false);

        result.Checks.Should().NotContain(c => c.Id == DeviceGroupCheckId);
    }

    [Fact]
    public async Task A_root_workload_with_no_resolved_group_id_warns_about_nothing()
    {
        var result = await ProbeAsync(joins: true, gids: Array.Empty<int>(), runAsRoot: true);

        result.Checks.Should().NotContain(c => c.Id == DeviceGroupCheckId);
    }

    [Fact]
    public async Task A_spec_free_probe_emits_no_device_group_check()
    {
        var result = await PreflightWithNoDaemon().ProbeAsync(Settings(), CancellationToken.None);

        result.Checks.Should().NotContain(c => c.Id == DeviceGroupCheckId);
    }

    private static Task<DockerPreflightResult> ProbeAsync(bool joins, IReadOnlyList<int> gids, bool runAsRoot)
        => PreflightWithNoDaemon().ProbeAsync(
            Settings(),
            Spec(new ResolvedPrivileges
            {
                JoinsDeviceGroups = joins,
                DeviceGroupIds = gids,
                RunAsRoot = runAsRoot,
                DevicePaths = new[] { RenderNode },
            }),
            CancellationToken.None);

    private static DockerPreflight PreflightWithNoDaemon()
    {
        var factory = Substitute.For<IDockerClientFactory>();
        factory.CreateClient(Arg.Any<string?>()).Returns(_ => throw new InvalidOperationException("no daemon in this unit test"));
        return new DockerPreflight(factory);
    }

    private static WorkloadOrchestratorSettings Settings()
        => new WorkloadOrchestratorSettings()
            .WithProvider(DockerProvider.Id, new DockerProviderSettings { SocketUri = "unix:///nonexistent.sock" });

    private static WorkloadDesiredSpec Spec(ResolvedPrivileges privileges)
        => new()
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.transcode",
            WorkloadId = "transcode",
            InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Inst8 = "11111111",
            Provider = DockerProvider.Id,
            ResourceName = "reeve-wl-com-reeve-transcode-transcode-11111111",
            SecretName = "reeve-wl-com-reeve-transcode-transcode-11111111",
            Labels = new Dictionary<string, string>(),
            Image = "ghcr.io/reeve/transcode@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Kind = WorkloadKind.SingleNode,
            Privileges = privileges,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = "abc123def456",
        };
}
