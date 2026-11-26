using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.Network;

namespace Reeve.Workloads.Domain.DesiredSpec;

/// <summary>The one producer of the reserved parameter names a workload synthesizes, with each name's fixed type. The
/// resolver synthesizes exactly these for its provider; the parser reserves them for every provider, so a manifest
/// cannot declare one with another type. Device index and group-id keys are reserved by their '[' and ':' instead.</summary>
internal static class WorkloadReservedParameters
{
    /// <summary>The reserved names of <paramref name="workload"/> and their types, in synthesis order. A null
    /// <paramref name="provider"/> includes the names only some providers synthesize.</summary>
    internal static IReadOnlyList<KeyValuePair<string, WorkloadParameterType>> For(
        WorkloadManifest workload, WorkloadProviderDescriptor? provider)
    {
        ArgumentNullException.ThrowIfNull(workload);
        var names = new List<KeyValuePair<string, WorkloadParameterType>>();

        void Add(string name, WorkloadParameterType type) => names.Add(new(name, type));

        if (workload.Privileges?.HostDbus == true)
            Add(WorkloadPrivilegeRequirements.DbusHostPathParameter, WorkloadParameterType.Text);

        foreach (var name in WorkloadParameterResolver.ResourceOverrideParameterNames)
            Add(name, WorkloadParameterType.Text);

        Add(WorkloadParameterResolver.NodeSelectorParameterName, WorkloadParameterType.NodeSelector);

        if (provider is null || provider.SupportsUserNamespaces)
            Add(WorkloadParameterResolver.UserNamespaceParameterName, WorkloadParameterType.Boolean);

        if (workload.Network is { } network)
        {
            if (provider is null || provider.PublishesHostPorts)
            {
                foreach (var port in network.Ports)
                {
                    if (!string.IsNullOrEmpty(port.Name))
                        Add(HostPortParameter(port.Name), WorkloadParameterType.Integer);
                }
            }

            if (network.HostNetwork || network.Attachments.Count > 0)
            {
                Add(WorkloadNetworkAttachmentGrammar.NetworkModeParameter, WorkloadParameterType.Enum);
                Add(WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter, WorkloadParameterType.NetworkAttachment);
            }
        }

        foreach (var volume in workload.Volumes)
        {
            if (string.IsNullOrEmpty(volume.Id))
                continue;
            Add(ExistingClaimParameter(volume.Id), WorkloadParameterType.Text);
            Add($"{WorkloadDesiredSpecFactory.VolumeSizeOverrideParameterPrefix}{volume.Id}", WorkloadParameterType.StorageSize);
        }

        return names;
    }

    /// <summary>The value a reserved name resolves to when neither the author nor the operator sets it.</summary>
    internal static string? SynthesizedDefault(WorkloadManifest workload, string name)
    {
        if (string.Equals(name, WorkloadPrivilegeRequirements.DbusHostPathParameter, StringComparison.Ordinal))
            return WorkloadPrivilegeRequirements.DefaultDbusHostPath;
        if (string.Equals(name, WorkloadNetworkAttachmentGrammar.NetworkModeParameter, StringComparison.Ordinal))
            return workload.Network?.HostNetwork == true
                ? WorkloadNetworkAttachmentGrammar.HostNetworkMode
                : WorkloadNetworkAttachmentGrammar.ClusterMode;
        return null;
    }

    internal static string HostPortParameter(string portName) => $"port.{portName}.hostPort";

    internal static string ExistingClaimParameter(string volumeId) => $"existingClaim.{volumeId}";

    internal static bool IsHostPortParameter(string name)
        => name.StartsWith("port.", StringComparison.Ordinal) && name.EndsWith(".hostPort", StringComparison.Ordinal);
}
