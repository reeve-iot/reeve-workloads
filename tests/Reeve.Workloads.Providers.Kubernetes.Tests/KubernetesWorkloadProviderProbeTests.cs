using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Providers.Kubernetes;
using SettingsConnectionMode = Reeve.Workloads.Providers.Kubernetes.KubernetesConnectionMode;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesWorkloadProviderProbeTests
{
    private const string Ns = "reeve";

    private const string SelfContainedKubeconfig = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            client-certificate-data: ZmFrZS1jZXJ0LWRhdGE=
            client-key-data: ZmFrZS1rZXktZGF0YQ==
        """;

    private const string FileReferenceKubeconfig = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            client-certificate: /home/op/.kube/client.crt
            client-key: /home/op/.kube/client.key
        """;

    private static WorkloadOrchestratorSettings InClusterSettings(string? ns = Ns) => new WorkloadOrchestratorSettings
    {
        Provider = KubernetesProvider.Id,
    }
        .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings { Namespace = ns });

    private static WorkloadOrchestratorSettings RemoteSettings(
        string? kubeconfig, string? context = null, string? ns = Ns) => new WorkloadOrchestratorSettings
        {
        Provider = KubernetesProvider.Id,
        }
            .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings
        {
            Namespace = ns,
            ConnectionMode = SettingsConnectionMode.RemoteKubeconfig,
            Kubeconfig = kubeconfig,
            KubeconfigContext = context,
        });

    private static KubernetesWorkloadProviderProbe Build(
        IKubernetesClusterInspector inspector, IKubernetesClientFactory? factory = null, string? podNamespace = null)
        => new(
            factory ?? new FakeClientFactory(),
            inspector,
            new KubernetesNamespaceResolver(
                new InClusterEnvironment(_ => true, InClusterEnvironment.ServiceAccountTokenPath, () => podNamespace), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            NullLogger<KubernetesWorkloadProviderProbe>.Instance);

    [Fact]
    public void Reports_the_kubernetes_provider()
    {
        var probe = Build(new FakeInspector(new KubernetesClusterInspection { ApiReachable = true }));

        probe.Provider.Should().Be(KubernetesProvider.Id);
    }

    [Fact]
    public async Task Reachable_but_forbidden_reads_reflect_per_field_markers_not_overall_reachability()
    {
        var inspection = new KubernetesClusterInspection
        {
            ApiReachable = true,
            NodesAvailable = false,
            StorageClassesAvailable = false,
            NamespacePsaAvailable = false,
        };

        var result = await Build(new FakeInspector(inspection)).ProbeAsync(InClusterSettings(), CancellationToken.None);

        result.ApiServerReachable.Should().BeTrue();
        result.CanListNodes.Should().BeFalse();
        result.StorageClasses.Should().BeNull();
        result.PsaEnforcementLabels.Should().BeNull();
        result.Message.Should().Contain("Connected");
        result.ConnectionFailed.Should().BeFalse();
    }

    [Fact]
    public async Task Reachable_cluster_projects_reachability_storage_classes_and_psa_labels()
    {
        var inspection = new KubernetesClusterInspection
        {
            ApiReachable = true,
            StorageClasses = new[]
            {
                new ClusterStorageClassInfo { Name = "local-path", Provisioner = "rancher.io/local-path" },
                new ClusterStorageClassInfo { Name = "fast-ssd" },
            },
            NamespacePsaLabels = new Dictionary<string, string>
            {
                ["pod-security.kubernetes.io/enforce"] = "baseline",
            },
        };
        var probe = Build(new FakeInspector(inspection));

        var result = await probe.ProbeAsync(InClusterSettings());

        result.Provider.Should().Be(KubernetesProvider.Id);
        result.ProbeAvailable.Should().BeTrue();
        result.ApiServerReachable.Should().BeTrue();
        result.CanListNodes.Should().BeTrue();
        result.StorageClasses.Should().BeEquivalentTo(new[] { "local-path", "fast-ssd" });
        result.PsaEnforcementLabels.Should().ContainKey("pod-security.kubernetes.io/enforce")
            .WhoseValue.Should().Be("baseline");
        result.Message.Should().Contain("Connected");
        result.ConnectionFailed.Should().BeFalse();
    }

    [Fact]
    public async Task Unreachable_cluster_reports_api_unreachable_but_probe_available()
    {
        var inspection = KubernetesClusterInspection.Unreachable("connection refused (10.0.0.1:6443)");
        var probe = Build(new FakeInspector(inspection));

        var result = await probe.ProbeAsync(InClusterSettings());

        result.Provider.Should().Be(KubernetesProvider.Id);
        result.ProbeAvailable.Should().BeTrue();
        result.ApiServerReachable.Should().Be(false);
        result.CanListNodes.Should().Be(false);
        result.ConnectionFailed.Should().BeTrue();
        result.Message.Should().Contain("unreachable");
        result.Message.Should().Contain("connection refused");
    }

    [Fact]
    public async Task Blank_namespace_in_cluster_probes_the_pods_own_namespace()
    {
        var inspector = new FakeInspector(new KubernetesClusterInspection { ApiReachable = true });
        var probe = Build(inspector, podNamespace: "pod-ns");

        await probe.ProbeAsync(InClusterSettings(ns: null));

        inspector.LastNamespace.Should().Be("pod-ns");
    }

    [Fact]
    public async Task Blank_namespace_with_none_to_fall_back_on_reports_the_namespace_and_not_a_connection_failure()
    {
        var inspector = new FakeInspector(new KubernetesClusterInspection { ApiReachable = true });
        var probe = Build(inspector, podNamespace: "pod-ns");

        var result = await probe.ProbeAsync(RemoteSettings(SelfContainedKubeconfig, ns: null));

        result.ApiServerReachable.Should().BeNull("nothing was contacted");
        result.ConnectionFailed.Should().BeNull();
        result.Message.Should().Contain("namespace");
        result.Warnings.Should().ContainSingle();
        inspector.LastNamespace.Should().BeNull();
    }

    [Fact]
    public async Task Blank_namespace_does_not_hide_an_in_cluster_config_error()
    {
        var probe = Build(
            new FakeInspector(new KubernetesClusterInspection { ApiReachable = true }),
            new ThrowingClientFactory("no in-cluster config"));

        var result = await probe.ProbeAsync(InClusterSettings(ns: null));

        result.ConnectionFailed.Should().BeTrue();
        result.Message.Should().Contain("no in-cluster config");
    }

    [Fact]
    public async Task Client_factory_throwing_outside_a_cluster_is_caught_and_reported_not_reachable()
    {
        var probe = Build(
            new FakeInspector(new KubernetesClusterInspection { ApiReachable = true }),
            new ThrowingClientFactory("no in-cluster config"));

        var act = async () => await probe.ProbeAsync(InClusterSettings());

        var result = await act.Should().NotThrowAsync();
        result.Subject.Provider.Should().Be(KubernetesProvider.Id);
        result.Subject.ProbeAvailable.Should().BeTrue();
        result.Subject.ApiServerReachable.Should().Be(false);
        result.Subject.ConnectionFailed.Should().BeTrue();
        result.Subject.Message.Should().Contain("no in-cluster config");
    }

    [Fact]
    public async Task Remote_valid_kubeconfig_connects_and_projects_reachability()
    {
        var factory = new FakeClientFactory();
        var inspector = new FakeInspector(new KubernetesClusterInspection { ApiReachable = true });
        var probe = Build(inspector, factory);

        var result = await probe.ProbeAsync(RemoteSettings(SelfContainedKubeconfig));

        result.ApiServerReachable.Should().BeTrue();
        result.ConnectionFailed.Should().BeFalse();
        result.Message.Should().Contain("Connected");
        factory.Calls.Should().Be(1);
        inspector.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Remote_empty_kubeconfig_short_circuits_with_named_reason_without_connecting()
    {
        var factory = new FakeClientFactory();
        var inspector = new FakeInspector(new KubernetesClusterInspection { ApiReachable = true });
        var probe = Build(inspector, factory);

        var result = await probe.ProbeAsync(RemoteSettings(kubeconfig: ""));

        result.ProbeAvailable.Should().BeTrue();
        result.ApiServerReachable.Should().BeFalse();
        result.ConnectionFailed.Should().BeTrue();
        result.Message.Should().Contain("empty");
        factory.Calls.Should().Be(0);
        inspector.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Remote_unparseable_kubeconfig_short_circuits_with_named_reason_without_connecting()
    {
        var factory = new FakeClientFactory();
        var inspector = new FakeInspector(new KubernetesClusterInspection { ApiReachable = true });
        var probe = Build(inspector, factory);

        var result = await probe.ProbeAsync(RemoteSettings("- just\n- a\n- list"));

        result.ApiServerReachable.Should().BeFalse();
        result.ConnectionFailed.Should().BeTrue();
        result.Message.Should().Contain("could not be parsed");
        factory.Calls.Should().Be(0);
        inspector.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Remote_file_reference_kubeconfig_short_circuits_as_not_self_contained_without_connecting()
    {
        var factory = new FakeClientFactory();
        var inspector = new FakeInspector(new KubernetesClusterInspection { ApiReachable = true });
        var probe = Build(inspector, factory);

        var result = await probe.ProbeAsync(RemoteSettings(FileReferenceKubeconfig));

        result.ApiServerReachable.Should().BeFalse();
        result.ConnectionFailed.Should().BeTrue();
        result.Message.Should().Contain("file");
        result.Message.Should().Contain("client-certificate");
        result.Message.Should().NotContain("/home/op");
        factory.Calls.Should().Be(0);
        inspector.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Remote_unknown_context_short_circuits_as_context_not_found_without_connecting()
    {
        var factory = new FakeClientFactory();
        var inspector = new FakeInspector(new KubernetesClusterInspection { ApiReachable = true });
        var probe = Build(inspector, factory);

        var result = await probe.ProbeAsync(RemoteSettings(SelfContainedKubeconfig, context: "does-not-exist"));

        result.ApiServerReachable.Should().BeFalse();
        result.ConnectionFailed.Should().BeTrue();
        result.Message.Should().Contain("context");
        factory.Calls.Should().Be(0);
        inspector.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Remote_client_build_throw_is_caught_and_reports_invalid_kubeconfig_without_leaking()
    {
        var probe = Build(
            new FakeInspector(new KubernetesClusterInspection { ApiReachable = true }),
            new ThrowingClientFactory("boom: kubeconfig fragment leaked here"));

        var act = async () => await probe.ProbeAsync(RemoteSettings(SelfContainedKubeconfig));

        var result = await act.Should().NotThrowAsync();
        result.Subject.ApiServerReachable.Should().BeFalse();
        result.Subject.ConnectionFailed.Should().BeTrue();
        result.Subject.Message.Should().Contain("kubeconfig is invalid");
        result.Subject.Message.Should().NotContain("boom");
    }

    [Fact]
    public async Task Tls_ca_failure_is_surfaced_as_a_named_tls_failure()
    {
        var inspection = KubernetesClusterInspection.Unreachable(
            "The remote certificate is invalid because of errors in the certificate chain.",
            KubernetesApiFailure.TlsCaFailure);
        var result = await Build(new FakeInspector(inspection)).ProbeAsync(RemoteSettings(SelfContainedKubeconfig));

        result.ApiServerReachable.Should().BeFalse();
        result.ConnectionFailed.Should().BeTrue();
        result.Message.Should().Contain("TLS/CA failure");
        result.Message.Should().Contain("certificate could not be validated");
    }

    [Fact]
    public async Task Auth_rejected_is_surfaced_as_a_named_401_failure_and_stays_reachable()
    {
        var inspection = KubernetesClusterInspection.AuthRejected("Unauthorized");
        var result = await Build(new FakeInspector(inspection)).ProbeAsync(RemoteSettings(SelfContainedKubeconfig));

        result.ApiServerReachable.Should().BeTrue();
        result.ConnectionFailed.Should().BeTrue();
        result.CanListNodes.Should().BeFalse();
        result.Message.Should().Contain("401");
        result.Message.Should().Contain("rejected");
    }

    [Fact]
    public async Task Unreachable_remote_api_server_is_surfaced_as_a_named_unreachable_failure()
    {
        var inspection = KubernetesClusterInspection.Unreachable(
            "No such host is known. (api.remote:6443)", KubernetesApiFailure.Unreachable);
        var result = await Build(new FakeInspector(inspection)).ProbeAsync(RemoteSettings(SelfContainedKubeconfig));

        result.ApiServerReachable.Should().BeFalse();
        result.ConnectionFailed.Should().BeTrue();
        result.Message.Should().Contain("unreachable");
        result.Message.Should().Contain("No such host is known");
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var probe = Build(
            new FakeInspector(new KubernetesClusterInspection { ApiReachable = true }),
            new CancelObservingClientFactory());

        var act = async () => await probe.ProbeAsync(InClusterSettings(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeClientFactory : IKubernetesClientFactory
    {
        public int Calls { get; private set; }

        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IKubernetes>(null!);
        }
    }

    private sealed class ThrowingClientFactory(string message) : IKubernetesClientFactory
    {
        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(message);
    }

    private sealed class CancelObservingClientFactory : IKubernetesClientFactory
    {
        public Task<IKubernetes> CreateClientAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IKubernetes>(null!);
        }
    }

    private sealed class FakeInspector(KubernetesClusterInspection inspection) : IKubernetesClusterInspector
    {
        public int Calls { get; private set; }

        public string? LastNamespace { get; private set; }

        public Task<KubernetesClusterInspection> InspectAsync(IKubernetes client, string @namespace, CancellationToken ct)
        {
            Calls++;
            LastNamespace = @namespace;
            return Task.FromResult(inspection);
        }
    }
}
