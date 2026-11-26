using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Reeve.Workloads.Providers.Kubernetes.IntegrationTests;

public sealed class KubernetesProviderK3dTests
{
    private static readonly InMemorySettingsService _stubSettings = new();

    private const string Namespace = "reeve-e2e";

    private const string WorkloadImage =
        "registry.k8s.io/pause@sha256:ee6521f290b2168b6e0935a181d4cff9be1ac3f505666ef0e3c98fae8199917a";

    private const string ScanWorkloadImage =
        "docker.io/library/hello-world@sha256:5e23090353324d887c48ad5e5c56d294eab81588df9605b07d1afe895f9cc8f8";

    private readonly ITestOutputHelper _output;

    public KubernetesProviderK3dTests(ITestOutputHelper output) => _output = output;

    private static string? KubeconfigPath => Environment.GetEnvironmentVariable("REEVE_E2E_KUBECONFIG");

    [SkippableFact]
    public async Task Full_lifecycle_apply_status_suspend_resume_teardown_against_live_k3d()
    {
        var kubeconfig = KubeconfigPath;
        Skip.If(string.IsNullOrWhiteSpace(kubeconfig),
            "Set REEVE_E2E_KUBECONFIG=<kubeconfig> (pointing at a live cluster, for example k3d or kind, with a 'reeve-e2e' namespace) to run the live-cluster e2e.");

        var instanceId = Guid.NewGuid();
        var inst8 = instanceId.ToString("N")[..8];
        var spec = BuildSpec(instanceId, inst8);
        var reference = WorkloadRef.FromDesiredSpec(spec);
        var selector = LabelSelectorFor(instanceId);

        var provider = BuildProvider(kubeconfig!);
        using var raw = RawClient(kubeconfig!);
        var ct = TestCt();

        try
        {
            var apply = await provider.ApplyAsync(spec, null, ct);
            _output.WriteLine($"ApplyAsync -> Succeeded={apply.Succeeded} Reason={apply.Reason} Hash={apply.AppliedSpecHash} Msg={apply.Message}");

            apply.Succeeded.Should().BeTrue("a fresh apply against a reachable cluster must succeed");
            apply.Reason.Should().Be(WorkloadApplyReason.Applied);
            apply.AppliedSpecHash.Should().Be(spec.SpecHash, "Applied echoes the desired spec hash");

            var deployList = await raw.AppsV1.ListNamespacedDeploymentAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            var svcList = await raw.CoreV1.ListNamespacedServiceAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            var pvcList = await raw.CoreV1.ListNamespacedPersistentVolumeClaimAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            var secretList = await raw.CoreV1.ListNamespacedSecretAsync(Namespace, labelSelector: selector, cancellationToken: ct);

            deployList.Items.Should().ContainSingle("exactly one Deployment materializes for a Service-kind workload");
            svcList.Items.Should().ContainSingle("a workload with a port renders a ClusterIP Service");
            pvcList.Items.Should().ContainSingle("the single declared volume renders one PVC");
            secretList.Items.Should().ContainSingle("the per-instance token Secret is applied");

            var deployment = deployList.Items[0];
            var labelValue = instanceId.ToString("D");
            deployment.Metadata.Labels.Should().ContainKey(WorkloadResourceLabels.InstanceLabelKey)
                .WhoseValue.Should().Be(labelValue);
            svcList.Items[0].Metadata.Labels[WorkloadResourceLabels.InstanceLabelKey].Should().Be(labelValue);
            pvcList.Items[0].Metadata.Labels[WorkloadResourceLabels.InstanceLabelKey].Should().Be(labelValue);
            secretList.Items[0].Metadata.Labels[WorkloadResourceLabels.InstanceLabelKey].Should().Be(labelValue);

            var managers = (deployment.Metadata.ManagedFields ?? new List<k8s.Models.V1ManagedFieldsEntry>())
                .Select(m => m.Manager).ToList();
            _output.WriteLine($"Deployment managedFields managers: [{string.Join(", ", managers)}]");
            managers.Should().Contain(KubernetesWorkloadProvider.FieldManager,
                "the deployment was server-side-applied under fieldManager reeve-workloads");

            var secret = await raw.CoreV1.ReadNamespacedSecretAsync(secretList.Items[0].Metadata.Name, Namespace, cancellationToken: ct);
            secret.Data.Should().ContainKey("REEVE_WORKLOAD_TOKEN");
            var tokenValue = System.Text.Encoding.UTF8.GetString(secret.Data["REEVE_WORKLOAD_TOKEN"]);
            tokenValue.Should().StartWith("minted-", "the minted workload credential is carried in the Secret");
            _output.WriteLine($"Secret {secret.Metadata.Name} REEVE_WORKLOAD_TOKEN prefix: {tokenValue[..Math.Min(12, tokenValue.Length)]}...");

            WorkloadStatus status = await PollStatusAsync(
                provider, reference,
                accept: s => s.Phase is WorkloadProviderPhase.Running or WorkloadProviderPhase.Ready,
                timeout: TimeSpan.FromSeconds(60), ct: ct);

            _output.WriteLine($"GetStatus -> Phase={status.Phase} ProviderReady={status.ProviderReady} Restarts={status.RestartCount} Msg={status.Message} Image={status.ObservedImage}");
            status.Phase.Should().BeOneOf(
                new[] { WorkloadProviderPhase.Running, WorkloadProviderPhase.Ready },
                "once the pod schedules the provider reports it Running (Ready once its container reports ready)");

            await provider.SuspendAsync(reference, ct);
            var afterSuspend = await raw.AppsV1.ReadNamespacedDeploymentAsync(deployment.Metadata.Name, Namespace, cancellationToken: ct);
            _output.WriteLine($"After Suspend: spec.replicas={afterSuspend.Spec.Replicas}");
            afterSuspend.Spec.Replicas.Should().Be(0, "suspend scales the Deployment to zero replicas");

            await provider.ResumeAsync(reference, ct);
            var afterResume = await raw.AppsV1.ReadNamespacedDeploymentAsync(deployment.Metadata.Name, Namespace, cancellationToken: ct);
            _output.WriteLine($"After Resume: spec.replicas={afterResume.Spec.Replicas}");
            afterResume.Spec.Replicas.Should().Be(1, "resume restores the Deployment to one replica");

            var reapply = await provider.ApplyAsync(spec, null, ct);
            _output.WriteLine($"Re-ApplyAsync -> Succeeded={reapply.Succeeded} Reason={reapply.Reason}");
            reapply.Succeeded.Should().BeTrue("re-applying an unchanged spec is an idempotent no-op that still succeeds");

            var deployAfterReapply = await raw.AppsV1.ListNamespacedDeploymentAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            deployAfterReapply.Items.Should().ContainSingle("a re-apply upserts the same object — never a duplicate");

            await provider.TeardownAsync(reference, retainVolumes: true, ct);

            (await raw.AppsV1.ListNamespacedDeploymentAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("teardown deletes the Deployment");
            (await raw.CoreV1.ListNamespacedServiceAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("teardown deletes the Service");
            (await raw.CoreV1.ListNamespacedSecretAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("teardown deletes the Secret");

            var pvcAfterRetain = await raw.CoreV1.ListNamespacedPersistentVolumeClaimAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            _output.WriteLine($"After Teardown(retain=true): PVCs remaining = {pvcAfterRetain.Items.Count}");
            pvcAfterRetain.Items.Should().ContainSingle("retainVolumes keeps the PVC");

            await provider.TeardownAsync(reference, retainVolumes: false, ct);
            await WaitUntilAsync(async () =>
                    (await raw.CoreV1.ListNamespacedPersistentVolumeClaimAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                    .Items.Count == 0,
                TimeSpan.FromSeconds(30), ct);

            var pvcAfterPurge = await raw.CoreV1.ListNamespacedPersistentVolumeClaimAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            _output.WriteLine($"After Teardown(retain=false): PVCs remaining = {pvcAfterPurge.Items.Count}");
            pvcAfterPurge.Items.Should().BeEmpty("a non-retaining teardown deletes the PVC");
        }
        finally
        {
            await SafeTeardownAsync(provider, reference);
        }
    }

    [SkippableFact]
    public async Task Preflight_reports_api_reachable_and_flags_topology_bound_default_storageclass()
    {
        var kubeconfig = KubeconfigPath;
        Skip.If(string.IsNullOrWhiteSpace(kubeconfig),
            "Set REEVE_E2E_KUBECONFIG=<kubeconfig> to run the live-cluster e2e.");

        var instanceId = Guid.NewGuid();
        var inst8 = instanceId.ToString("N")[..8];
        var spec = BuildSpec(instanceId, inst8);

        var provider = BuildProvider(kubeconfig!);
        var result = await provider.PreflightAsync(spec, TestCt());

        foreach (var c in result.Checks)
        {
            _output.WriteLine($"preflight[{c.Id}] passed={c.Passed} severity={c.Severity} :: {c.Message}");
        }

        var api = result.Checks.SingleOrDefault(c => c.Id == "api-reachable");
        api.Should().NotBeNull("preflight always emits the api-reachable check");
        api!.Passed.Should().BeTrue("the live API server is reachable");

        var topology = result.Checks.SingleOrDefault(c => c.Id == "storageclass-topology");
        topology.Should().NotBeNull("the spec declares a volume, so the storageclass-topology check runs");
        topology!.Passed.Should().BeFalse("the local-path default is topology-bound");
        topology.Severity.Should().Be(PreflightSeverity.Warning, "topology-binding on a homelab default is informational, not blocking");

        result.HasBlockingError.Should().BeFalse("no preflight check blocks a plain workload on a default kind or k3d cluster");
    }

    [SkippableFact]
    public async Task A_user_namespace_reaches_the_cluster_and_the_preflight_reads_the_server_version()
    {
        var kubeconfig = KubeconfigPath;
        Skip.If(string.IsNullOrWhiteSpace(kubeconfig), "Set REEVE_E2E_KUBECONFIG=<kubeconfig> to run the live-cluster e2e.");

        var instanceId = Guid.NewGuid();
        var inst8 = instanceId.ToString("N")[..8];
        var spec = BuildSpec(instanceId, inst8) with { UserNamespace = true };
        var reference = WorkloadRef.FromDesiredSpec(spec);
        var provider = BuildProvider(kubeconfig!);
        using var raw = RawClient(kubeconfig!);
        var ct = TestCt();

        try
        {
            var preflight = await provider.PreflightAsync(spec, ct);
            foreach (var c in preflight.Checks)
            {
                _output.WriteLine($"preflight[{c.Id}] passed={c.Passed} severity={c.Severity} :: {c.Message}");
            }
            preflight.Checks.Should().Contain(c => c.Id == "user-namespaces");
            preflight.HasBlockingError.Should().BeFalse("a user namespace only ever warns");

            var apply = await provider.ApplyAsync(spec, null, ct);
            apply.Succeeded.Should().BeTrue(apply.Message);
            var deployment = (await raw.AppsV1.ListNamespacedDeploymentAsync(
                Namespace, labelSelector: LabelSelectorFor(instanceId), cancellationToken: ct)).Items.Single();
            deployment.Spec.Template.Spec.HostUsers.Should().BeFalse(
                "the API server keeps the field from 1.33 on; a cluster older than that fails here by design");
        }
        finally
        {
            await SafeTeardownAsync(provider, reference);
        }
    }


    [SkippableFact]
    public async Task PerNode_daemonset_lifecycle_apply_status_suspend_resume_teardown_against_live_k3d()
    {
        var kubeconfig = KubeconfigPath;
        Skip.If(string.IsNullOrWhiteSpace(kubeconfig),
            "Set REEVE_E2E_KUBECONFIG=<kubeconfig> (pointing at a live cluster, for example k3d or kind, with a 'reeve-e2e' namespace) to run the live-cluster e2e.");

        var instanceId = Guid.NewGuid();
        var inst8 = instanceId.ToString("N")[..8];
        var spec = BuildSpec(instanceId, inst8, WorkloadKind.PerNode);
        var reference = WorkloadRef.FromDesiredSpec(spec);
        var selector = LabelSelectorFor(instanceId);
        var labelValue = instanceId.ToString("D");
        var suspendKey = WorkloadResourceNaming.SuspendSelectorKey(inst8);

        var provider = BuildProvider(kubeconfig!);
        using var raw = RawClient(kubeconfig!);
        var ct = TestCt(minutes: 8);

        var schedulableNodes = (await raw.CoreV1.ListNodeAsync(cancellationToken: ct)).Items
            .Count(n => (n.Spec?.Taints ?? new List<k8s.Models.V1Taint>())
                .All(t => t.Effect is not ("NoSchedule" or "NoExecute")));
        _output.WriteLine($"schedulable nodes in the cluster: {schedulableNodes}");
        schedulableNodes.Should().BeGreaterThan(0, "a DaemonSet needs at least one schedulable node to mean anything");

        try
        {
            var apply = await provider.ApplyAsync(spec, null, ct);
            _output.WriteLine($"ApplyAsync(per-node) -> Succeeded={apply.Succeeded} Reason={apply.Reason} Hash={apply.AppliedSpecHash} Msg={apply.Message}");

            apply.Succeeded.Should().BeTrue("a fresh per-node apply against a reachable cluster must succeed");
            apply.Reason.Should().Be(WorkloadApplyReason.Applied);
            apply.AppliedSpecHash.Should().Be(spec.SpecHash);

            var dsList = await raw.AppsV1.ListNamespacedDaemonSetAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            var deployList = await raw.AppsV1.ListNamespacedDeploymentAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            var svcList = await raw.CoreV1.ListNamespacedServiceAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            var pvcList = await raw.CoreV1.ListNamespacedPersistentVolumeClaimAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            var secretList = await raw.CoreV1.ListNamespacedSecretAsync(Namespace, labelSelector: selector, cancellationToken: ct);

            dsList.Items.Should().ContainSingle("a per-node workload materializes exactly one DaemonSet");
            deployList.Items.Should().BeEmpty("per-node must never fall through to the Deployment arm");
            svcList.Items.Should().ContainSingle("a per-node workload with a declared port still renders a ClusterIP Service");
            pvcList.Items.Should().BeEmpty("per-node forbids volumes[], so nothing renders a PVC");
            secretList.Items.Should().ContainSingle("the per-instance token Secret is applied for every kind");

            var daemonSet = dsList.Items[0];
            daemonSet.Metadata.Labels.Should().ContainKey(WorkloadResourceLabels.InstanceLabelKey)
                .WhoseValue.Should().Be(labelValue);

            var managers = (daemonSet.Metadata.ManagedFields ?? new List<k8s.Models.V1ManagedFieldsEntry>())
                .Select(m => m.Manager).ToList();
            _output.WriteLine($"DaemonSet {daemonSet.Metadata.Name} managedFields managers: [{string.Join(", ", managers)}]");
            managers.Should().Contain(KubernetesWorkloadProvider.FieldManager,
                "the DaemonSet was server-side-applied under fieldManager reeve-workloads");

            daemonSet.Spec.Template.Spec.NodeSelector.Should().BeNull(
                "a freshly-applied DaemonSet carries no suspend selector — otherwise the suspend assertion below proves nothing");

            var nodeNameEnv = daemonSet.Spec.Template.Spec.Containers[0].Env
                .SingleOrDefault(e => e.Name == "NODE_NAME");
            nodeNameEnv.Should().NotBeNull("a per-node pod learns its scheduling node through the downward API");
            nodeNameEnv!.ValueFrom.FieldRef.FieldPath.Should().Be("spec.nodeName");

            var status = await PollStatusAsync(
                provider, reference,
                accept: s => s.Phase == WorkloadProviderPhase.Ready,
                timeout: TimeSpan.FromMinutes(3), ct: ct);

            _output.WriteLine($"GetStatus(per-node) -> Phase={status.Phase} ProviderReady={status.ProviderReady} Msg={status.Message} Image={status.ObservedImage}");
            status.Phase.Should().Be(WorkloadProviderPhase.Ready, "every node's pause pod reports ready");
            status.ProviderReady.Should().BeTrue();
            status.Message.Should().Be($"{schedulableNodes}/{schedulableNodes} ready.",
                "desired comes from status.desiredNumberScheduled and ready from status.numberReady — a DaemonSet has no spec.replicas to read");

            await provider.SuspendAsync(reference, ct);

            var afterSuspend = await raw.AppsV1.ReadNamespacedDaemonSetAsync(daemonSet.Metadata.Name, Namespace, cancellationToken: ct);
            _output.WriteLine($"After Suspend: pod-template nodeSelector = {Describe(afterSuspend.Spec.Template.Spec.NodeSelector)}");
            afterSuspend.Spec.Template.Spec.NodeSelector.Should().ContainKey(suspendKey)
                .WhoseValue.Should().Be("true", "suspend stamps the no-match selector rather than scaling anything");

            await WaitUntilAsync(async () =>
                    (await raw.AppsV1.ReadNamespacedDaemonSetAsync(daemonSet.Metadata.Name, Namespace, cancellationToken: ct))
                    .Status?.DesiredNumberScheduled == 0,
                TimeSpan.FromMinutes(1), ct);

            var suspendedDs = await raw.AppsV1.ReadNamespacedDaemonSetAsync(daemonSet.Metadata.Name, Namespace, cancellationToken: ct);
            _output.WriteLine($"After Suspend: desiredNumberScheduled={suspendedDs.Status?.DesiredNumberScheduled} numberReady={suspendedDs.Status?.NumberReady}");
            suspendedDs.Status!.DesiredNumberScheduled.Should().Be(0,
                "the no-match selector leaves the controller with no eligible node");

            var suspendedStatus = await PollStatusAsync(
                provider, reference,
                accept: s => s.Phase == WorkloadProviderPhase.Pending,
                timeout: TimeSpan.FromMinutes(2), ct: ct);
            _output.WriteLine($"GetStatus(suspended) -> Phase={suspendedStatus.Phase} Msg={suspendedStatus.Message}");
            suspendedStatus.Phase.Should().Be(WorkloadProviderPhase.Pending);
            suspendedStatus.ProviderReady.Should().BeFalse();
            suspendedStatus.Message.Should().Be("Scaled to 0 — no pods scheduled.",
                "the suspended-DaemonSet arm reaches the same operator-facing message through desiredNumberScheduled=0");

            await provider.ResumeAsync(reference, ct);

            var afterResume = await raw.AppsV1.ReadNamespacedDaemonSetAsync(daemonSet.Metadata.Name, Namespace, cancellationToken: ct);
            _output.WriteLine($"After Resume: pod-template nodeSelector = {Describe(afterResume.Spec.Template.Spec.NodeSelector)}");
            (afterResume.Spec.Template.Spec.NodeSelector ?? new Dictionary<string, string>())
                .Should().NotContainKey(suspendKey, "resume removes the key outright — a leftover false would still be a no-match label");

            await WaitUntilAsync(async () =>
                    (await raw.AppsV1.ReadNamespacedDaemonSetAsync(daemonSet.Metadata.Name, Namespace, cancellationToken: ct))
                    .Status?.DesiredNumberScheduled == schedulableNodes,
                TimeSpan.FromMinutes(1), ct);

            var resumedStatus = await PollStatusAsync(
                provider, reference,
                accept: s => s.Phase == WorkloadProviderPhase.Ready,
                timeout: TimeSpan.FromMinutes(3), ct: ct);
            _output.WriteLine($"GetStatus(resumed) -> Phase={resumedStatus.Phase} Msg={resumedStatus.Message}");
            resumedStatus.Phase.Should().Be(WorkloadProviderPhase.Ready, "resume puts a pod back on every node");
            resumedStatus.Message.Should().Be($"{schedulableNodes}/{schedulableNodes} ready.");

            await provider.TeardownAsync(reference, retainVolumes: false, ct);

            (await raw.AppsV1.ListNamespacedDaemonSetAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("teardown deletes the DaemonSet");
            (await raw.CoreV1.ListNamespacedServiceAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("teardown deletes the Service");
            (await raw.CoreV1.ListNamespacedSecretAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("teardown deletes the Secret");

            var goneStatus = await provider.GetStatusAsync(reference, ct);
            _output.WriteLine($"GetStatus(after teardown) -> Phase={goneStatus.Phase} Msg={goneStatus.Message}");
            goneStatus.Phase.Should().Be(WorkloadProviderPhase.NotFound,
                "with the DaemonSet gone and no Jobs, the read collapses to the NotFound sentinel");
        }
        finally
        {
            await SafeTeardownAsync(provider, reference);
        }
    }

    [SkippableFact]
    public async Task ScanPerNode_job_lifecycle_apply_status_suspend_resume_teardown_against_live_k3d()
    {
        var kubeconfig = KubeconfigPath;
        Skip.If(string.IsNullOrWhiteSpace(kubeconfig),
            "Set REEVE_E2E_KUBECONFIG=<kubeconfig> (pointing at a live cluster, for example k3d or kind, with a 'reeve-e2e' namespace) to run the live-cluster e2e.");

        var instanceId = Guid.NewGuid();
        var inst8 = instanceId.ToString("N")[..8];
        var selector = LabelSelectorFor(instanceId);
        var labelValue = instanceId.ToString("D");

        var provider = BuildProvider(kubeconfig!);
        using var raw = RawClient(kubeconfig!);
        var ct = TestCt(minutes: 18);

        var nodeNames = (await raw.CoreV1.ListNodeAsync(cancellationToken: ct)).Items
            .Select(n => n.Metadata.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        _output.WriteLine($"scan target nodes: [{string.Join(", ", nodeNames)}]");
        nodeNames.Should().NotBeEmpty();

        var idleSpec = BuildScanSpec(instanceId, inst8, pendingScanRunId: null, readyNodeNames: nodeNames);
        var reference = WorkloadRef.FromDesiredSpec(idleSpec);

        try
        {
            var idleApply = await provider.ApplyAsync(idleSpec, null, ct);
            _output.WriteLine($"ApplyAsync(scan, no marker) -> Succeeded={idleApply.Succeeded} Reason={idleApply.Reason} Hash={idleApply.AppliedSpecHash}");
            idleApply.Succeeded.Should().BeTrue();
            idleApply.AppliedSpecHash.Should().Be(idleSpec.SpecHash, "the hash settles even though nothing scanned");

            (await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("no pending run marker ⇒ zero Jobs");
            (await raw.CoreV1.ListNamespacedServiceAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("a run-once fan-out gets no ClusterIP Service, even though the spec declares a port");
            (await raw.CoreV1.ListNamespacedSecretAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().ContainSingle("the token Secret is applied before any run exists");

            var idleStatus = await provider.GetStatusAsync(reference, ct);
            _output.WriteLine($"GetStatus(scan, never run) -> Phase={idleStatus.Phase} Msg={idleStatus.Message}");
            idleStatus.Phase.Should().Be(WorkloadProviderPhase.NotFound,
                "no workload object and no Jobs is the plain NotFound sentinel");

            const string firstRunId = "r1";
            var run1 = BuildScanSpec(instanceId, inst8, pendingScanRunId: firstRunId, readyNodeNames: nodeNames);
            var runApply = await provider.ApplyAsync(run1, null, ct);
            _output.WriteLine($"ApplyAsync(scan, run={firstRunId}) -> Succeeded={runApply.Succeeded} Reason={runApply.Reason}");
            runApply.Succeeded.Should().BeTrue();

            var jobList = await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            var run1Names = jobList.Items.Select(j => j.Metadata.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            _output.WriteLine($"run {firstRunId} Jobs: [{string.Join(", ", run1Names)}]");
            jobList.Items.Should().HaveCount(nodeNames.Count, "exactly one Job per Ready node");
            jobList.Items.Select(j => j.Spec.Template.Spec.NodeName).OrderBy(n => n, StringComparer.Ordinal)
                .Should().Equal(nodeNames, "each Job is PINNED to its node via spec.nodeName, not left to the scheduler");

            (await raw.AppsV1.ListNamespacedDeploymentAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("a scan kind renders no Deployment");
            (await raw.AppsV1.ListNamespacedDaemonSetAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("a scan kind renders no DaemonSet");

            foreach (var job in jobList.Items)
            {
                job.Metadata.Labels[WorkloadResourceLabels.InstanceLabelKey].Should().Be(labelValue);
                job.Metadata.Labels[WorkloadResourceLabels.ScanRunLabelKey].Should().Be(firstRunId,
                    "the (instance, node, run) triple must be recoverable from labels alone");
                job.Metadata.Labels.Should().ContainKey(WorkloadResourceLabels.ScanNodeLabelKey);
                job.Spec.BackoffLimit.Should().Be(0, "a scan never silently re-runs itself");
                job.Spec.Completions.Should().Be(1);
                job.Spec.Template.Spec.RestartPolicy.Should().Be("Never");
                (job.Metadata.ManagedFields ?? new List<k8s.Models.V1ManagedFieldsEntry>())
                    .Select(m => m.Manager).Should().Contain(KubernetesWorkloadProvider.FieldManager);
            }

            var status = await PollStatusAsync(
                provider, reference,
                accept: s => s.ScanRunTerminal,
                timeout: TimeSpan.FromMinutes(3), ct: ct);

            _output.WriteLine($"GetStatus(scan, completed) -> Phase={status.Phase} ProviderReady={status.ProviderReady} Terminal={status.ScanRunTerminal} Failed=[{string.Join(", ", status.ScanFailedNodeNames)}] Msg={status.Message}");
            status.ScanRunTerminal.Should().BeTrue("every Job reached a terminal count");
            status.ScanFailedNodeNames.Should().BeEmpty("hello-world exits 0 on every node");
            status.Phase.Should().Be(WorkloadProviderPhase.Ready,
                "a finished scan reads Ready as 'armed for the next run' — NOT the Deployment sense of a ready pod");
            status.ProviderReady.Should().BeTrue();
            status.Message.Should().Contain($"{nodeNames.Count} node(s) completed, 0 failed");

            var scanPods = await raw.CoreV1.ListNamespacedPodAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            _output.WriteLine($"scan pod phases: [{string.Join(", ", scanPods.Items.Select(p => $"{p.Spec.NodeName}={p.Status.Phase}"))}]");
            scanPods.Items.Should().HaveCount(nodeNames.Count);
            scanPods.Items.Should().OnlyContain(p => p.Status.Phase == "Succeeded",
                "the status above must rest on containers that really ran and exited 0");

            await provider.SuspendAsync(reference, ct);

            var afterTerminalSuspend = await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            _output.WriteLine($"After Suspend(terminal run): spec.suspend=[{string.Join(", ", afterTerminalSuspend.Items.Select(j => j.Spec.Suspend?.ToString() ?? "null"))}]");
            afterTerminalSuspend.Items.Should().HaveCount(nodeNames.Count, "suspend deletes nothing");
            afterTerminalSuspend.Items.Should().OnlyContain(j => j.Spec.Suspend != true,
                "a COMPLETED scan run must not be marked held — there is nothing left to hold");

            var terminalStatus = await provider.GetStatusAsync(reference, ct);
            _output.WriteLine($"GetStatus(after suspend of a finished run) -> Phase={terminalStatus.Phase} Msg={terminalStatus.Message}");
            terminalStatus.Phase.Should().Be(WorkloadProviderPhase.Ready);
            terminalStatus.Message.Should().NotContain("Scan suspended", "nothing is held");

            await provider.ResumeAsync(reference, ct);
            (await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().OnlyContain(j => j.Spec.Suspend != true, "resume over a finished run is a no-op too");

            const string heldRunId = "r2";
            var heldRun = BuildScanSpec(instanceId, inst8, pendingScanRunId: heldRunId, readyNodeNames: nodeNames, image: WorkloadImage);
            (await provider.ApplyAsync(heldRun, null, ct)).Succeeded.Should().BeTrue();

            await WaitUntilAsync(async () =>
                {
                    var jobs = await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct);
                    return jobs.Items.Count == nodeNames.Count && jobs.Items.All(j => (j.Status?.Active ?? 0) >= 1);
                },
                TimeSpan.FromMinutes(2), ct);

            var inFlight = await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            inFlight.Items.Should().HaveCount(nodeNames.Count,
                "a fan-out is one Job PER NODE — fewer means a node was never scanned, so the wait above timed out silently");
            inFlight.Items.Should().OnlyContain(j => (j.Status.Active ?? 0) >= 1,
                "the suspend below is only meaningful while every node's scan is genuinely running");

            var runningStatus = await provider.GetStatusAsync(reference, ct);
            runningStatus.Phase.Should().Be(WorkloadProviderPhase.Running, "an active fan-out reads as Scanning");

            await provider.SuspendAsync(reference, ct);

            await WaitUntilAsync(async () =>
                {
                    var jobs = await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct);
                    return jobs.Items.All(j => j.Spec.Suspend == true && (j.Status?.Active ?? 0) == 0);
                },
                TimeSpan.FromMinutes(2), ct);

            var held = await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            _output.WriteLine($"After Suspend(in-flight): suspend=[{string.Join(", ", held.Items.Select(j => j.Spec.Suspend?.ToString() ?? "null"))}] active=[{string.Join(", ", held.Items.Select(j => (j.Status?.Active ?? 0).ToString()))}] failed=[{string.Join(", ", held.Items.Select(j => (j.Status?.Failed ?? 0).ToString()))}]");
            held.Items.Should().HaveCount(nodeNames.Count,
                "a fan-out is one Job PER NODE — fewer means a node's Job never reached the held state the wait was for");
            held.Items.Should().OnlyContain(j => j.Spec.Suspend == true, "every node's Job carries the hold");
            held.Items.Should().OnlyContain(j => (j.Status.Active ?? 0) == 0, "the API server terminated the scan pods");
            held.Items.Should().OnlyContain(j => (j.Status.Failed ?? 0) == 0,
                "a pod deleted BY the suspension must not count as a failure — otherwise backoffLimit: 0 would be "
                + "consumed and the run could never resume");

            await WaitUntilAsync(async () =>
                    (await raw.CoreV1.ListNamespacedPodAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                    .Items.All(p => p.Status.Phase != "Running"),
                TimeSpan.FromMinutes(2), ct);

            var podsAfterSuspend = await raw.CoreV1.ListNamespacedPodAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            podsAfterSuspend.Items.Should().NotContain(p => p.Status.Phase == "Running",
                "a suspend that never actually terminated the pods must fail here, not slip past on Job counters alone");

            var heldStatus = await provider.GetStatusAsync(reference, ct);
            _output.WriteLine($"GetStatus(held) -> Phase={heldStatus.Phase} Terminal={heldStatus.ScanRunTerminal} Msg={heldStatus.Message}");
            heldStatus.Message.Should().Contain("Scan suspended", "the operator must see WHY the scan stopped");
            heldStatus.ScanRunTerminal.Should().BeFalse("a held run is not a finished run — the marker must survive");

            await provider.ResumeAsync(reference, ct);

            await WaitUntilAsync(async () =>
                {
                    var jobs = await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct);
                    return jobs.Items.All(j => j.Spec.Suspend != true && (j.Status?.Active ?? 0) >= 1);
                },
                TimeSpan.FromMinutes(2), ct);

            var resumed = await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            _output.WriteLine($"After Resume(in-flight): suspend=[{string.Join(", ", resumed.Items.Select(j => j.Spec.Suspend?.ToString() ?? "null"))}] active=[{string.Join(", ", resumed.Items.Select(j => (j.Status?.Active ?? 0).ToString()))}]");
            resumed.Items.Should().HaveCount(nodeNames.Count,
                "a fan-out is one Job PER NODE — fewer means a node's Job never came back after the resume the wait was for");
            resumed.Items.Should().OnlyContain(j => j.Spec.Suspend != true, "the hold is lifted");
            resumed.Items.Should().OnlyContain(j => (j.Status.Active ?? 0) >= 1,
                "the API server created FRESH pods — a resumed scan run genuinely continues");
            resumed.Items.Should().OnlyContain(j => (j.Status.Failed ?? 0) == 0,
                "the resumed run must still have its single allowed attempt intact, or the run could never finish");
            (await provider.GetStatusAsync(reference, ct)).Message.Should().NotContain("Scan suspended");

            const string secondRunId = "r3";
            var run2 = BuildScanSpec(instanceId, inst8, pendingScanRunId: secondRunId, readyNodeNames: nodeNames);
            (await provider.ApplyAsync(run2, null, ct)).Succeeded.Should().BeTrue();

            await WaitUntilAsync(async () =>
                {
                    var jobs = await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct);
                    return jobs.Items.Count == nodeNames.Count
                        && jobs.Items.All(j => j.Metadata.Labels[WorkloadResourceLabels.ScanRunLabelKey] == secondRunId);
                },
                TimeSpan.FromMinutes(1), ct);

            var run2Jobs = await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct);
            var run2Names = run2Jobs.Items.Select(j => j.Metadata.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            _output.WriteLine($"run {secondRunId} Jobs: [{string.Join(", ", run2Names)}]");
            run2Jobs.Items.Should().HaveCount(nodeNames.Count, "the past run's Jobs were swept, not accumulated");
            run2Jobs.Items.Should().OnlyContain(j => j.Metadata.Labels[WorkloadResourceLabels.ScanRunLabelKey] == secondRunId);
            run2Names.Should().NotIntersectWith(run1Names, "the run id rides the Job name, so a new run is new objects");

            await provider.TeardownAsync(reference, retainVolumes: false, ct);

            await WaitUntilAsync(async () =>
                    (await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                    .Items.Count == 0,
                TimeSpan.FromMinutes(1), ct);

            (await raw.BatchV1.ListNamespacedJobAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("teardown reaps the scan Jobs by the same instance label");
            (await raw.CoreV1.ListNamespacedSecretAsync(Namespace, labelSelector: selector, cancellationToken: ct))
                .Items.Should().BeEmpty("teardown deletes the Secret");

            var goneStatus = await provider.GetStatusAsync(reference, ct);
            _output.WriteLine($"GetStatus(after teardown) -> Phase={goneStatus.Phase} Msg={goneStatus.Message}");
            goneStatus.Phase.Should().Be(WorkloadProviderPhase.NotFound);
        }
        finally
        {
            await SafeTeardownAsync(provider, reference);
        }
    }

    private static string Describe(IDictionary<string, string>? map)
        => map is null or { Count: 0 }
            ? "(none)"
            : string.Join(", ", map.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    private static KubernetesWorkloadProvider BuildProvider(string kubeconfigPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KubernetesConnectionResolver.AllowKubeconfigKey] = "true",
                [KubernetesConnectionResolver.KubeconfigPathKey] = kubeconfigPath,
            })
            .Build();

        var scopeFactory = new ServiceCollection()
            .AddSingleton<IWorkloadOrchestratorSettingsService>(_stubSettings)
            .AddSingleton<IWorkloadOrchestratorSecretReader>(_stubSettings)
            .AddSingleton<IWorkloadCredentialPort>(new RecordingCredentialPort())
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        var clientFactory = new KubernetesClientFactory(
            scopeFactory, configuration, NullLogger<KubernetesClientFactory>.Instance);

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
            new KubernetesNamespaceResolver(new InClusterEnvironment(_ => false, ""), configuration),
            NullLogger<KubernetesWorkloadProvider>.Instance);
    }

    private static k8s.Kubernetes RawClient(string kubeconfigPath)
        => new(KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfigPath));

    private static WorkloadDesiredSpec BuildSpec(
        Guid instanceId, string inst8, WorkloadKind kind = WorkloadKind.Service)
    {
        const string pluginTypeId = "com.reeve.e2e";
        const string workloadId = "probe";
        const string specHash = "e2ehash0001";
        const string volumeId = "data";

        var resourceName = DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = pluginTypeId,
                            WorkloadId = workloadId,
                            Inst8 = inst8,
                        });
        var secretName = DefaultWorkloadResourceNamer.Instance.SecretName(new WorkloadNamingSubject
                        {
                            GroupId = pluginTypeId,
                            WorkloadId = workloadId,
                            Inst8 = inst8,
                        });
        var pvcName = DefaultWorkloadResourceNamer.Instance.VolumeName(
                          new WorkloadNamingSubject
                          {
                              GroupId = pluginTypeId,
                              WorkloadId = workloadId,
                              Inst8 = inst8,
                          },
                          volumeId);
        var labels = new Dictionary<string, string>(
            DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
            {
                InstanceId = instanceId,
                InstallationId = "e2e-install",
                SpecHash = specHash,
            }));

        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.NewGuid(),
            GroupId = pluginTypeId,
            WorkloadId = workloadId,
            InstanceId = instanceId,
            Inst8 = inst8,
            Provider = KubernetesProvider.Id,
            ResourceName = resourceName,
            SecretName = secretName,
            Labels = labels,
            Image = WorkloadImage,
            Kind = kind,
            Network = new ResolvedNetwork
            {
                Ports = new List<ResolvedPort> { new() { Name = "api", Port = 8080, Protocol = "tcp" } },
            },
            Volumes = kind == WorkloadKind.PerNode
                ? new List<ResolvedVolume>()
                : new List<ResolvedVolume>
                {
                    new()
                    {
                        Id = volumeId,
                        MountPath = "/data",
                        Size = "16Mi",
                        Criticality = VolumeCriticality.Recoverable,
                        ResourceName = pvcName,
                    },
                },
            Env = new List<ResolvedEnvVar>
            {
                new() { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = secretName, CredentialId = "workload-token" },
            },
            ProvisionedCredentials = [new ProvisionedCredential("workload-token", "REEVE_WORKLOAD_TOKEN")],
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = specHash,
        };
    }

    private static WorkloadDesiredSpec BuildScanSpec(
        Guid instanceId,
        string inst8,
        string? pendingScanRunId,
        IReadOnlyList<string> readyNodeNames,
        string image = ScanWorkloadImage)
    {
        const string pluginTypeId = "com.reeve.e2e";
        const string workloadId = "scan";
        const string specHash = "e2ehash0002";

        var subject = new WorkloadNamingSubject
        {
            GroupId = pluginTypeId,
            WorkloadId = workloadId,
            Inst8 = inst8,
        };
        var secretName = DefaultWorkloadResourceNamer.Instance.SecretName(subject);

        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.NewGuid(),
            GroupId = pluginTypeId,
            WorkloadId = workloadId,
            InstanceId = instanceId,
            Inst8 = inst8,
            Provider = KubernetesProvider.Id,
            ResourceName = DefaultWorkloadResourceNamer.Instance.ObjectName(subject),
            SecretName = secretName,
            Labels = new Dictionary<string, string>(
                DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                {
                    InstanceId = instanceId,
                    InstallationId = "e2e-install",
                    SpecHash = specHash,
                })),
            Image = image,
            Kind = WorkloadKind.ScanPerNode,
            Network = new ResolvedNetwork
            {
                Ports = new List<ResolvedPort> { new() { Name = "api", Port = 8080, Protocol = "tcp" } },
            },
            Volumes = new List<ResolvedVolume>(),
            Env = new List<ResolvedEnvVar>
            {
                new() { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = secretName, CredentialId = "workload-token" },
            },
            ProvisionedCredentials = [new ProvisionedCredential("workload-token", "REEVE_WORKLOAD_TOKEN")],
            Privileges = new ResolvedPrivileges { RunAsRoot = true },
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = specHash,
            PendingScanRunId = pendingScanRunId,
            ScanReadyNodeNames = readyNodeNames,
        };
    }

    private static string LabelSelectorFor(Guid instanceId)
        => $"{WorkloadResourceLabels.InstanceLabelKey}={instanceId:D}";

    private static CancellationToken TestCt(int minutes = 3)
        => new CancellationTokenSource(TimeSpan.FromMinutes(minutes)).Token;

    private async Task<WorkloadStatus> PollStatusAsync(
        IWorkloadProvider provider,
        WorkloadRef reference,
        Func<WorkloadStatus, bool> accept,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        WorkloadStatus last = await provider.GetStatusAsync(reference, ct);
        while (!accept(last) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            last = await provider.GetStatusAsync(reference, ct);
        }

        return last;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
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
                Settings = new WorkloadOrchestratorSettings
                {
                    Provider = KubernetesProvider.Id,
                }
                    .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings
                    {
                        Namespace = Namespace,
                        AllowUnsupportedOverrides = true,
                    }),
                EffectiveProvider = KubernetesProvider.Id,
                AutoDetected = false,
                Providers = Array.Empty<WorkloadProviderInfo>(),
            });


        public Task<WorkloadProviderProbeResult> TestAsync(WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The e2e stub does not probe.");
    }
}
