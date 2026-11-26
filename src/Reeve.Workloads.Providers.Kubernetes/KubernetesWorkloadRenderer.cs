using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using k8s.Models;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesWorkloadRenderer
{
    public const string RestartGenerationAnnotationKey = "reeve.io/restart-generation";

    public const string CredentialDigestAnnotationKey = "reeve.io/credential-digest";

    public const string NetworksAnnotationKey = "k8s.v1.cni.cncf.io/networks";

    private const string OperatorNetworkDnsPolicy = "ClusterFirst";

    private const string HostNetworkDnsPolicy = "ClusterFirstWithHostNet";

    private sealed record MultusNetworkSelection
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("namespace")]
        public required string Namespace { get; init; }
    }

    public static string? RenderNetworksAnnotationValue(WorkloadDesiredSpec spec, string targetNamespace)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Network is not { } net || net.Attachments.Count == 0)
        {
            return null;
        }

        var selections = net.Attachments
            .Select(name => new MultusNetworkSelection { Name = name, Namespace = targetNamespace })
            .ToList();

        return JsonSerializer.Serialize(selections, WorkloadJsonDefaults.CanonicalWire);
    }

    private const long ScanJobActiveDeadlineSeconds = 300;
    private const int ScanJobTtlSecondsAfterFinished = 300;

    public RenderedKubernetesObjects Render(
        WorkloadDesiredSpec spec,
        KubernetesRenderContext ctx,
        IReadOnlyList<string>? readyNodeNames = null,
        string? scanRunId = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(ctx);

        var pvcs = spec.Volumes.Select(v => RenderPvc(spec, ctx, v)).ToList();
        var podSpec = RenderPodSpec(spec, ctx);

        var podAnnotations = new Dictionary<string, string>
        {
            [WorkloadResourceLabels.SpecHashLabelKey] = spec.SpecHash,
        };

        if (spec.RestartNonce != 0)
        {
            podAnnotations[RestartGenerationAnnotationKey] = spec.RestartNonce.ToString(CultureInfo.InvariantCulture);
        }

        if (RenderNetworksAnnotationValue(spec, ctx.Namespace) is { } networksAnnotation)
        {
            podAnnotations[NetworksAnnotationKey] = networksAnnotation;
        }

        var podTemplate = new V1PodTemplateSpec
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string>(spec.Labels),
                Annotations = podAnnotations,
            },
            Spec = podSpec,
        };

        var selector = new V1LabelSelector
        {
            MatchLabels = InstanceSelector(spec),
        };

        V1Deployment? deployment = null;
        V1DaemonSet? daemonSet = null;
        IReadOnlyList<V1Job> jobs = [];

        switch (spec.Kind)
        {
            case WorkloadKind.ScanPerNode:
                jobs = RenderScanJobs(spec, ctx, podAnnotations, readyNodeNames ?? [], scanRunId);
                break;

            case WorkloadKind.PerNode:
                daemonSet = new V1DaemonSet
                {
                    ApiVersion = "apps/v1",
                    Kind = "DaemonSet",
                    Metadata = ObjectMeta(spec, ctx, spec.ResourceName),
                    Spec = new V1DaemonSetSpec
                    {
                        Selector = selector,
                        Template = podTemplate,
                    },
                };
                break;

            case WorkloadKind.Service:
            case WorkloadKind.SingleNode:
            default:
                deployment = new V1Deployment
                {
                    ApiVersion = "apps/v1",
                    Kind = "Deployment",
                    Metadata = ObjectMeta(spec, ctx, spec.ResourceName),
                    Spec = new V1DeploymentSpec
                    {
                        Replicas = 1,
                        Strategy = new V1DeploymentStrategy { Type = "Recreate" },
                        Selector = selector,
                        Template = podTemplate,
                    },
                };
                break;
        }

        return new RenderedKubernetesObjects
        {
            Deployment = deployment,
            DaemonSet = daemonSet,
            Jobs = jobs,
            Service = RenderService(spec, ctx),
            PersistentVolumeClaims = pvcs,
            Secret = RenderSecretShell(spec, ctx),
            ImagePullSecret = RenderImagePullSecretShell(spec, ctx),
        };
    }


    private static IReadOnlyList<V1Job> RenderScanJobs(
        WorkloadDesiredSpec spec,
        KubernetesRenderContext ctx,
        IReadOnlyDictionary<string, string> podAnnotations,
        IReadOnlyList<string> readyNodeNames,
        string? scanRunId)
    {
        var runId = scanRunId ?? string.Empty;
        var jobs = new List<V1Job>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in readyNodeNames)
        {
            if (string.IsNullOrEmpty(node) || !seen.Add(node))
            {
                continue;
            }

            var podSpec = RenderPodSpec(spec, ctx);
            podSpec.NodeName = node;
            podSpec.RestartPolicy = "Never";

            podSpec.NodeSelector = null;

            var template = new V1PodTemplateSpec
            {
                Metadata = new V1ObjectMeta
                {
                    Labels = ScanJobLabels(spec, node, runId),
                    Annotations = new Dictionary<string, string>(podAnnotations),
                },
                Spec = podSpec,
            };

            jobs.Add(new V1Job
            {
                ApiVersion = "batch/v1",
                Kind = "Job",
                Metadata = new V1ObjectMeta
                {
                    Name = WorkloadResourceNaming.ScanPerNodeJobName(spec.GroupId, spec.WorkloadId, spec.Inst8, node, runId),
                    NamespaceProperty = ctx.Namespace,
                    Labels = ScanJobLabels(spec, node, runId),
                },
                Spec = new V1JobSpec
                {
                    BackoffLimit = 0,
                    Completions = 1,
                    Parallelism = 1,
                    ActiveDeadlineSeconds = ScanJobActiveDeadlineSeconds,
                    TtlSecondsAfterFinished = ScanJobTtlSecondsAfterFinished,
                    Template = template,
                },
            });
        }

        return jobs;
    }

    private static Dictionary<string, string> ScanJobLabels(WorkloadDesiredSpec spec, string node, string runId)
        => new(WorkloadResourceNaming.StampScanLabels(spec.Labels, node, runId));


    private static V1PodSpec RenderPodSpec(WorkloadDesiredSpec spec, KubernetesRenderContext ctx)
    {
        var volumes = new List<V1Volume>();
        var mounts = new List<V1VolumeMount>();

        BuildStorage(spec, volumes, mounts);

        var container = new V1Container
        {
            Name = ContainerName(spec),
            Image = spec.Image,
            Env = RenderEnv(spec),
            Resources = RenderResources(spec.Resources),
            SecurityContext = RenderSecurityContext(spec.Privileges),
            VolumeMounts = mounts.Count > 0 ? mounts : null,
        };

        var pod = new V1PodSpec
        {
            Containers = new List<V1Container> { container },
            Volumes = volumes.Count > 0 ? volumes : null,
        };

        pod.SecurityContext = RenderPodSecurityContext(spec.Privileges);

        ApplyNetworking(spec, pod);

        if (spec.UserNamespace)
        {
            if (pod.HostNetwork == true)
            {
                throw new InvalidOperationException(
                    $"Incoherent pod for workload '{spec.WorkloadId}': a user namespace was requested together with "
                    + "hostNetwork, which Kubernetes rejects. The renderer refuses to emit it.");
            }

            pod.HostUsers = false;
        }

        if (spec.NodeSelector.Count > 0)
        {
            pod.NodeSelector = new Dictionary<string, string>(spec.NodeSelector);
        }

        var affinity = RenderArchAffinity(spec.Platforms);
        if (affinity is not null)
        {
            pod.Affinity = affinity;
        }

        var pullSecretNames = new List<string>();
        if (!string.IsNullOrEmpty(ctx.ImagePullSecretName))
        {
            pullSecretNames.Add(ctx.ImagePullSecretName);
        }

        if (!string.IsNullOrEmpty(ctx.AutoImagePullSecretName)
            && !pullSecretNames.Contains(ctx.AutoImagePullSecretName, StringComparer.Ordinal))
        {
            pullSecretNames.Add(ctx.AutoImagePullSecretName);
        }

        if (pullSecretNames.Count > 0)
        {
            pod.ImagePullSecrets = pullSecretNames
                .Select(name => new V1LocalObjectReference { Name = name })
                .ToList();
        }

        return pod;
    }

    private static void ApplyNetworking(WorkloadDesiredSpec spec, V1PodSpec pod)
    {
        if (spec.Network is not { } net)
        {
            return;
        }

        if (net.Attachments.Count > 0 && (net.HostNetwork || net.Mode == WorkloadNetworkMode.HostNetwork))
        {
            throw new InvalidOperationException(
                $"Incoherent network posture for workload '{spec.WorkloadId}': hostNetwork is set together with "
                + $"{net.Attachments.Count} network attachment(s). A host-networked pod shares the node's network "
                + "namespace and cannot also take an operator-network attachment (two default routes; the Service "
                + "CIDR becomes unreachable). The renderer refuses to emit an incoherent pod spec.");
        }

        if (net.Mode == WorkloadNetworkMode.OperatorNetwork && net.Attachments.Count == 0)
        {
            throw new InvalidOperationException(
                $"Incoherent network posture for workload '{spec.WorkloadId}': network mode is 'operatorNetwork' but "
                + "no network attachment was resolved. The renderer refuses to emit a pod that declares an "
                + "operator-network posture with no interface to attach.");
        }

        if (net.HostNetwork || net.Mode == WorkloadNetworkMode.HostNetwork)
        {
            pod.HostNetwork = true;
            pod.DnsPolicy = HostNetworkDnsPolicy;
            return;
        }

        if (net.Mode == WorkloadNetworkMode.OperatorNetwork)
        {
            pod.HostNetwork = false;
            pod.DnsPolicy = OperatorNetworkDnsPolicy;
        }
    }


    private static IList<V1EnvVar> RenderEnv(WorkloadDesiredSpec spec)
    {
        var env = new List<V1EnvVar>();
        foreach (var e in spec.Env)
        {
            switch (e.Kind)
            {
                case ResolvedEnvKind.SecretRef:
                    env.Add(new V1EnvVar
                    {
                        Name = e.Name,
                        ValueFrom = new V1EnvVarSource
                        {
                            SecretKeyRef = new V1SecretKeySelector
                            {
                                Name = spec.SecretName,
                                Key = e.Name,
                            },
                        },
                    });
                    break;

                case ResolvedEnvKind.Literal:
                case ResolvedEnvKind.SpecHash:
                default:
                    env.Add(new V1EnvVar { Name = e.Name, Value = KubernetesEnvValue.Escape(e.Value) });
                    break;
            }
        }

        if (spec.Kind is WorkloadKind.PerNode or WorkloadKind.ScanPerNode
            && !env.Any(v => v.Name == NodeNameDownwardApiEnv))
        {
            env.Add(new V1EnvVar
            {
                Name = NodeNameDownwardApiEnv,
                ValueFrom = new V1EnvVarSource
                {
                    FieldRef = new V1ObjectFieldSelector { FieldPath = "spec.nodeName" },
                },
            });
        }

        return env;
    }

    private const string NodeNameDownwardApiEnv = "NODE_NAME";

    private static bool Require(ResourceQuantityKind kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!ResourceQuantityPolicy.TryParseForRender(kind, value, out _))
        {
            throw new WorkloadResourceRenderException(
                $"The resolved {kind} quantity '{value}' is not a valid Kubernetes quantity, so this workload cannot "
                + "be rendered. Correct it on the workload's Resources panel.");
        }

        return true;
    }

    private static V1ResourceRequirements? RenderResources(ResolvedResources? r)
    {
        if (r is null)
        {
            return null;
        }

        var requests = new Dictionary<string, ResourceQuantity>();
        var limits = new Dictionary<string, ResourceQuantity>();

        if (Require(ResourceQuantityKind.Cpu, r.RequestsCpu))
            requests["cpu"] = new ResourceQuantity(r.RequestsCpu);
        if (Require(ResourceQuantityKind.Memory, r.RequestsMemory))
            requests["memory"] = new ResourceQuantity(r.RequestsMemory);
        if (Require(ResourceQuantityKind.Cpu, r.LimitsCpu))
            limits["cpu"] = new ResourceQuantity(r.LimitsCpu);
        if (Require(ResourceQuantityKind.Memory, r.LimitsMemory))
            limits["memory"] = new ResourceQuantity(r.LimitsMemory);

        if (requests.Count == 0 && limits.Count == 0)
        {
            return null;
        }

        return new V1ResourceRequirements
        {
            Requests = requests.Count > 0 ? requests : null,
            Limits = limits.Count > 0 ? limits : null,
        };
    }

    private static V1PodSecurityContext? RenderPodSecurityContext(ResolvedPrivileges? priv)
    {
        if (priv is null || (priv.DeviceGroupIds.Count == 0 && priv.FsGroup is null))
        {
            return null;
        }

        var context = new V1PodSecurityContext();
        if (priv.DeviceGroupIds.Count > 0)
        {
            context.SupplementalGroups = priv.DeviceGroupIds.OrderBy(g => g).Select(g => (long?)g).ToList();
        }

        if (priv.FsGroup is { } fsGroup)
        {
            context.FsGroup = fsGroup;
            context.FsGroupChangePolicy = "OnRootMismatch";
        }

        return context;
    }

    private static V1SecurityContext RenderSecurityContext(ResolvedPrivileges? priv)
    {
        var sc = new V1SecurityContext
        {
            Capabilities = new V1Capabilities
            {
                Drop = new List<string> { "ALL" },
                Add = priv is { Capabilities.Count: > 0 } ? priv.Capabilities.ToList() : null,
            },
            AllowPrivilegeEscalation = false,
            RunAsNonRoot = true,
            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
        };

        if (priv is null)
        {
            return sc;
        }

        if (priv.EffectivePrivileged)
        {
            sc.Privileged = true;
            sc.AllowPrivilegeEscalation = null;
        }

        if (priv.RunAsRoot)
        {
            sc.RunAsUser = 0;
            sc.RunAsNonRoot = false;
        }

        return sc;
    }


    private static void BuildStorage(WorkloadDesiredSpec spec, List<V1Volume> volumes, List<V1VolumeMount> mounts)
    {
        for (var i = 0; i < spec.Volumes.Count; i++)
        {
            var v = spec.Volumes[i];
            var name = $"vol-{Sanitize(v.Id)}";
            volumes.Add(new V1Volume
            {
                Name = name,
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = v.ResourceName },
            });
            mounts.Add(new V1VolumeMount { Name = name, MountPath = v.MountPath });
        }

        for (var i = 0; i < spec.ConfigMapMounts.Count; i++)
        {
            var m = spec.ConfigMapMounts[i];
            var name = $"cfg-{i}";
            volumes.Add(new V1Volume
            {
                Name = name,
                ConfigMap = new V1ConfigMapVolumeSource { Name = m.ConfigMapName },
            });
            mounts.Add(new V1VolumeMount { Name = name, MountPath = m.MountPath, ReadOnlyProperty = true });
        }

        if (spec.Privileges is not { } priv)
        {
            return;
        }

        foreach (var source in HostPathSources(priv))
        {
            volumes.Add(new V1Volume
            {
                Name = source.VolumeName,
                HostPath = new V1HostPathVolumeSource { Path = source.HostPath, Type = source.HostPathType },
            });
            mounts.Add(new V1VolumeMount
            {
                Name = source.VolumeName,
                MountPath = source.MountPath,
                ReadOnlyProperty = source.ReadOnly ? true : null,
            });
        }
    }

    internal sealed record HostPathSource(
        string VolumeName, string HostPath, string MountPath, string? HostPathType, bool ReadOnly);

    internal static IReadOnlyList<HostPathSource> HostPathSources(ResolvedPrivileges priv)
    {
        var sources = new List<HostPathSource>();

        if (priv.NodeLocalState is { } nls && !string.IsNullOrEmpty(nls.HostPath))
        {
            sources.Add(new("nodestate", nls.HostPath, nls.HostPath, "DirectoryOrCreate", ReadOnly: false));
        }

        if (!string.IsNullOrEmpty(priv.DbusHostPath))
        {
            sources.Add(new("dbus", priv.DbusHostPath, priv.DbusHostPath, HostPathType: null, ReadOnly: true));
        }

        if (priv.TunDevice)
        {
            sources.Add(new("tun", TunDevicePath, TunDevicePath, "CharDevice", ReadOnly: false));
        }

        for (var i = 0; i < priv.DevicePaths.Count; i++)
        {
            var path = priv.DevicePaths[i];
            if (!string.IsNullOrEmpty(path))
            {
                sources.Add(new($"dev-{i}", path, path, "CharDevice", ReadOnly: false));
            }
        }

        for (var i = 0; i < priv.HostPaths.Count; i++)
        {
            var hp = priv.HostPaths[i];
            if (!string.IsNullOrEmpty(hp.HostPath) && !string.IsNullOrEmpty(hp.MountPath))
            {
                sources.Add(new($"hostpath-{i}", hp.HostPath, hp.MountPath, HostPathType: null, ReadOnly: true));
            }
        }

        return sources;
    }

    internal const string TunDevicePath = "/dev/net/tun";

    internal static V1Affinity? RenderArchAffinity(IReadOnlyList<string> platforms)
    {
        if (platforms.Count == 0)
        {
            return null;
        }

        var archs = platforms
            .Select(ArchOf)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        if (archs.Count == 0)
        {
            return null;
        }

        return new V1Affinity
        {
            NodeAffinity = new V1NodeAffinity
            {
                RequiredDuringSchedulingIgnoredDuringExecution = new V1NodeSelector
                {
                    NodeSelectorTerms = new List<V1NodeSelectorTerm>
                    {
                        new()
                        {
                            MatchExpressions = new List<V1NodeSelectorRequirement>
                            {
                                new()
                                {
                                    Key = "kubernetes.io/arch",
                                    OperatorProperty = "In",
                                    Values = archs,
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    private static string ArchOf(string platform)
    {
        if (string.IsNullOrEmpty(platform))
        {
            return string.Empty;
        }

        var slash = platform.LastIndexOf('/');
        return slash < 0 ? platform : platform[(slash + 1)..];
    }


    private static V1Service? RenderService(WorkloadDesiredSpec spec, KubernetesRenderContext ctx)
    {
        if (spec.Kind == WorkloadKind.ScanPerNode)
        {
            return null;
        }

        var ports = spec.Network?.Ports ?? [];
        var metrics = spec.Observability?.Metrics;
        if (ports.Count == 0 && metrics is null)
        {
            return null;
        }

        if (ports.Count == 0)
        {
            return null;
        }

        var servicePorts = ports
            .Select(p => new V1ServicePort
            {
                Name = p.Name,
                Port = p.Port,
                Protocol = ProtocolWire(p.Protocol),
            })
            .ToList();

        var meta = ObjectMeta(spec, ctx, spec.ResourceName);

        if (metrics is not null)
        {
            var metricsAlreadyExposed = servicePorts
                .Any(sp => sp.Port == metrics.Port && string.Equals(sp.Protocol, "TCP", StringComparison.Ordinal));
            if (!metricsAlreadyExposed)
            {
                servicePorts.Add(new V1ServicePort
                {
                    Name = "metrics",
                    Port = metrics.Port,
                    Protocol = "TCP",
                });
            }

            meta.Annotations ??= new Dictionary<string, string>();
            meta.Annotations["prometheus.io/scrape"] = "true";
            meta.Annotations["prometheus.io/port"] = metrics.Port.ToString(CultureInfo.InvariantCulture);
            meta.Annotations["prometheus.io/path"] = metrics.Path;
        }

        return new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = meta,
            Spec = new V1ServiceSpec
            {
                Type = "ClusterIP",
                Selector = InstanceSelector(spec),
                Ports = servicePorts,
            },
        };
    }


    private static V1PersistentVolumeClaim RenderPvc(WorkloadDesiredSpec spec, KubernetesRenderContext ctx, ResolvedVolume v)
    {
        var labels = new Dictionary<string, string>(spec.Labels)
        {
            [WorkloadResourceLabels.CriticalityLabelKey] = CriticalityWire(v.Criticality),
        };

        return new V1PersistentVolumeClaim
        {
            ApiVersion = "v1",
            Kind = "PersistentVolumeClaim",
            Metadata = new V1ObjectMeta
            {
                Name = v.ResourceName,
                NamespaceProperty = ctx.Namespace,
                Labels = labels,
            },
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = new List<string> { AccessModeWire(v.AccessMode) },
                StorageClassName = v.StorageClassName ?? ctx.DefaultStorageClassName,
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        ["storage"] = new(v.Size),
                    },
                },
            },
        };
    }


    private static V1Secret RenderSecretShell(WorkloadDesiredSpec spec, KubernetesRenderContext ctx)
        => new()
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Metadata = ObjectMeta(spec, ctx, spec.SecretName),
            Type = "Opaque",
            StringData = new Dictionary<string, string>(),
        };

    private static V1Secret? RenderImagePullSecretShell(WorkloadDesiredSpec spec, KubernetesRenderContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.AutoImagePullSecretName))
        {
            return null;
        }

        return new V1Secret
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Metadata = ObjectMeta(spec, ctx, ctx.AutoImagePullSecretName),
            Type = "kubernetes.io/dockerconfigjson",
            StringData = new Dictionary<string, string>(),
        };
    }


    private static V1ObjectMeta ObjectMeta(WorkloadDesiredSpec spec, KubernetesRenderContext ctx, string name)
        => new()
        {
            Name = name,
            NamespaceProperty = ctx.Namespace,
            Labels = new Dictionary<string, string>(spec.Labels),
        };

    internal static IDictionary<string, string> InstanceSelector(WorkloadDesiredSpec spec)
    {
        var value = spec.Labels.TryGetValue(WorkloadResourceLabels.InstanceLabelKey, out var v)
            ? v
            : spec.InstanceId.ToString("D");
        return new Dictionary<string, string> { [WorkloadResourceLabels.InstanceLabelKey] = value };
    }

    private static string ContainerName(WorkloadDesiredSpec spec)
    {
        var name = Sanitize(spec.WorkloadId);
        return string.IsNullOrEmpty(name) ? "workload" : name;
    }

    private static string ProtocolWire(string protocol)
        => string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase) ? "UDP" : "TCP";

    private static string AccessModeWire(string accessMode)
        => accessMode switch
        {
            "rwx" => "ReadWriteMany",
            "rox" => "ReadOnlyMany",
            _ => "ReadWriteOnce",
        };

    private static string CriticalityWire(VolumeCriticality c) => c switch
    {
        VolumeCriticality.Cache => "cache",
        VolumeCriticality.Recoverable => "recoverable",
        VolumeCriticality.Critical => "critical",
        _ => c.ToString().ToLowerInvariant(),
    };

    private static string Sanitize(string value) => WorkloadResourceNaming.Sanitize(value);
}
