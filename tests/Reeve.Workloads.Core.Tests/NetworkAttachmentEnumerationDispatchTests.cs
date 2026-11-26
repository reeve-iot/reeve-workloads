using Reeve.Workloads.Domain;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Core.Network;
using Reeve.Workloads.Providers.Docker;
using Xunit;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests;

public sealed class NetworkAttachmentEnumerationDispatchTests
{
    [Fact]
    public async Task Asks_only_the_effective_providers_source()
    {
        var docker = new RecordingSource(DockerProvider.Id);
        var kubernetes = new RecordingSource(KubernetesProvider.Id);
        var service = Build(DockerProvider.Id, docker, kubernetes);

        var result = await service.ListAsync(CancellationToken.None);

        docker.Calls.Should().Be(1);
        kubernetes.Calls.Should().Be(0);
        result.Provider.Should().Be(DockerProvider.Id);
    }

    [Fact]
    public async Task A_provider_with_no_source_is_not_supported_and_does_not_throw()
    {
        var service = Build(ManualProvider.Id, new RecordingSource(DockerProvider.Id));

        var result = await service.ListAsync(CancellationToken.None);

        result.Provider.Should().Be(ManualProvider.Id);
        result.Items.Should().BeEmpty();
        result.UnavailableReason.Should().Be(WorkloadNetworkAttachmentUnavailableReasons.NotSupported);
    }

    [Fact]
    public async Task Hands_the_source_the_resolved_settings_not_a_fresh_read()
    {
        var docker = new RecordingSource(DockerProvider.Id);
        var service = Build(DockerProvider.Id, docker);

        await service.ListAsync(CancellationToken.None);

        docker.LastSocketUri.Should().Be("unix:///var/run/docker.sock");
    }

    private static NetworkAttachmentEnumerationService Build(
        WorkloadProviderId effective,
        params INetworkAttachmentSource[] sources)
    {
        var settingsService = Substitute.For<IWorkloadOrchestratorSettingsService>();
        settingsService.GetAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = new WorkloadOrchestratorSettings
                {
                    Provider = effective,
                }
                    .WithProvider(DockerProvider.Id, new DockerProviderSettings { SocketUri = "unix:///var/run/docker.sock" }),
                EffectiveProvider = effective,
                AutoDetected = false,
                Providers = Array.Empty<WorkloadProviderInfo>(),
            }));

        return new NetworkAttachmentEnumerationService(settingsService, sources);
    }

    private sealed class RecordingSource(WorkloadProviderId provider) : INetworkAttachmentSource
    {
        public int Calls { get; private set; }

        public string? LastSocketUri { get; private set; }

        public WorkloadProviderId Provider => provider;

        public string DescribeMissing(string name) => "not evaluated by this test";

        public Task<WorkloadNetworkAttachmentListResponse> ListAsync(
            WorkloadOrchestratorSettings settings,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastSocketUri = DockerProviderSettings.From(settings).SocketUri;
            return Task.FromResult(new WorkloadNetworkAttachmentListResponse
            {
                Provider = provider,
                Items = new List<WorkloadNetworkAttachmentDto>(),
            });
        }
    }
}
