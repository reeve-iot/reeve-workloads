using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerRollbackReportingTests
{
    private const string CandidateId = "candidate";
    private const string PreviousId = "previous";

    [Fact]
    public async Task The_rollback_message_names_the_exit_state_the_last_output_and_a_failed_restore()
    {
        var client = Substitute.For<IDockerClient>();
        client.Containers.InspectContainerAsync(CandidateId, Arg.Any<CancellationToken>())
            .Returns(new ContainerInspectResponse { State = new State { ExitCode = 3 } });
        client.Containers.GetContainerLogsAsync(CandidateId, Arg.Any<ContainerLogsParameters>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MultiplexedStream(
                new MemoryStream(Encoding.UTF8.GetBytes("starting\nfatal: config missing\n\n")), multiplexed: false));
        client.Containers.StartContainerAsync(PreviousId, Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DockerApiException(HttpStatusCode.InternalServerError, "no such image"));

        var result = await new DockerRollbackCoordinator().RollbackAsync(
            client, Spec(), Ref(CandidateId), Ref(PreviousId), CancellationToken.None);

        result.PreviousRestored.Should().BeFalse();
        result.Message.Should().Contain("exit code 3")
            .And.Contain("last output: fatal: config missing")
            .And.Contain("the previous container could not be restored");
    }

    [Fact]
    public void The_apply_failure_message_carries_the_rollback_outcome()
    {
        var rollback = new DockerRollbackResult { PreviousRestored = false, Message = "the previous container could not be restored." };

        DockerJournaledRecreate.WithRollback("the new container exited during the health gate", rollback)
            .Should().Be("the new container exited during the health gate; the previous container could not be restored.");
        DockerJournaledRecreate.WithRollback("start failed", null).Should().Be("start failed");
    }

    private static DockerContainerRef Ref(string id) => new() { Id = id, Name = id };

    private static WorkloadDesiredSpec Spec() => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "com.reeve.matter",
        WorkloadId = "matter",
        InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Inst8 = "11111111",
        Provider = DockerProvider.Id,
        ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
        SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
        Labels = new Dictionary<string, string>(),
        Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "abc123def456",
    };
}
