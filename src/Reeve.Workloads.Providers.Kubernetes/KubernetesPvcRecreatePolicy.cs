using System;
using k8s.Models;

namespace Reeve.Workloads.Providers.Kubernetes;

internal enum KubernetesPvcAction
{
    Keep = 0,

    Recreate = 1,
}

internal static class KubernetesPvcRecreatePolicy
{
    private const string PendingPhase = "Pending";

    public static KubernetesPvcAction Decide(V1PersistentVolumeClaim desired, V1PersistentVolumeClaim existing)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);

        if (!IsUnbound(existing))
        {
            return KubernetesPvcAction.Keep;
        }

        var desiredClass = desired.Spec?.StorageClassName;
        if (string.IsNullOrEmpty(desiredClass))
        {
            return KubernetesPvcAction.Keep;
        }

        return string.Equals(desiredClass, existing.Spec?.StorageClassName, StringComparison.Ordinal)
            ? KubernetesPvcAction.Keep
            : KubernetesPvcAction.Recreate;
    }

    public static bool IsUnbound(V1PersistentVolumeClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return string.Equals(claim.Status?.Phase, PendingPhase, StringComparison.Ordinal)
            && string.IsNullOrEmpty(claim.Spec?.VolumeName);
    }
}
