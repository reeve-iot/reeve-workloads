using System;
using System.Collections.Generic;

namespace Reeve.Workloads.Abstractions;

/// <summary>The kind of live object adoption can take over, named independently of any provider client library so
/// the port carries no provider dependency. Ordinals mirror the Kubernetes kinds and are pinned by a parity test.</summary>
public enum AdoptableResourceKind
{
    /// <summary>A replicated workload object (Kubernetes Deployment).</summary>
    Deployment = 0,

    /// <summary>A per-node workload object (Kubernetes DaemonSet).</summary>
    DaemonSet = 1,

    /// <summary>A network endpoint object (Kubernetes Service).</summary>
    Service = 2,

    /// <summary>A secret-material object.</summary>
    Secret = 3,

    /// <summary>A persistent volume claim — the metadata-surgery target, never applied.</summary>
    PersistentVolumeClaim = 4,

    /// <summary>A one-shot task object (Kubernetes Job).</summary>
    Job = 5,
}

/// <summary>The kind of adoption a labelled resource is eligible for; detection keys on labels only. Serialises as its
/// string name, which is drift-proof where a numeric ordinal is not.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AdoptionClass>))]
public enum AdoptionClass
{
    /// <summary>Carries <c>reeve-iot.dev/workload-instance</c> but a FOREIGN <c>reeve-iot.dev/installation</c> id — a previous
    /// installation's retained fabric/Thread/bindkey data after DB loss.</summary>
    ForeignInstallation = 0,

    /// <summary>Operator-deployed artifacts carrying <c>reeve-iot.dev/managed-by: operator</c> plus the labels Reeve
    /// renders — the manual→kubernetes provider-switch path.</summary>
    ManualManaged = 1,
}

/// <summary>One adoptable live object — a client-library-free projection of an object carrying the Reeve instance
/// label. Ownership evidence is verified against the ACTUAL <see cref="Labels"/>/<see cref="Image"/> before any SSA.</summary>
public sealed record AdoptableResource
{
    /// <summary>The object kind (Deployment/DaemonSet/Service/PersistentVolumeClaim).</summary>
    public required AdoptableResourceKind Kind { get; init; }

    /// <summary>True when the object is a PersistentVolumeClaim (the metadata-surgery target).</summary>
    public bool IsPersistentVolumeClaim => Kind == AdoptableResourceKind.PersistentVolumeClaim;

    /// <summary>The object's metadata name.</summary>
    public required string Name { get; init; }

    /// <summary>The object's actual labels (the evidence surface — never a reconstructed name).</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The object's actual annotations (Helm/Argo ownership lives here — the surgery target).</summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The observed container image for a Deployment/DaemonSet's first container; null for a Service/PVC.</summary>
    public string? Image { get; init; }

    /// <summary>The <c>reeve-iot.dev/workload-instance</c> label value (the grouping key); empty when absent.</summary>
    public string InstanceLabel =>
        Labels.TryGetValue(Reeve.Workloads.Abstractions.Domain.WorkloadResourceLabels.InstanceLabelKey, out var v) ? v : string.Empty;

    /// <summary>The <c>reeve-iot.dev/installation</c> label value; null when absent.</summary>
    public string? InstallationLabel =>
        Labels.TryGetValue(Reeve.Workloads.Abstractions.Domain.WorkloadResourceLabels.InstallationLabelKey, out var v) ? v : null;

    /// <summary>True when the object carries <c>reeve-iot.dev/managed-by: operator</c> (the manual→managed marker).</summary>
    public bool IsOperatorManaged =>
        Labels.TryGetValue(WorkloadAdoptionLabels.ManagedByLabelKey, out var v)
        && string.Equals(v, WorkloadAdoptionLabels.ManagedByOperatorValue, StringComparison.Ordinal);
}

/// <summary>Adoption-specific label keys/values not already on
/// <c>WorkloadResourceNaming</c>: the manual→managed marker and the stripped Helm/Argo metadata.</summary>
public static class WorkloadAdoptionLabels
{
    /// <summary>The <c>reeve-iot.dev/managed-by</c> label key — <c>operator</c> marks a manual→managed candidate.</summary>
    public const string ManagedByLabelKey = "reeve-iot.dev/managed-by";

    /// <summary>The <c>reeve-iot.dev/managed-by</c> value for operator-deployed manual artifacts.</summary>
    public const string ManagedByOperatorValue = "operator";

    /// <summary>Helm release-name annotation stripped on adoption.</summary>
    public const string HelmReleaseNameAnnotation = "meta.helm.sh/release-name";

    /// <summary>Helm release-namespace annotation stripped on adoption.</summary>
    public const string HelmReleaseNamespaceAnnotation = "meta.helm.sh/release-namespace";

    /// <summary>The <c>helm.sh/resource-policy</c> annotation stripped on adoption: once Reeve owns the PVC the old
    /// release's retain policy only confuses a later `helm uninstall`.</summary>
    public const string HelmResourcePolicyAnnotation = "helm.sh/resource-policy";

    /// <summary>The value that marks the Kubernetes <c>managed-by</c> label as Helm-owned. The label KEY is the
    /// Kubernetes arm's, declared beside the only code that strips it.</summary>
    public const string HelmManagedByValue = "Helm";

    /// <summary>Argo CD's NON-default (label mode) tracking label. The default mode keys on the Kubernetes
    /// <c>app.kubernetes.io/instance</c> label instead, which is why BOTH are stripped.</summary>
    public const string ArgoInstanceLabel = "argocd.argoproj.io/instance";

    /// <summary>The annotation/label prefix (<c>argocd.argoproj.io/</c>) whose keys are all stripped on adoption.</summary>
    public const string ArgoAnnotationPrefix = "argocd.argoproj.io/";
}
