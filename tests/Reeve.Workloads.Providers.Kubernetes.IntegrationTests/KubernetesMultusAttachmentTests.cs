using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Network;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;
using Xunit.Abstractions;
using Reeve.Workloads.Domain;

using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Domain.Network;
namespace Reeve.Workloads.Providers.Kubernetes.IntegrationTests;

public sealed class KubernetesMultusAttachmentTests
{
    private static readonly InMemorySettingsService _stubSettings = new();

    private const string Namespace = "reeve-e2e";

    private const string NetworksAnnotationKey = "k8s.v1.cni.cncf.io/networks";

    private const string NetworkStatusAnnotationKey = "k8s.v1.cni.cncf.io/network-status";

    private const string WorkloadImageRef = "registry.k8s.io/pause";

    private const string WorkloadImageDigest =
        "sha256:ee6521f290b2168b6e0935a181d4cff9be1ac3f505666ef0e3c98fae8199917a";

    private const string HelperImage = "busybox:1.36";

    private const string TestJobLabelKey = "reeve.io/e2e-multus";

    private readonly ITestOutputHelper _output;

    public KubernetesMultusAttachmentTests(ITestOutputHelper output) => _output = output;

    private static string? KubeconfigPath => Environment.GetEnvironmentVariable("REEVE_E2E_KUBECONFIG");

    private static string? MultusGate => Environment.GetEnvironmentVariable("REEVE_E2E_MULTUS");

    private static string SuppressedNadName =>
        Environment.GetEnvironmentVariable("REEVE_E2E_MULTUS_NAD") ?? "iot-macvlan";

    private static string UnsuppressedNadName =>
        Environment.GetEnvironmentVariable("REEVE_E2E_MULTUS_UNSUPPRESSED_NAD") ?? "iot-macvlan-unsuppressed";

    private static string? AttachmentCidr => Environment.GetEnvironmentVariable("REEVE_E2E_MULTUS_VLAN_CIDR");

    private static string PrimaryInterfaceName =>
        Environment.GetEnvironmentVariable("REEVE_E2E_MULTUS_PRIMARY_IF") ?? "eth0";

    private static string AttachmentInterfaceName =>
        Environment.GetEnvironmentVariable("REEVE_E2E_MULTUS_ATTACH_IF") ?? "net1";

    private void SkipUnlessLive()
    {
        Skip.If(
            string.IsNullOrWhiteSpace(KubeconfigPath),
            "Set REEVE_E2E_KUBECONFIG=<kubeconfig> (a live cluster, for example k3d or kind, with a 'reeve-e2e' namespace) to run the "
                + "Multus attachment e2e.");
        Skip.If(
            string.IsNullOrWhiteSpace(MultusGate),
            "Set REEVE_E2E_MULTUS=1 to confirm the Multus setup is in place on that cluster: the route-override CNI "
                + "plugin, the per-node cni-dhcp daemon, a SUPPRESSED iot-macvlan NAD and an UNSUPPRESSED one. "
                + "See the Multus section of CONTRIBUTING.md.");
        Skip.If(
            string.IsNullOrWhiteSpace(AttachmentCidr),
            "Set REEVE_E2E_MULTUS_VLAN_CIDR to the CIDR the attachment's DHCP lease must come from.");
    }

    [SkippableFact]
    public async Task A_hostNetwork_release_installs_on_an_operator_network_with_the_multus_annotation()
    {
        SkipUnlessLive();

        var kubeconfig = KubeconfigPath!;
        var attachment = SuppressedNadName;
        var instanceId = Guid.NewGuid();
        var inst8 = instanceId.ToString("N")[..8];

        var manifest = LegacyHostNetworkManifest();
        manifest.Network!.HostNetwork.Should().BeTrue("the fixture declares hostNetwork and nothing else");
        manifest.Network!.Attachments.Should().BeEmpty("it declares NO attachment slot — that is the whole point of the reserved slot");
        manifest.Parameters.Should().BeEmpty("and no parameters at all, so nothing but a reserved slot can carry the choice");

        var resolved = WorkloadParameterResolver.Resolve(
            manifest,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WorkloadNetworkAttachmentGrammar.NetworkModeParameter] =
                    WorkloadNetworkAttachmentGrammar.OperatorNetworkMode,
                [WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter] = attachment,
            },
            KubernetesProvider.Descriptor);

        resolved.Should().ContainKey(WorkloadNetworkAttachmentGrammar.NetworkModeParameter)
            .WhoseValue.Should().Be(WorkloadNetworkAttachmentGrammar.OperatorNetworkMode);
        resolved.Should().ContainKey(WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter)
            .WhoseValue.Should().Be(attachment, "the reserved slot is what carries the operator's choice");

        var spec = ComputeSpec(manifest, resolved, instanceId, inst8);
        spec.Network.Mode.Should().Be(WorkloadNetworkMode.OperatorNetwork);
        spec.Network.HostNetwork.Should().BeFalse("operatorNetwork and hostNetwork are mutually exclusive");
        spec.Network.Attachments.Should().Equal(attachment);

        var reference = WorkloadRef.FromDesiredSpec(spec);
        var provider = BuildProvider(kubeconfig);
        using var raw = RawClient(kubeconfig);
        var ct = TestCt();

        try
        {
            var apply = await provider.ApplyAsync(spec, null, ct);
            _output.WriteLine($"ApplyAsync -> Succeeded={apply.Succeeded} Reason={apply.Reason} Msg={apply.Message}");
            apply.Succeeded.Should().BeTrue("the attached workload must apply against the live cluster");

            var pod = await WaitForRunningPodAsync(raw, instanceId, TimeSpan.FromMinutes(3), ct);
            _output.WriteLine($"pod={pod.Metadata.Name} node={pod.Spec.NodeName} phase={pod.Status.Phase} podIP={pod.Status.PodIP}");

            var annotations = pod.Metadata.Annotations ?? new Dictionary<string, string>();
            annotations.Should().ContainKey(NetworksAnnotationKey,
                "the renderer emits the Multus network-selection annotation for an operatorNetwork workload");
            _output.WriteLine($"{NetworksAnnotationKey} = {annotations[NetworksAnnotationKey]}");
            annotations[NetworksAnnotationKey].Should().Contain(attachment);

            (pod.Spec.HostNetwork ?? false).Should().BeFalse("the legacy hostNetwork release now runs OFF the host network");
            pod.Spec.DnsPolicy.Should().Be("ClusterFirst", "annotation + hostNetwork + dnsPolicy are managed as one unit");

            var statuses = ReadNetworkStatuses(pod);
            _output.WriteLine("network-status: " + string.Join(
                " | ", statuses.Select(s => $"{s.Name}[{s.Interface}]={string.Join(",", s.Ips)} default={s.Default}")));

            var attachStatus = statuses.SingleOrDefault(s =>
                string.Equals(s.Interface, AttachmentInterfaceName, StringComparison.Ordinal));
            attachStatus.Should().NotBeNull(
                $"Multus must report an interface '{AttachmentInterfaceName}' for the attachment — its absence means "
                + "CNI ADD failed (the classic cause is a missing per-node cni-dhcp daemon)");
            attachStatus!.Ips.Should().NotBeEmpty("the DHCP IPAM must have produced a lease");

            var lease = attachStatus.Ips.First(IsIpv4);
            _output.WriteLine($"attachment lease on {AttachmentInterfaceName}: {lease} (expected inside {AttachmentCidr!})");
            IsInCidr(lease, AttachmentCidr!).Should().BeTrue(
                $"the lease must come from the attachment's own segment {AttachmentCidr!}, proving the parent interface "
                + "really is the VLAN sub-interface");

            var defaultStatus = statuses.SingleOrDefault(s => s.Default);
            defaultStatus.Should().NotBeNull("Multus marks exactly one network as the pod's default");
            defaultStatus!.Interface.Should().Be(PrimaryInterfaceName,
                "the primary CNI — never the attachment — owns the pod's default network");
        }
        finally
        {
            await SafeTeardownAsync(provider, reference);
        }
    }

    [SkippableFact]
    public async Task Attachment_isolates_the_node_from_the_pod_while_a_peer_host_reaches_it_and_the_pod_is_ready()
    {
        SkipUnlessLive();

        var kubeconfig = KubeconfigPath!;
        var attachment = SuppressedNadName;
        var instanceId = Guid.NewGuid();
        var inst8 = instanceId.ToString("N")[..8];

        var manifest = LegacyHostNetworkManifest();
        var resolved = OperatorNetworkParameters(manifest, attachment);
        var spec = ComputeSpec(manifest, resolved, instanceId, inst8);
        var reference = WorkloadRef.FromDesiredSpec(spec);

        var provider = BuildProvider(kubeconfig);
        using var raw = RawClient(kubeconfig);
        var ct = TestCt();

        try
        {
            var apply = await provider.ApplyAsync(spec, null, ct);
            apply.Succeeded.Should().BeTrue();

            var pod = await WaitForRunningPodAsync(raw, instanceId, TimeSpan.FromMinutes(3), ct);
            var nodeName = pod.Spec.NodeName;
            nodeName.Should().NotBeNullOrWhiteSpace();

            var statuses = ReadNetworkStatuses(pod);
            var attachStatus = statuses.SingleOrDefault(s =>
                string.Equals(s.Interface, AttachmentInterfaceName, StringComparison.Ordinal));
            attachStatus.Should().NotBeNull();
            var attachmentIp = attachStatus!.Ips.First(IsIpv4);
            _output.WriteLine($"workload pod {pod.Metadata.Name} on node {nodeName}, attachment address {attachmentIp}");

            var running = pod.Status.ContainerStatuses ?? new List<V1ContainerStatus>();
            running.Should().NotBeEmpty();
            running.Should().OnlyContain(c => c.State != null && c.State.Running != null,
                "readiness here is the container actually running");

            foreach (var container in pod.Spec.Containers)
            {
                container.LivenessProbe.Should().BeNull("Reeve renders no kubelet probes");
                container.ReadinessProbe.Should().BeNull("Reeve renders no kubelet probes");
                container.StartupProbe.Should().BeNull("Reeve renders no kubelet probes");
            }

            var endpointAddresses = await WaitForServiceEndpointAsync(
                raw, instanceId, pod.Status.PodIP, TimeSpan.FromMinutes(2), ct);
            _output.WriteLine($"service endpoints: [{string.Join(",", endpointAddresses)}]");
            endpointAddresses.Should().Contain(pod.Status.PodIP,
                "the pod is Ready in the only sense that exists here: it is a live endpoint of its own Service");

            var nodeRoute = await RunHelperJobAsync(
                raw,
                $"reeve-e2e-noderoute-{inst8}",
                attachments: null,
                hostNetwork: true,
                nodeName: nodeName,
                command: $"ip route get {attachmentIp}; echo EXIT=$?",
                ct);

            _output.WriteLine("--- the NODE's route to the attachment address ---");
            _output.WriteLine(nodeRoute);

            var fromNode = await RunHelperJobAsync(
                raw,
                $"reeve-e2e-node-{inst8}",
                attachments: null,
                hostNetwork: true,
                nodeName: nodeName,
                command: $"ping -c 3 -W 2 {attachmentIp}; echo EXIT=$?",
                ct);

            _output.WriteLine("--- from the NODE (hostNetwork, pinned) ---");
            _output.WriteLine(fromNode);

            var parentInterface = ReadMacvlanParent(await ReadNadSpecConfigAsync(raw, attachment, ct));
            parentInterface.Should().NotBeNullOrWhiteSpace(
                "the suppressed NAD must name a macvlan/ipvlan `master`; without it this leg cannot tell the "
                + "parent↔child path from a router hop, and would be asserting nothing");
            if (nodeRoute.Contains($"dev {parentInterface}", StringComparison.Ordinal))
            {
                fromNode.Should().Contain("EXIT=1",
                    "the node's route egresses the macvlan parent '{0}', so this IS the parent↔child path, and "
                    + "macvlan/ipvlan structurally break it — a reply here would mean the isolation the design rests on "
                    + "does not hold at all", parentInterface);
            }
            else
            {
                nodeRoute.Should().NotContain($"dev {parentInterface}",
                    "sanity: this branch exists precisely because the route does NOT egress the parent");
                _output.WriteLine(
                    $"NOTE: the node has no on-link route over the macvlan parent '{parentInterface}', so any "
                    + "reachability observed above is an inter-VLAN ROUTER hop, not the parent↔child path. The isolation "
                    + "rule stands and is in fact stronger: whether the node can reach the attachment address "
                    + "depends on the operator's routing, which Reeve cannot observe — so no host→workload probe "
                    + "or health check may depend on it.");
            }

            var fromPeer = await RunHelperJobAsync(
                raw,
                $"reeve-e2e-peer-{inst8}",
                attachments: [attachment],
                hostNetwork: false,
                nodeName: nodeName,
                command: $"ping -c 3 -W 2 {attachmentIp}; echo EXIT=$?",
                ct);

            _output.WriteLine("--- from a PEER on the segment (a second macvlan child of the same parent) ---");
            _output.WriteLine(fromPeer);
            fromPeer.Should().Contain("EXIT=0",
                "the attachment address must be reachable from the segment — an unreachable-from-everywhere address "
                + "would satisfy the node-leg assertion for entirely the wrong reason");
        }
        finally
        {
            await SafeTeardownAsync(provider, reference);
        }
    }

    [SkippableFact]
    public async Task A_live_nad_exists_and_passes_whatever_its_configuration()
    {
        SkipUnlessLive();

        var kubeconfig = KubeconfigPath!;
        var ct = TestCt();

        var attachmentSource = BuildAttachmentSource(kubeconfig);
        var evaluator = new NetworkAttachmentEvaluator(
            new WorkloadProviderRegistry([BuildProvider(kubeconfig)]),
            [attachmentSource]);

        var listing = await attachmentSource.ListAsync(E2eSettings(), ct);
        listing.UnavailableReason.Should().BeNull();
        var listed = listing.Items.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in new[] { SuppressedNadName, UnsuppressedNadName })
        {
            var evaluation = await evaluator.EvaluateAsync(
                EvaluationRequestFor(name, exists: listed.Contains(name)), ct);

            foreach (var check in evaluation.Checks)
                _output.WriteLine($"[{name}] {check.Id} passed={check.Passed} severity={check.Severity} :: {check.Message}");

            evaluation.HasBlockingError.Should().BeFalse(
                "an attachment is judged by its name and existence only, whatever route its configuration gives");
            evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists).Passed.Should().BeTrue();
            evaluation.Checks.Select(c => c.Id).Should().BeSubsetOf(NetworkAttachmentCheckIds.All);
        }
    }

    private static IConfiguration KubeconfigConfiguration(string kubeconfigPath)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KubernetesConnectionResolver.AllowKubeconfigKey] = "true",
                [KubernetesConnectionResolver.KubeconfigPathKey] = kubeconfigPath,
            })
            .Build();

    private static IServiceScopeFactory StubScopeFactory()
        => new ServiceCollection()
            .AddSingleton<IWorkloadOrchestratorSettingsService>(_stubSettings)
            .AddSingleton<IWorkloadOrchestratorSecretReader>(_stubSettings)
            .AddSingleton<IWorkloadCredentialPort>(new NoCredentialsPort())
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    private static KubernetesWorkloadProvider BuildProvider(string kubeconfigPath)
    {
        var scopeFactory = StubScopeFactory();
        var clientFactory = new KubernetesClientFactory(
            scopeFactory, KubeconfigConfiguration(kubeconfigPath), NullLogger<KubernetesClientFactory>.Instance);

        return new KubernetesWorkloadProvider(
            clientFactory,
            new KubernetesObjectClient(NullLogger<KubernetesObjectClient>.Instance),
            new KubernetesWorkloadRenderer(),
            new UnsupportedPodPatchApplier(),
            new KubernetesPatchRevalidator(),
            new KubernetesLogReader(new KubernetesPodLogSource(NullLogger<KubernetesPodLogSource>.Instance)),
            new KubernetesClusterInspector(),
            new KubernetesWorkloadPreflightChecker(),
            scopeFactory,
            new KubernetesNamespaceResolver(new InClusterEnvironment(_ => false, ""), KubeconfigConfiguration(kubeconfigPath)),
            NullLogger<KubernetesWorkloadProvider>.Instance);
    }

    private static KubernetesNetworkAttachmentSource BuildAttachmentSource(string kubeconfigPath)
        => new(
            new KubernetesClientFactory(
                StubScopeFactory(), KubeconfigConfiguration(kubeconfigPath), NullLogger<KubernetesClientFactory>.Instance),
            new KubernetesObjectClient(NullLogger<KubernetesObjectClient>.Instance),
            new KubernetesNamespaceResolver(new InClusterEnvironment(_ => false, ""), KubeconfigConfiguration(kubeconfigPath)));

    private static k8s.Kubernetes RawClient(string kubeconfigPath)
        => new(KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfigPath));

    private static CancellationToken TestCt() => new CancellationTokenSource(TimeSpan.FromMinutes(10)).Token;

    private static WorkloadManifest LegacyHostNetworkManifest() => new()
    {
        Id = "probe",
        DisplayName = "Legacy hostNetwork adapter",
        Image = new WorkloadImage { Ref = WorkloadImageRef, Digest = WorkloadImageDigest },
        Kind = WorkloadKind.Service,
        Network = new WorkloadNetwork
        {
            HostNetwork = true,
            Ports = [new WorkloadPort { Name = "api", Port = 8080, Protocol = "tcp" }],
        },
        Volumes = Array.Empty<WorkloadVolume>(),
        Parameters = Array.Empty<WorkloadParameter>(),
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
    };

    private static IReadOnlyDictionary<string, string?> OperatorNetworkParameters(
        WorkloadManifest manifest, string attachment)
        => WorkloadParameterResolver.Resolve(
            manifest,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WorkloadNetworkAttachmentGrammar.NetworkModeParameter] =
                    WorkloadNetworkAttachmentGrammar.OperatorNetworkMode,
                [WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter] = attachment,
            },
            KubernetesProvider.Descriptor);

    private static WorkloadDesiredSpec ComputeSpec(
        WorkloadManifest manifest,
        IReadOnlyDictionary<string, string?> resolved,
        Guid instanceId,
        string inst8)
        => WorkloadDesiredSpecFactory.Compute(
            manifest,
            E2eSettings(),
            resolved,
            new WorkloadBootstrapContext { InstanceId = instanceId, SecretName = "reeve-wl-secret" },
            KubernetesProvider.Descriptor,
            DefaultWorkloadResourceNamer.Instance,
            pluginInstanceId: Guid.NewGuid(),
            pluginTypeId: "com.reeve.e2e",
            instanceId: instanceId,
            installationId: $"e2e-{inst8}");

    private static WorkloadOrchestratorSettings E2eSettings()
        => new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id, AllowUnpinnedImages = true }.WithProvider(
            KubernetesProvider.Id,
            new KubernetesProviderSettings { Namespace = Namespace, AllowUnsupportedOverrides = true });

    private static string LabelSelectorFor(Guid instanceId)
        => $"{WorkloadResourceLabels.InstanceLabelKey}={instanceId:D}";

    private async Task<V1Pod> WaitForRunningPodAsync(
        k8s.Kubernetes raw, Guid instanceId, TimeSpan timeout, CancellationToken ct)
    {
        var selector = LabelSelectorFor(instanceId);
        var deadline = DateTimeOffset.UtcNow + timeout;
        V1Pod? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var pods = await raw.CoreV1.ListNamespacedPodAsync(
                Namespace, labelSelector: selector, cancellationToken: ct);

            last = pods.Items.FirstOrDefault();
            if (last is not null
                && string.Equals(last.Status?.Phase, "Running", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(last.Status?.PodIP)
                && (last.Metadata.Annotations?.ContainsKey(NetworkStatusAnnotationKey) ?? false))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new InvalidOperationException(
            "No Running pod with a Multus network-status annotation appeared for the attached workload within "
            + $"{timeout}. Last observed phase: {last?.Status?.Phase ?? "<no pod>"}. A pod stuck in "
            + "ContainerCreating is the missing-DHCP-daemon failure — check the per-node cni-dhcp daemon and the CNI binaries.");
    }

    private static async Task<IReadOnlyList<string>> WaitForServiceEndpointAsync(
        k8s.Kubernetes raw, Guid instanceId, string? podIp, TimeSpan timeout, CancellationToken ct)
    {
        var selector = LabelSelectorFor(instanceId);
        var deadline = DateTimeOffset.UtcNow + timeout;
        var seen = new List<string>();

        while (DateTimeOffset.UtcNow < deadline)
        {
            var services = await raw.CoreV1.ListNamespacedServiceAsync(
                Namespace, labelSelector: selector, cancellationToken: ct);

            seen.Clear();
            foreach (var service in services.Items)
            {
                var endpoints = await raw.CoreV1.ReadNamespacedEndpointsAsync(
                    service.Metadata.Name, Namespace, cancellationToken: ct);

                foreach (var subset in endpoints.Subsets ?? new List<V1EndpointSubset>())
                {
                    foreach (var address in subset.Addresses ?? new List<V1EndpointAddress>())
                    {
                        if (!string.IsNullOrWhiteSpace(address.Ip))
                            seen.Add(address.Ip);
                    }
                }
            }

            if (podIp is not null && seen.Contains(podIp, StringComparer.Ordinal))
                return seen;

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return seen;
    }

    private static string? ReadMacvlanParent(string? specConfigJson)
    {
        if (string.IsNullOrWhiteSpace(specConfigJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(specConfigJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("master", out var direct) && direct.ValueKind == System.Text.Json.JsonValueKind.String)
                return direct.GetString();

            if (root.TryGetProperty("plugins", out var plugins)
                && plugins.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var plugin in plugins.EnumerateArray())
                {
                    if (plugin.TryGetProperty("master", out var m)
                        && m.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        return m.GetString();
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        return null;
    }

    private static async Task<string> ReadNadSpecConfigAsync(k8s.Kubernetes raw, string name, CancellationToken ct)
    {
        object body;
        try
        {
            body = await raw.CustomObjects.GetNamespacedCustomObjectAsync(
                group: "k8s.cni.cncf.io",
                version: "v1",
                namespaceParameter: Namespace,
                plural: "network-attachment-definitions",
                name: name,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"NetworkAttachmentDefinition '{name}' is not readable in namespace '{Namespace}'. The Multus setup "
                + "requires BOTH a suppressed and an unsuppressed NAD to exist there before this run; see "
                + "the Multus section of CONTRIBUTING.md.", ex);
        }

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        if (!doc.RootElement.TryGetProperty("spec", out var spec)
            || !spec.TryGetProperty("config", out var config))
        {
            throw new InvalidOperationException($"NetworkAttachmentDefinition '{name}' carries no spec.config.");
        }

        return config.ValueKind == JsonValueKind.String ? config.GetString()! : config.GetRawText();
    }

    private static NetworkAttachmentEvaluationRequest EvaluationRequestFor(string name, bool exists)
        => new()
        {
            Provider = KubernetesProvider.Id,
            Mode = WorkloadNetworkMode.OperatorNetwork,
            Kind = WorkloadKind.Service,
            NodeEligibilitySelector = new Dictionary<string, string> { ["reeve.io/iot-vlan"] = "true" },
            Attachments = [new NetworkAttachmentObservation { Name = name, Exists = exists }],
        };

    private sealed record MultusNetworkStatus(string? Name, string? Interface, IReadOnlyList<string> Ips, bool Default);

    private static IReadOnlyList<MultusNetworkStatus> ReadNetworkStatuses(V1Pod pod)
    {
        var annotations = pod.Metadata.Annotations ?? new Dictionary<string, string>();
        if (!annotations.TryGetValue(NetworkStatusAnnotationKey, out var json) || string.IsNullOrWhiteSpace(json))
            return Array.Empty<MultusNetworkStatus>();

        using var doc = JsonDocument.Parse(json);
        var result = new List<MultusNetworkStatus>();

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var ips = new List<string>();
            if (entry.TryGetProperty("ips", out var ipArray) && ipArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var ip in ipArray.EnumerateArray())
                {
                    if (ip.GetString() is { Length: > 0 } value)
                        ips.Add(value);
                }
            }

            result.Add(new MultusNetworkStatus(
                Name: entry.TryGetProperty("name", out var n) ? n.GetString() : null,
                Interface: entry.TryGetProperty("interface", out var i) ? i.GetString() : null,
                Ips: ips,
                Default: entry.TryGetProperty("default", out var d)
                    && d.ValueKind == JsonValueKind.True));
        }

        return result;
    }

    private static bool IsIpv4(string address)
        => IPAddress.TryParse(address.Split('/')[0], out var parsed)
            && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    private static bool IsInCidr(string address, string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0)
            return false;

        if (!IPAddress.TryParse(cidr[..slash], out var network)
            || !int.TryParse(cidr[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)
            || !IPAddress.TryParse(address.Split('/')[0], out var candidate))
        {
            return false;
        }

        var networkBytes = network.GetAddressBytes();
        var candidateBytes = candidate.GetAddressBytes();
        if (networkBytes.Length != candidateBytes.Length)
            return false;

        var networkValue = BitConverter.ToUInt32(networkBytes.Reverse().ToArray(), 0);
        var candidateValue = BitConverter.ToUInt32(candidateBytes.Reverse().ToArray(), 0);
        var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);

        return (networkValue & mask) == (candidateValue & mask);
    }

    private async Task<string> RunHelperJobAsync(
        k8s.Kubernetes raw,
        string jobName,
        IReadOnlyList<string>? attachments,
        bool hostNetwork,
        string? nodeName,
        string command,
        CancellationToken ct)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TestJobLabelKey] = jobName,
        };

        var podAnnotations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (attachments is { Count: > 0 })
        {
            podAnnotations[NetworksAnnotationKey] = JsonSerializer.Serialize(
                attachments.Select(a => new Dictionary<string, string> { ["name"] = a }).ToList());
        }

        var job = new V1Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new V1ObjectMeta { Name = jobName, NamespaceProperty = Namespace, Labels = labels },
            Spec = new V1JobSpec
            {
                BackoffLimit = 0,
                ActiveDeadlineSeconds = 120,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = labels,
                        Annotations = podAnnotations.Count > 0 ? podAnnotations : null,
                    },
                    Spec = new V1PodSpec
                    {
                        RestartPolicy = "Never",
                        HostNetwork = hostNetwork,
                        DnsPolicy = hostNetwork ? "ClusterFirstWithHostNet" : "ClusterFirst",
                        NodeName = nodeName,
                        Containers =
                        [
                            new V1Container
                            {
                                Name = "helper",
                                Image = HelperImage,
                                Command = ["/bin/sh", "-c", command],
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity>(StringComparer.Ordinal)
                                    {
                                        ["cpu"] = new("10m"),
                                        ["memory"] = new("32Mi"),
                                    },
                                },
                            },
                        ],
                    },
                },
            },
        };

        try
        {
            await raw.BatchV1.CreateNamespacedJobAsync(job, Namespace, cancellationToken: ct);

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
            string? podName = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var pods = await raw.CoreV1.ListNamespacedPodAsync(
                    Namespace, labelSelector: $"{TestJobLabelKey}={jobName}", cancellationToken: ct);

                var pod = pods.Items.FirstOrDefault();
                if (pod is not null)
                {
                    podName = pod.Metadata.Name;
                    if (pod.Status?.Phase is "Succeeded" or "Failed")
                        break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            if (podName is null)
            {
                throw new InvalidOperationException(
                    $"Helper Job '{jobName}' never produced a pod. On an attached Job this is the missing-DHCP-daemon failure "
                    + "(no per-node cni-dhcp daemon ⇒ CNI ADD fails ⇒ the pod never leaves ContainerCreating).");
            }

            using var stream = await raw.CoreV1.ReadNamespacedPodLogAsync(
                name: podName, namespaceParameter: Namespace, cancellationToken: ct);

            if (stream is null)
                return string.Empty;

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        }
        finally
        {
            try
            {
                await raw.BatchV1.DeleteNamespacedJobAsync(
                    jobName, Namespace, propagationPolicy: "Background", cancellationToken: CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    private static async Task SafeTeardownAsync(IWorkloadProvider provider, WorkloadRef reference)
    {
        try
        {
            await provider.TeardownAsync(reference, retainVolumes: false, CancellationToken.None);
        }
        catch
        {
        }
    }

    private sealed class InMemorySettingsService
        : IWorkloadOrchestratorSettingsService, IWorkloadOrchestratorSecretReader
    {
        public async Task<WorkloadOrchestratorSettings> GetDecryptedSettingsAsync(
            CancellationToken cancellationToken = default)
            => (await GetAsync(cancellationToken)).Settings;

        public Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = E2eSettings(),
                EffectiveProvider = KubernetesProvider.Id,
                AutoDetected = false,
                Providers = Array.Empty<WorkloadProviderInfo>(),
            });


        public Task<WorkloadProviderProbeResult> TestAsync(
            WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The e2e stub does not probe.");
    }

    private sealed class NoCredentialsPort : IWorkloadCredentialPort
    {
        public Task<string?> ResolveOrMintAsync(
            Guid instanceId, string credentialId, string? existingMaterial, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>($"rwt_{instanceId:N}");

        public Task RevokeAllAsync(
            Guid instanceId, Guid? actorUserId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
