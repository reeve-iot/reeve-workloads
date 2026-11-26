namespace Reeve.Workloads.Core.Rendering;

/// <summary>The one producer of the operator note the manual-provider artifact renderers append for an
/// operatorNetwork workload, shared so the disclosure can't drift between two hand-written copies.</summary>
public static class ManualArtifactNetworkNotes
{
    /// <summary>The operator disclosure for a resolved attachment set: names the networks, says where the workload is
    /// reachable, and states that Reeve cannot verify an out-of-band network.</summary>
    public static string Disclosure(IReadOnlyList<string> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        return
            "OPERATOR NETWORK: this workload is attached to the operator-supplied network(s): "
            + string.Join(", ", attachments) + ". It is reachable on, and can reach, that network segment "
            + "directly, and traffic on it is not seen by the platform's filtering. "
            + "Reeve cannot verify an out-of-band network: nothing is enumerated on the manual provider, so Reeve "
            + "neither creates, inspects nor manages these network(s) "
            + "— you own their configuration, their addressing and their reachability.";
    }
}
