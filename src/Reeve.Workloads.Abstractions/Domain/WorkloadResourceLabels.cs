namespace Reeve.Workloads.Abstractions.Domain;

/// <summary>The label keys every provider stamps and every adopter reads. Vocabulary, not a rule: what builds
/// names and label sets from them is <c>WorkloadResourceNaming</c> in Reeve.Workloads.Domain.</summary>
public static class WorkloadResourceLabels
{
    /// <summary>k8s instance-identity label key; value = the instance GUID. Adoption/drift/teardown key on THIS.</summary>
    public const string InstanceLabelKey = "reeve-iot.dev/workload-instance";

    /// <summary>k8s criticality label key — the backup-selector hook on PVCs.</summary>
    public const string CriticalityLabelKey = "reeve-iot.dev/criticality";

    /// <summary>k8s installation-id label key — co-tenancy hygiene, not an adversarial boundary.</summary>
    public const string InstallationLabelKey = "reeve-iot.dev/installation";

    /// <summary>k8s spec-hash label key — the drift/rollout discriminator.</summary>
    public const string SpecHashLabelKey = "reeve-iot.dev/spec-hash";

    /// <summary>k8s per-node label key for a scan Job; value = the DNS-safe node discriminator, so the node a scan
    /// Job is pinned to is recoverable from labels alone. Downstream tasks key on THIS const, never a literal.</summary>
    public const string ScanNodeLabelKey = "reeve-iot.dev/scan-node";

    /// <summary>k8s per-run label key for a scan Job; value = the DNS-safe run-id. Two runs of the same
    /// (instance, node) carry DIFFERENT values, so a retained terminal Job never cross-run 409-collides.</summary>
    public const string ScanRunLabelKey = "reeve-iot.dev/scan-run";
}
