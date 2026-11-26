using System.Net;
using Docker.DotNet;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerPortCollisionClassificationTests
{
    [Theory]
    [InlineData("failed to set up container networking: driver failed programming external connectivity on "
              + "endpoint reeve-wl-virtual-devices-simulator-db2990ca: Bind for 0.0.0.0:5179 failed: port is "
              + "already allocated")]
    [InlineData("Bind for :::5179 failed: port is already allocated")]
    [InlineData("listen tcp 0.0.0.0:5179: bind: address already in use")]
    public void A_port_collision_is_a_rejection_even_though_docker_reports_it_as_a_500(string message)
    {
        var failure = new DockerApiException(HttpStatusCode.InternalServerError, message);

        Assert.Equal(DockerFailureKind.Rejected, DockerContainerManager.ClassifyForTests(failure));
    }

    [Fact]
    public void An_ordinary_500_is_still_a_retryable_error()
    {
        var failure = new DockerApiException(HttpStatusCode.InternalServerError, "devicemapper: Error running deviceCreate");

        Assert.Equal(DockerFailureKind.Error, DockerContainerManager.ClassifyForTests(failure));
    }

    [Fact]
    public void A_4xx_is_still_a_rejection()
    {
        var failure = new DockerApiException(HttpStatusCode.Conflict, "Conflict. The container name is already in use");

        Assert.Equal(DockerFailureKind.Rejected, DockerContainerManager.ClassifyForTests(failure));
    }
}
