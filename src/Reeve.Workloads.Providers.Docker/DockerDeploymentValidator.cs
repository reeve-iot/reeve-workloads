using System.Collections.Generic;
using System.Linq;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerDeploymentValidator : IDockerDeploymentValidator
{
    public const string PerNodeRejectionId = "docker-rejects-per-node";

    public const string ScanPerNodeRejectionId = "docker-rejects-scan-per-node";

    public const string UnsupportedPodPatchRejectionId = "docker-rejects-unsupported-pod-patch";

    public const string OperatorConfigMapRejectionId = "docker-rejects-operator-configmap";

    public const string HttpHealthRejectionId = "docker-rejects-http-health";

    public IReadOnlyList<PreflightCheck> Validate(WorkloadDesiredSpec spec)
    {
        System.ArgumentNullException.ThrowIfNull(spec);

        var checks = new List<PreflightCheck>(capacity: 5);

        if (spec.Kind == WorkloadKind.PerNode)
        {
            checks.Add(PreflightCheck.Fail(
                PerNodeRejectionId,
                PreflightSeverity.Error,
                "This workload is 'per-node' (a DaemonSet across every matching node), which the single-host docker "
                    + "provider cannot materialize. per-node scheduling requires the kubernetes provider.",
                fixSnippet: "Set the workload kind to 'service' or 'single-node', or deploy it on the kubernetes provider."));
        }

        if (spec.Kind == WorkloadKind.ScanPerNode)
        {
            checks.Add(PreflightCheck.Fail(
                ScanPerNodeRejectionId,
                PreflightSeverity.Error,
                "This workload is 'scan-per-node' (a run-once Job scheduled onto every matching node), which the "
                    + "single-host docker provider cannot materialize. scan-per-node scheduling requires the kubernetes provider.",
                fixSnippet: "Set the workload kind to 'service' or 'single-node', or deploy it on the kubernetes provider."));
        }

        if (spec.UnsupportedPodPatch is not null)
        {
            checks.Add(PreflightCheck.Fail(
                UnsupportedPodPatchRejectionId,
                PreflightSeverity.Error,
                "This workload declares an 'unsupportedPodPatch' (a Kubernetes-only RFC 6902 patch against the rendered "
                    + "pod spec). The docker provider renders a container, not a pod, so the patch cannot be applied.",
                fixSnippet: "Remove 'unsupportedPodPatch' from the manifest, or deploy this workload on the kubernetes provider."));
        }

        var operatorMounts = spec.ConfigMapMounts
            .Where(m => !string.IsNullOrWhiteSpace(m.ConfigMapName))
            .ToList();
        if (operatorMounts.Count > 0)
        {
            var names = string.Join(", ", operatorMounts.Select(m => $"'{m.ConfigMapName}' at {m.MountPath}"));
            checks.Add(PreflightCheck.Fail(
                OperatorConfigMapRejectionId,
                PreflightSeverity.Error,
                $"This workload mounts an operator-provided ConfigMap ({names}), which has no docker equivalent — docker "
                    + "has no ConfigMap object to project into the container.",
                fixSnippet: "Clear the configMapMount parameter(s), or deploy this workload on the kubernetes provider."));
        }

        if (spec.Health.Mode == WorkloadHealthMode.Http)
        {
            checks.Add(PreflightCheck.Fail(
                HttpHealthRejectionId,
                PreflightSeverity.Error,
                "This workload uses 'http' health, but the docker provider cannot drive an http readiness/liveness probe. "
                    + "Use 'container' health (provider-ready), or a health mode your host observes, under docker.",
                fixSnippet: "Set health.mode to 'container' or to a mode your host observes, or deploy this workload on the kubernetes provider."));
        }

        return checks;
    }
}
