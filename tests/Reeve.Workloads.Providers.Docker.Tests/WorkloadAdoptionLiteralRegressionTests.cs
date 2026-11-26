using Reeve.Workloads.Domain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Docker.Orphans;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class WorkloadAdoptionLiteralRegressionTests
{
    private static readonly Guid Installation = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Instance = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    [Fact]
    public void The_docker_namer_still_produces_the_names_live_resources_carry()
    {
        var namer = DockerResourceNamer.Instance;
        var subject = new WorkloadNamingSubject
        {
            GroupId = "acme.thread",
            WorkloadId = "gateway",
            Inst8 = "22222222",
        };

        namer.ObjectName(subject).Should().Be("reeve-wl-acme-thread-gateway-22222222");
        namer.VolumeName(subject, "data").Should().Be("reeve-acme-thread-data-22222222");
        namer.VolumeScheme.Should().Be(RetainedVolumeSchemeId.Docker);
        namer.InstanceSelectorLabels(Instance).Should().Equal(new Dictionary<string, string>
        {
            ["io.reeve.workload-instance"] = "aaaaaaaa-0000-0000-0000-000000000001",
        });
    }

    [Fact]
    public void A_retained_volume_of_a_true_docker_instance_resolves_under_the_docker_scheme()
    {
        var instance = WorkloadInstance.Create(
            Guid.NewGuid(), Guid.NewGuid(), "zwave", "zwave-js", new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero));
        instance.SetProvider(DockerProvider.Id, new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero));
        var namer = DockerResourceNamer.Instance;

        WorkloadResourceNaming.ResolveRetainedResourceName(instance, "state", namer)
            .Should().Be($"reeve-zwave-state-{instance.Inst8}");
        WorkloadResourceNaming.ResolveRetainedResourceScheme(instance, "state", namer)
            .Should().Be(RetainedVolumeSchemeId.Docker);
    }

    [Fact]
    public void The_provider_constants_are_the_literals_live_resources_carry()
    {
        DockerProviderConstants.DefaultNetworkName.Should().Be("reeve-workloads");
        DockerProviderConstants.InstanceLabelKey.Should().Be("io.reeve.workload-instance");
        DockerProviderConstants.InstallationLabelKey.Should().Be("io.reeve.installation");
        DockerProviderConstants.SpecHashLabelKey.Should().Be("io.reeve.spec-hash");
        DockerProviderConstants.RestartNonceLabelKey.Should().Be("io.reeve.restart-nonce");
        DockerProviderConstants.GroupIdLabelKey.Should().Be("io.reeve.plugin-type-id");
        DockerProviderConstants.WorkloadIdLabelKey.Should().Be("io.reeve.workload-id");
        DockerProviderConstants.ManagedByLabelKey.Should().Be("io.reeve.managed-by");
        DockerProviderConstants.ManagedByLabelValue.Should().Be("reeve-workloads");
        DockerProviderConstants.MetricsScrapeLabelKey.Should().Be("io.reeve.metrics.scrape");
        DockerProviderConstants.MetricsPortLabelKey.Should().Be("io.reeve.metrics.port");
        DockerProviderConstants.MetricsPathLabelKey.Should().Be("io.reeve.metrics.path");

        KubernetesWorkloadProvider.FieldManager.Should().Be("reeve-workloads");
        KubernetesWorkloadRenderer.RestartGenerationAnnotationKey
            .Should().Be("reeve.io/restart-generation");
        WorkloadAdoptionLabels.ManagedByLabelKey.Should().Be("reeve.io/managed-by");
    }

    [Fact]
    public void A_container_is_still_stamped_with_the_keys_the_orphan_sweep_filters_on()
    {
        var labels = DockerProviderConstants.BuildResourceLabels(SpecWithInstallation());

        labels.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["io.reeve.workload-instance"] = "22222222-2222-2222-2222-222222222222",
            ["io.reeve.spec-hash"] = "abc123",
            ["io.reeve.plugin-type-id"] = "acme.thread",
            ["io.reeve.workload-id"] = "gateway",
            ["io.reeve.managed-by"] = "reeve-workloads",
            ["io.reeve.installation"] = "install-1",
        });
    }

    [Fact]
    public async Task An_existing_container_carrying_the_live_labels_is_still_recognised_as_ours()
    {
        var service = BuildOrphanService(ContainerWithLiveLabels(), knownInstanceIds: []);

        var orphan = (await service.DetectAsync(CancellationToken.None)).Should().ContainSingle().Subject;

        orphan.InstanceId.Should().Be(Instance);
        orphan.GroupId.Should().Be("acme.thread");
        orphan.WorkloadId.Should().Be("gateway");
        orphan.IsForeign.Should().BeFalse();
    }

    [Fact]
    public async Task A_container_whose_row_survives_is_still_swept_as_a_non_orphan()
    {
        var service = BuildOrphanService(ContainerWithLiveLabels(), knownInstanceIds: [Instance]);

        (await service.DetectAsync(CancellationToken.None)).Should().BeEmpty();
    }

    private static ContainerListResponse ContainerWithLiveLabels() => new()
    {
        ID = "c0ffee",
        Names = ["/reeve-wl-acme-thread-gateway-22222222"],
        Labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["io.reeve.managed-by"] = "reeve-workloads",
            ["io.reeve.workload-instance"] = Instance.ToString("D"),
            ["io.reeve.installation"] = Installation.ToString("D"),
            ["io.reeve.plugin-type-id"] = "acme.thread",
            ["io.reeve.workload-id"] = "gateway",
        },
    };

    private static DockerWorkloadOrphanService BuildOrphanService(
        ContainerListResponse container, IReadOnlyCollection<Guid> knownInstanceIds)
    {
        var operations = Substitute.For<IContainerOperations>();
        operations
            .ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<ContainerListResponse>>([container]));

        var client = Substitute.For<IDockerClient>();
        client.Containers.Returns(operations);

        var factory = Substitute.For<IDockerClientFactory>();
        factory.CreateClient(Arg.Any<string?>()).Returns(client);

        var identity = Substitute.For<IWorkloadInstallationIdentity>();
        identity.GetInstallationIdAsync(Arg.Any<CancellationToken>()).Returns(Installation.ToString("D"));

        var rows = Substitute.For<IWorkloadInstanceIdReader>();
        rows.GetKnownInstanceIdsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlySet<Guid>>(_ => new HashSet<Guid>(knownInstanceIds));

        var socket = Substitute.For<IDockerSocketUriReader>();
        socket.GetDockerSocketUriAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        return new DockerWorkloadOrphanService(
            factory, identity, rows, socket, NullLogger<DockerWorkloadOrphanService>.Instance);
    }

    private static WorkloadDesiredSpec SpecWithInstallation() => new()
    {
        GroupInstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        GroupId = "acme.thread",
        WorkloadId = "gateway",
        InstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Inst8 = "22222222",
        Provider = DockerProvider.Id,
        ResourceName = "reeve-wl-acme-thread-gateway-22222222",
        SecretName = "reeve-wl-acme-thread-gateway-22222222-secret",
        Labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reeve.io/installation"] = "install-1",
        },
        Image = "registry.example.com/acme/thread@sha256:deadbeef",
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "abc123",
    };
}
