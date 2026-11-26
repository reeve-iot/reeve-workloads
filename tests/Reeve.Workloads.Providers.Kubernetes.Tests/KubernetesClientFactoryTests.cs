using Reeve.Workloads.Domain;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesClientFactoryTests
{
    private static readonly string RemoteKubeconfig =
        """
        apiVersion: v1
        kind: Config
        clusters:
        - name: reeve
          cluster:
            server: https://remote.example:6443
            insecure-skip-tls-verify: true
        contexts:
        - name: reeve
          context:
            cluster: reeve
            user: reeve
        current-context: reeve
        users:
        - name: reeve
          user:
            token: dummy-token-not-a-real-credential
        """;

    private static (KubernetesClientFactory Factory, StubSettingsService Settings, RecordingBuilder Builder) Build()
    {
        var settings = new StubSettingsService();
        var config = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddScoped<IWorkloadOrchestratorSettingsService>(_ => settings);
        services.AddScoped<IWorkloadOrchestratorSecretReader>(_ => settings);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var builder = new RecordingBuilder();
        var factory = new KubernetesClientFactory(
            scopeFactory, config, NullLogger<KubernetesClientFactory>.Instance, builder.Build);

        return (factory, settings, builder);
    }

    [Fact]
    public async Task CreateClientAsync_returns_the_SAME_cached_instance_for_the_same_fingerprint()
    {
        var (factory, _, builder) = Build();

        var first = await factory.CreateClientAsync();
        var second = await factory.CreateClientAsync();

        builder.BuildCount.Should().Be(1, "an unchanged connection must be built once and reused");
        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task CreateClientAsync_rebuilds_on_fingerprint_change_and_disposes_the_superseded_client_one_generation_later()
    {
        var (factory, settings, builder) = Build();

        var first = await factory.CreateClientAsync();

        settings.Kubernetes = RemoteSettings("ctx-1");
        var second = await factory.CreateClientAsync();

        builder.BuildCount.Should().Be(2, "a changed connection fingerprint must build a new client");
        second.Should().NotBeSameAs(first);
        ((IDisposable)first).DidNotReceive().Dispose();
        ((IDisposable)second).DidNotReceive().Dispose();

        settings.Kubernetes = RemoteSettings("ctx-2");
        var third = await factory.CreateClientAsync();

        builder.BuildCount.Should().Be(3);
        third.Should().NotBeSameAs(second);
        ((IDisposable)first).Received(1).Dispose();
        ((IDisposable)second).DidNotReceive().Dispose();
        ((IDisposable)third).DidNotReceive().Dispose();
    }

    [Fact]
    public async Task Dispose_disposes_both_the_current_and_the_one_generation_old_retained_client()
    {
        var (factory, settings, _) = Build();

        var first = await factory.CreateClientAsync();
        settings.Kubernetes = RemoteSettings("ctx-1");
        var second = await factory.CreateClientAsync();

        ((IDisposable)first).DidNotReceive().Dispose();

        factory.Dispose();

        ((IDisposable)first).Received(1).Dispose();
        ((IDisposable)second).Received(1).Dispose();
    }

    [Fact]
    public async Task Many_rebuilds_dispose_every_superseded_client_and_never_leak()
    {
        var (factory, settings, builder) = Build();

        await factory.CreateClientAsync();
        for (var i = 0; i < 6; i++)
        {
            settings.Kubernetes = RemoteSettings($"ctx-{i}");
            await factory.CreateClientAsync();
        }

        builder.BuildCount.Should().Be(7);

        for (var i = 0; i < 5; i++)
        {
            ((IDisposable)builder.Built[i]).Received(1).Dispose();
        }

        ((IDisposable)builder.Built[5]).DidNotReceive().Dispose();
        ((IDisposable)builder.Built[6]).DidNotReceive().Dispose();

        factory.Dispose();

        builder.Built.Should().HaveCount(7);
        foreach (var client in builder.Built)
        {
            ((IDisposable)client).Received(1).Dispose();
        }
    }

    [Fact]
    public async Task CreateClientAsync_does_not_memoize_a_throwing_build_and_the_next_call_retries()
    {
        var (factory, _, builder) = Build();
        builder.ThrowOnNextBuild(new InvalidOperationException("no in-cluster config"));

        var firstCall = async () => await factory.CreateClientAsync();
        await firstCall.Should().ThrowAsync<InvalidOperationException>(
            "the first build faults and must not be cached (no poisoned singleton)");

        var recovered = await factory.CreateClientAsync();
        recovered.Should().NotBeNull();
        builder.BuildCount.Should().Be(2);

        (await factory.CreateClientAsync()).Should().BeSameAs(recovered);
        builder.BuildCount.Should().Be(2);
    }

    [Fact]
    public async Task Dispose_disposes_the_cached_client_and_further_acquires_throw()
    {
        var (factory, _, _) = Build();

        var client = await factory.CreateClientAsync();
        factory.Dispose();

        ((IDisposable)client).Received(1).Dispose();

        var afterDispose = async () => await factory.CreateClientAsync();
        await afterDispose.Should().ThrowAsync<ObjectDisposedException>();
    }

    private sealed class RecordingBuilder
    {
        private readonly Queue<Exception> pendingThrows = new();

        public int BuildCount { get; private set; }

        public List<IKubernetes> Built { get; } = new();

        public void ThrowOnNextBuild(Exception exception) => pendingThrows.Enqueue(exception);

        public IKubernetes Build(KubernetesConnectionSource source)
        {
            BuildCount++;
            if (pendingThrows.Count > 0)
            {
                throw pendingThrows.Dequeue();
            }

            var client = Substitute.For<IKubernetes>();
            Built.Add(client);
            return client;
        }
    }

    private static KubernetesProviderSettings RemoteSettings(string context) => new()
    {
        ConnectionMode = KubernetesConnectionMode.RemoteKubeconfig,
        Kubeconfig = RemoteKubeconfig,
        KubeconfigContext = context,
    };

    private sealed class StubSettingsService : IWorkloadOrchestratorSettingsService, IWorkloadOrchestratorSecretReader
    {
        public KubernetesProviderSettings Kubernetes { get; set; } = new();

        public Task<WorkloadOrchestratorSettings> GetDecryptedSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadOrchestratorSettings().WithProvider(KubernetesProvider.Id, Kubernetes));

        public Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = new WorkloadOrchestratorSettings().WithProvider(KubernetesProvider.Id, Kubernetes),
                EffectiveProvider = KubernetesProvider.Id,
                AutoDetected = false,
                Providers = Array.Empty<WorkloadProviderInfo>(),
            });


        public Task<WorkloadProviderProbeResult> TestAsync(WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
