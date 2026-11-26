using System;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesPvcTerminatingException : Exception
{
    public KubernetesPvcTerminatingException(string claimName, string @namespace)
        : base($"Persistent volume claim '{claimName}' in '{@namespace}' is still being deleted (Terminating); the apply retries and ensures the claim once the deletion completes.")
    {
        ClaimName = claimName;
    }

    public string ClaimName { get; }
}
