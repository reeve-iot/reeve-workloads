using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using NSubstitute;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesClusterInspectorTests
{
    private const string Namespace = "reeve-workloads";

    private static HttpOperationException Forbidden() =>
        new("forbidden")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden), "{}"),
        };

    private static HttpRequestException Transport() => new("Connection refused (10.0.0.1:6443)");

    private static HttpOperationException Unauthorized() =>
        new("unauthorized")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Unauthorized), "{}"),
        };

    private static HttpRequestException TlsFailure() =>
        new("The SSL connection could not be established.",
            new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain."));

    private sealed class FakeInspector(
        Func<V1NodeList>? nodes = null,
        Func<V1Namespace>? ns = null,
        Func<V1StorageClassList>? storageClasses = null,
        Func<V1ResourceQuotaList>? quotas = null,
        Func<VersionInfo>? version = null) : KubernetesClusterInspector
    {
        internal override Task<V1NodeList> ReadNodesRawAsync(IKubernetes client, CancellationToken ct)
            => Task.FromResult((nodes ?? (() => new V1NodeList { Items = new List<V1Node>() }))());

        internal override Task<V1Namespace> ReadNamespaceRawAsync(IKubernetes client, string @namespace, CancellationToken ct)
            => Task.FromResult((ns ?? (() => new V1Namespace { Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string>() } }))());

        internal override Task<V1StorageClassList> ReadStorageClassesRawAsync(IKubernetes client, CancellationToken ct)
            => Task.FromResult((storageClasses ?? (() => new V1StorageClassList { Items = new List<V1StorageClass>() }))());

        internal override Task<V1ResourceQuotaList> ReadResourceQuotasRawAsync(IKubernetes client, string @namespace, CancellationToken ct)
            => Task.FromResult((quotas ?? (() => new V1ResourceQuotaList { Items = new List<V1ResourceQuota>() }))());

        internal override Task<VersionInfo> ReadServerVersionRawAsync(IKubernetes client, CancellationToken ct)
            => Task.FromResult((version ?? (() => new VersionInfo { Major = "1", Minor = "35", GitVersion = "v1.35.0" }))());
    }

    private static IKubernetes AnyClient() => Substitute.For<IKubernetes>();

    private static V1NodeList OneNode() => new()
    {
        Items = new List<V1Node>
        {
            new()
            {
                Metadata = new V1ObjectMeta { Name = "n1", Labels = new Dictionary<string, string> { ["kubernetes.io/arch"] = "amd64" } },
                Status = new V1NodeStatus { NodeInfo = new V1NodeSystemInfo { Architecture = "amd64" } },
                Spec = new V1NodeSpec(),
            },
        },
    };

    [Fact]
    public async Task All_reads_succeed_yields_a_fully_available_snapshot()
    {
        var inspector = new FakeInspector(
            nodes: OneNode,
            storageClasses: () => new V1StorageClassList { Items = new List<V1StorageClass> { new() { Metadata = new V1ObjectMeta { Name = "sc" }, VolumeBindingMode = "Immediate" } } });

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiReachable.Should().BeTrue();
        r.NodesAvailable.Should().BeTrue();
        r.NamespacePsaAvailable.Should().BeTrue();
        r.StorageClassesAvailable.Should().BeTrue();
        r.ResourceQuotaAvailable.Should().BeTrue();
        r.Nodes.Should().ContainSingle();
        r.StorageClasses.Should().ContainSingle();
    }

    [Fact]
    public async Task A_403_on_resourcequotas_keeps_api_reachable_and_marks_only_that_field_unavailable()
    {
        var inspector = new FakeInspector(nodes: OneNode, quotas: () => throw Forbidden());

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiReachable.Should().BeTrue();
        r.ResourceQuotaAvailable.Should().BeFalse();
        r.NamespaceHasResourceQuotaRequiringRequests.Should().BeFalse();
        r.NodesAvailable.Should().BeTrue();
        r.Nodes.Should().ContainSingle();
        r.NamespacePsaAvailable.Should().BeTrue();
        r.StorageClassesAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_403_on_nodes_and_namespace_and_storageclasses_keeps_api_reachable()
    {
        var inspector = new FakeInspector(
            nodes: () => throw Forbidden(),
            ns: () => throw Forbidden(),
            storageClasses: () => throw Forbidden(),
            quotas: () => new V1ResourceQuotaList { Items = new List<V1ResourceQuota>() });

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiReachable.Should().BeTrue();
        r.NodesAvailable.Should().BeFalse();
        r.NamespacePsaAvailable.Should().BeFalse();
        r.StorageClassesAvailable.Should().BeFalse();
        r.ResourceQuotaAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_transport_failure_on_any_read_collapses_the_whole_snapshot_to_unreachable()
    {
        var inspector = new FakeInspector(nodes: () => throw Transport());

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiReachable.Should().BeFalse();
        r.ApiError.Should().Contain("Connection refused");
        r.ApiFailure.Should().Be(KubernetesApiFailure.Unreachable);
        r.NodesAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_TLS_handshake_failure_is_classified_as_a_TLS_CA_failure()
    {
        var inspector = new FakeInspector(nodes: () => throw TlsFailure());

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiReachable.Should().BeFalse();
        r.ApiFailure.Should().Be(KubernetesApiFailure.TlsCaFailure);
    }

    [Fact]
    public async Task A_401_collapses_the_snapshot_to_auth_rejected_but_stays_reachable()
    {
        var inspector = new FakeInspector(nodes: () => throw Unauthorized());

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiReachable.Should().BeTrue();
        r.ApiFailure.Should().Be(KubernetesApiFailure.AuthRejected);
        r.NodesAvailable.Should().BeFalse();
        r.StorageClassesAvailable.Should().BeFalse();
        r.NamespacePsaAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_403_with_response_is_distinguished_from_a_null_response_transport_error()
    {
        var nullResponse = new HttpOperationException("no response") { Response = null };
        var inspector = new FakeInspector(storageClasses: () => throw nullResponse);

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiReachable.Should().BeFalse();
    }

    private sealed class CountingInspector(
        Func<V1NodeList>? nodes = null,
        Func<V1Namespace>? ns = null,
        Func<V1StorageClassList>? storageClasses = null,
        Func<V1ResourceQuotaList>? quotas = null,
        Func<VersionInfo>? version = null) : KubernetesClusterInspector
    {
        public int NodeReads { get; private set; }
        public int NamespaceReads { get; private set; }
        public int StorageClassReads { get; private set; }
        public int QuotaReads { get; private set; }
        public int VersionReads { get; private set; }

        internal override Task<V1NodeList> ReadNodesRawAsync(IKubernetes client, CancellationToken ct)
        {
            NodeReads++;
            return Task.FromResult((nodes ?? (() => new V1NodeList { Items = new List<V1Node>() }))());
        }

        internal override Task<V1Namespace> ReadNamespaceRawAsync(IKubernetes client, string @namespace, CancellationToken ct)
        {
            NamespaceReads++;
            return Task.FromResult((ns ?? (() => new V1Namespace { Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string>() } }))());
        }

        internal override Task<V1StorageClassList> ReadStorageClassesRawAsync(IKubernetes client, CancellationToken ct)
        {
            StorageClassReads++;
            return Task.FromResult((storageClasses ?? (() => new V1StorageClassList { Items = new List<V1StorageClass>() }))());
        }

        internal override Task<V1ResourceQuotaList> ReadResourceQuotasRawAsync(IKubernetes client, string @namespace, CancellationToken ct)
        {
            QuotaReads++;
            return Task.FromResult((quotas ?? (() => new V1ResourceQuotaList { Items = new List<V1ResourceQuota>() }))());
        }

        internal override Task<VersionInfo> ReadServerVersionRawAsync(IKubernetes client, CancellationToken ct)
        {
            VersionReads++;
            return Task.FromResult((version ?? (() => new VersionInfo { Major = "1", Minor = "35", GitVersion = "v1.35.0" }))());
        }
    }

    [Theory]
    [InlineData("1", "35", "v1.35.5+k3s1", 1, 35)]
    [InlineData("1", "33+", "v1.33.2-eks-1", 1, 33)]
    [InlineData("", "", "v1.34.1+k3s1", 1, 34)]
    [InlineData(null, null, "v1.36.0", 1, 36)]
    public void The_server_version_parses_the_shapes_clusters_report(string? major, string? minor, string git, int expMajor, int expMinor)
        => KubernetesClusterInspector.ParseServerVersion(new VersionInfo { Major = major, Minor = minor, GitVersion = git })
            .Should().Be(new System.Version(expMajor, expMinor));

    [Fact]
    public void An_unparseable_or_missing_version_is_null()
    {
        KubernetesClusterInspector.ParseServerVersion(null).Should().BeNull();
        KubernetesClusterInspector.ParseServerVersion(new VersionInfo { Major = "", Minor = "", GitVersion = "garbage" }).Should().BeNull();
    }

    [Fact]
    public async Task A_forbidden_version_read_leaves_the_version_unknown_and_the_cluster_reachable()
    {
        var r = await new FakeInspector(version: () => throw Forbidden()).InspectAsync(AnyClient(), Namespace, CancellationToken.None);
        r.ApiReachable.Should().BeTrue();
        r.ServerVersion.Should().BeNull();
    }

    [Fact]
    public async Task An_unreadable_version_body_leaves_the_cluster_reachable_and_version_unknown()
    {
        var r = await new FakeInspector(version: () => throw new InvalidOperationException("Could not parse /version response."))
            .InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiReachable.Should().BeTrue();
        r.ServerVersion.Should().BeNull();
    }

    [Fact]
    public async Task The_default_handler_decides_node_support_and_a_named_handler_does_not()
    {
        static V1Node NodeWith(string name, params V1NodeRuntimeHandler[] handlers) => new()
        {
            Metadata = new V1ObjectMeta { Name = name, Labels = new Dictionary<string, string>() },
            Status = new V1NodeStatus { RuntimeHandlers = handlers.Length == 0 ? null : handlers },
        };
        static V1NodeRuntimeHandler Handler(string name, bool? userNs) => new()
        {
            Name = name,
            Features = userNs is null ? null : new V1NodeRuntimeHandlerFeatures { UserNamespaces = userNs },
        };
        var nodes = new V1NodeList { Items = new List<V1Node>
        {
            NodeWith("none"), NodeWith("named-only", Handler("crun", false)),
            NodeWith("default-off", Handler("", false), Handler("crun", true)), NodeWith("default-on", Handler("", true)),
            NodeWith("default-no-features", Handler("", null)),
        } };

        var r = await new FakeInspector(nodes: () => nodes).InspectAsync(AnyClient(), Namespace, CancellationToken.None);
        r.Nodes.Select(n => (n.Name, n.UserNamespacesSupported)).Should().Equal(
            ("none", (bool?)null), ("named-only", null), ("default-off", false), ("default-on", true), ("default-no-features", null));
    }

    [Fact]
    public async Task A_transport_failure_on_the_FIRST_read_short_circuits_without_attempting_the_later_reads()
    {
        var inspector = new CountingInspector(nodes: () => throw Transport());

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiReachable.Should().BeFalse();
        r.ApiError.Should().Contain("Connection refused");
        inspector.NodeReads.Should().Be(1);
        inspector.NamespaceReads.Should().Be(0);
        inspector.StorageClassReads.Should().Be(0);
        inspector.QuotaReads.Should().Be(0);
    }

    [Fact]
    public async Task A_transport_failure_on_the_SECOND_read_short_circuits_before_the_remaining_reads()
    {
        var inspector = new CountingInspector(nodes: OneNode, ns: () => throw Transport());

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiReachable.Should().BeFalse();
        inspector.NodeReads.Should().Be(1);
        inspector.NamespaceReads.Should().Be(1);
        inspector.StorageClassReads.Should().Be(0);
        inspector.QuotaReads.Should().Be(0);
    }

    [Fact]
    public async Task A_403_on_the_first_read_does_NOT_short_circuit_the_later_reads()
    {
        var inspector = new CountingInspector(nodes: () => throw Forbidden(), quotas: () => new V1ResourceQuotaList { Items = new List<V1ResourceQuota>() });

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiReachable.Should().BeTrue();
        r.NodesAvailable.Should().BeFalse();
        inspector.NodeReads.Should().Be(1);
        inspector.NamespaceReads.Should().Be(1);
        inspector.StorageClassReads.Should().Be(1);
        inspector.QuotaReads.Should().Be(1);
    }

    [Fact]
    public async Task A_401_on_the_first_read_short_circuits_the_later_reads()
    {
        var inspector = new CountingInspector(nodes: () => throw Unauthorized());

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ApiFailure.Should().Be(KubernetesApiFailure.AuthRejected);
        inspector.NodeReads.Should().Be(1);
        inspector.NamespaceReads.Should().Be(0);
        inspector.StorageClassReads.Should().Be(0);
        inspector.QuotaReads.Should().Be(0);
    }

    [Fact]
    public async Task Cordoned_node_is_mapped_as_not_schedulable()
    {
        var cordoned = new V1NodeList
        {
            Items = new List<V1Node>
            {
                new()
                {
                    Metadata = new V1ObjectMeta { Name = "cordoned", Labels = new Dictionary<string, string> { ["kubernetes.io/arch"] = "amd64" } },
                    Spec = new V1NodeSpec { Unschedulable = true },
                },
            },
        };
        var inspector = new FakeInspector(nodes: () => cordoned);

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.Nodes.Should().ContainSingle();
        r.Nodes[0].Schedulable.Should().BeFalse();
    }

    [Fact]
    public async Task NoSchedule_tainted_node_is_mapped_as_not_schedulable()
    {
        var tainted = new V1NodeList
        {
            Items = new List<V1Node>
            {
                new()
                {
                    Metadata = new V1ObjectMeta { Name = "tainted", Labels = new Dictionary<string, string> { ["kubernetes.io/arch"] = "amd64" } },
                    Spec = new V1NodeSpec
                    {
                        Taints = new List<V1Taint> { new() { Key = "dedicated", Effect = "NoSchedule" } },
                    },
                },
            },
        };
        var inspector = new FakeInspector(nodes: () => tainted);

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.Nodes[0].Schedulable.Should().BeFalse();
    }

    [Fact]
    public async Task Plain_node_is_schedulable()
    {
        var inspector = new FakeInspector(nodes: OneNode);

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.Nodes[0].Schedulable.Should().BeTrue();
    }

    [Fact]
    public async Task Quota_with_bare_cpu_hard_key_requires_requests()
    {
        var quotas = new V1ResourceQuotaList
        {
            Items = new List<V1ResourceQuota>
            {
                new()
                {
                    Spec = new V1ResourceQuotaSpec
                    {
                        Hard = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("1"), ["memory"] = new("1Gi") },
                    },
                },
            },
        };
        var inspector = new FakeInspector(quotas: () => quotas);

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.ResourceQuotaAvailable.Should().BeTrue();
        r.NamespaceHasResourceQuotaRequiringRequests.Should().BeTrue();
    }

    [Fact]
    public async Task Quota_with_limits_cpu_hard_key_requires_requests()
    {
        var quotas = new V1ResourceQuotaList
        {
            Items = new List<V1ResourceQuota>
            {
                new()
                {
                    Spec = new V1ResourceQuotaSpec
                    {
                        Hard = new Dictionary<string, ResourceQuantity> { ["limits.cpu"] = new("2") },
                    },
                },
            },
        };
        var inspector = new FakeInspector(quotas: () => quotas);

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.NamespaceHasResourceQuotaRequiringRequests.Should().BeTrue();
    }

    [Fact]
    public async Task Quota_hard_without_cpu_or_memory_does_not_require_requests()
    {
        var quotas = new V1ResourceQuotaList
        {
            Items = new List<V1ResourceQuota>
            {
                new()
                {
                    Spec = new V1ResourceQuotaSpec
                    {
                        Hard = new Dictionary<string, ResourceQuantity> { ["pods"] = new("10") },
                    },
                },
            },
        };
        var inspector = new FakeInspector(quotas: () => quotas);

        var r = await inspector.InspectAsync(AnyClient(), Namespace, CancellationToken.None);

        r.NamespaceHasResourceQuotaRequiringRequests.Should().BeFalse();
    }
}
