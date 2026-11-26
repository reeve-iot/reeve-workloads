using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class SchemeDispatchingRetainedVolumeDeleterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private readonly SchemeDeleter docker = new(RetainedVolumeSchemeId.Docker);
    private readonly SchemeDeleter kubernetes = new(RetainedVolumeSchemeId.Kubernetes);

    private SchemeDispatchingRetainedVolumeDeleter Dispatcher(params IRetainedVolumeSchemeDeleter[] deleters)
        => new(deleters, NullLogger<SchemeDispatchingRetainedVolumeDeleter>.Instance);

    private static RetainedVolume Row(RetainedVolumeSchemeId scheme, string name) => RetainedVolume.Record(
        Guid.NewGuid(), "zwave", "zwave-js", "state", name, "1Gi", VolumeCriticality.Critical, null,
        WorkloadProviderId.Parse("any"), scheme, Guid.NewGuid(), Guid.NewGuid(), Now);

    [Fact]
    public async Task A_docker_scheme_volume_reaches_the_docker_deleter_only()
    {
        var freed = await Dispatcher(kubernetes, docker)
            .TryDeletePhysicalVolumeAsync(Row(RetainedVolumeSchemeId.Docker, "reeve-zwave-state-abc"));

        freed.Should().BeTrue();
        docker.Deleted.Should().ContainSingle().Which.Should().Be("reeve-zwave-state-abc");
        kubernetes.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_kubernetes_scheme_volume_reaches_the_kubernetes_deleter_only()
    {
        var freed = await Dispatcher(kubernetes, docker)
            .TryDeletePhysicalVolumeAsync(Row(RetainedVolumeSchemeId.Kubernetes, "reeve-wl-zwave-state-abc"));

        freed.Should().BeTrue();
        kubernetes.Deleted.Should().ContainSingle().Which.Should().Be("reeve-wl-zwave-state-abc");
        docker.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_volume_whose_scheme_no_registered_provider_owns_is_refused()
    {
        var freed = await Dispatcher(kubernetes)
            .TryDeletePhysicalVolumeAsync(Row(RetainedVolumeSchemeId.Docker, "reeve-zwave-state-abc"));

        freed.Should().BeFalse();
        kubernetes.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_scheme_deleter_registered_every_volume_is_refused()
    {
        (await Dispatcher().TryDeletePhysicalVolumeAsync(Row(RetainedVolumeSchemeId.Kubernetes, "pvc"))).Should().BeFalse();
    }

    [Fact]
    public async Task The_answer_of_the_owning_deleter_is_passed_through()
    {
        docker.Result = false;

        (await Dispatcher(docker).TryDeletePhysicalVolumeAsync(Row(RetainedVolumeSchemeId.Docker, "v"))).Should().BeFalse();
    }

    [Fact]
    public void Two_deleters_claiming_one_scheme_are_refused_at_construction()
    {
        var act = () => Dispatcher(docker, new SchemeDeleter(RetainedVolumeSchemeId.Docker));

        act.Should().Throw<InvalidOperationException>().WithMessage("*'docker'*SchemeDeleter*SchemeDeleter*");
    }

    private sealed class SchemeDeleter(RetainedVolumeSchemeId scheme) : IRetainedVolumeSchemeDeleter
    {
        public List<string> Deleted { get; } = [];

        public bool Result { get; set; } = true;

        public RetainedVolumeSchemeId NamingScheme => scheme;

        public Task<bool> TryDeletePhysicalVolumeAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
        {
            Deleted.Add(retainedVolume.ResourceName);
            return Task.FromResult(Result);
        }
    }
}
