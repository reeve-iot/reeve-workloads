using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Reeve.Workloads.Abstractions.Contracts;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests.Network;

public sealed class KubernetesNetworkAttachmentSourceListTests
{
    private static WorkloadOrchestratorSettings Settings(string? @namespace = "reeve-workloads") =>
        new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id }
            .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings { Namespace = @namespace });

    private static InClusterEnvironment Environment(string? podNamespace)
        => new(_ => true, InClusterEnvironment.ServiceAccountTokenPath, () => podNamespace);

    private static (KubernetesNetworkAttachmentSource Source, IKubernetesObjectClient Client, IKubernetesClientFactory Factory) Build(
        string? podNamespace = null)
    {
        var factory = Substitute.For<IKubernetesClientFactory>();
        factory.CreateClientAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Substitute.For<IKubernetes>()));
        var client = Substitute.For<IKubernetesObjectClient>();
        return (new KubernetesNetworkAttachmentSource(factory, client, new KubernetesNamespaceResolver(Environment(podNamespace), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())), client, factory);
    }

    private static void Answers(IKubernetesObjectClient client, KubernetesNetworkAttachmentListResult result) =>
        client
            .ListNetworkAttachmentDefinitionsAsync(
                Arg.Any<IKubernetes>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(result);

    [Theory]
    [InlineData(WorkloadNetworkAttachmentUnavailableReasons.MultusNotInstalled)]
    [InlineData(WorkloadNetworkAttachmentUnavailableReasons.Forbidden)]
    [InlineData(WorkloadNetworkAttachmentUnavailableReasons.ProviderUnavailable)]
    [InlineData(WorkloadNetworkAttachmentUnavailableReasons.NoneInNamespace)]
    public async Task A_degraded_listing_passes_its_exact_reason_through(string reason)
    {
        var (source, client, _) = Build();
        Answers(client, KubernetesNetworkAttachmentListResult.Unavailable(reason));

        var response = await source.ListAsync(Settings(), CancellationToken.None);

        response.Provider.Should().Be(KubernetesProvider.Id);
        response.Items.Should().BeEmpty();
        response.UnavailableReason.Should().Be(reason);
    }

    [Fact]
    public async Task A_populated_listing_carries_the_name_of_every_item_and_no_unavailable_reason()
    {
        var (source, client, _) = Build();
        Answers(client, KubernetesNetworkAttachmentListResult.Populated(
        [
            new KubernetesNetworkAttachment { Name = "iot-macvlan" },
            new KubernetesNetworkAttachment { Name = "opaque-nad" },
        ]));

        var response = await source.ListAsync(Settings(), CancellationToken.None);

        response.UnavailableReason.Should().BeNull();
        response.Items.Should().HaveCount(2);
        response.Items[0].Name.Should().Be("iot-macvlan");
        response.Items[1].Name.Should().Be("opaque-nad");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_unconfigured_namespace_is_provider_unavailable_and_asks_nobody(string? @namespace)
    {
        var (source, client, factory) = Build();

        var response = await source.ListAsync(Settings(@namespace), CancellationToken.None);

        response.Items.Should().BeEmpty();
        response.UnavailableReason.Should().Be(WorkloadNetworkAttachmentUnavailableReasons.ProviderUnavailable);
        await factory.DidNotReceiveWithAnyArgs().CreateClientAsync(default);
        await client.DidNotReceiveWithAnyArgs().ListNetworkAttachmentDefinitionsAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task A_blank_namespace_in_cluster_lists_the_pods_own_namespace(string? @namespace)
    {
        var (source, client, _) = Build(podNamespace: "smarthome");
        Answers(client, KubernetesNetworkAttachmentListResult.Populated(
            [new KubernetesNetworkAttachment { Name = "iot-macvlan" }]));

        var response = await source.ListAsync(Settings(@namespace), CancellationToken.None);

        response.UnavailableReason.Should().BeNull();
        response.Items.Should().ContainSingle().Which.Name.Should().Be("iot-macvlan");
        await client.Received(1).ListNetworkAttachmentDefinitionsAsync(
            Arg.Any<IKubernetes>(), "smarthome", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_blank_namespace_on_a_remote_kubeconfig_is_still_provider_unavailable()
    {
        var (source, client, _) = Build(podNamespace: "smarthome");
        var settings = new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id }
            .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings
            {
                ConnectionMode = KubernetesConnectionMode.RemoteKubeconfig,
            });

        var response = await source.ListAsync(settings, CancellationToken.None);

        response.UnavailableReason.Should().Be(WorkloadNetworkAttachmentUnavailableReasons.ProviderUnavailable);
        await client.DidNotReceiveWithAnyArgs().ListNetworkAttachmentDefinitionsAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_throwing_seam_still_degrades_rather_than_escaping()
    {
        var (source, client, _) = Build();
        client
            .ListNetworkAttachmentDefinitionsAsync(default!, default!, default)
            .ThrowsAsyncForAnyArgs(new HttpRequestException("the API server hung up"));

        var response = await source.ListAsync(Settings(), CancellationToken.None);

        response.Items.Should().BeEmpty();
        response.UnavailableReason.Should().Be(WorkloadNetworkAttachmentUnavailableReasons.ProviderUnavailable);
    }

    [Fact]
    public async Task Caller_cancellation_still_propagates()
    {
        var (source, client, _) = Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        client
            .ListNetworkAttachmentDefinitionsAsync(default!, default!, default)
            .ThrowsAsyncForAnyArgs(new OperationCanceledException(cts.Token));

        var act = async () => await source.ListAsync(Settings(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void The_wire_reason_vocabulary_contains_every_kubernetes_seam_constant()
    {
        WorkloadNetworkAttachmentUnavailableReasons.All.Should().Contain(
        [
            KubernetesNetworkAttachmentUnavailableReasons.MultusNotInstalled,
            KubernetesNetworkAttachmentUnavailableReasons.Forbidden,
            KubernetesNetworkAttachmentUnavailableReasons.ProviderUnavailable,
            KubernetesNetworkAttachmentUnavailableReasons.NoneInNamespace,
        ]);

        WorkloadNetworkAttachmentUnavailableReasons.MultusNotInstalled.Should()
            .Be(KubernetesNetworkAttachmentUnavailableReasons.MultusNotInstalled);
        WorkloadNetworkAttachmentUnavailableReasons.Forbidden.Should()
            .Be(KubernetesNetworkAttachmentUnavailableReasons.Forbidden);
        WorkloadNetworkAttachmentUnavailableReasons.ProviderUnavailable.Should()
            .Be(KubernetesNetworkAttachmentUnavailableReasons.ProviderUnavailable);
        WorkloadNetworkAttachmentUnavailableReasons.NoneInNamespace.Should()
            .Be(KubernetesNetworkAttachmentUnavailableReasons.NoneInNamespace);

        WorkloadNetworkAttachmentUnavailableReasons.All.Should().HaveCount(5)
            .And.Contain(WorkloadNetworkAttachmentUnavailableReasons.NotSupported);
    }
}
