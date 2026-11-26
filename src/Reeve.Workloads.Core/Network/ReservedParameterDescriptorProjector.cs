using System;
using System.Collections.Generic;
using System.Linq;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Domain.Network;

namespace Reeve.Workloads.Core.Network;

/// <summary>The projector for reserved parameters, including provider-conditional names omitted when no provider is known.
/// The names and their types come from the one Domain producer the parser and the resolver use; this class adds only
/// how each is presented (hint, default shown, whether the wizard renders it) and the device index and group-id keys.</summary>
public static class ReservedParameterDescriptorProjector
{
    /// <summary>Reserved names whose site-2 synthesis is provider-conditional. The host-port pattern matches the
    /// parser's reserved-name rule; the user-namespace name is provider-trait-gated.</summary>
    public static bool IsProviderConditional(string name)
        => WorkloadReservedParameters.IsHostPortParameter(name)
            || string.Equals(name, WorkloadParameterResolver.UserNamespaceParameterName, StringComparison.Ordinal);

    /// <summary>Projects every reserved parameter the workload synthesizes. A null provider is a DIFFERENT answer:
    /// conditional names are OMITTED, never projected false, which would be the Kubernetes answer respelled.</summary>
    public static IReadOnlyList<WorkloadReservedParameterDescriptor> Project(
        WorkloadManifest workload,
        WorkloadProviderDescriptor? provider)
    {
        ArgumentNullException.ThrowIfNull(workload);

        var synthesized = provider is null
            ? null
            : WorkloadReservedParameters.For(workload, provider)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
        var descriptors = new List<WorkloadReservedParameterDescriptor>();

        foreach (var (name, type) in WorkloadReservedParameters.For(workload, provider: null))
        {
            if (synthesized is null && IsProviderConditional(name))
            {
                continue;
            }

            descriptors.Add(Describe(workload, name, type, provider, synthesized?.Contains(name) ?? true));
        }

        AddDeviceKeys(workload, descriptors);
        return descriptors;
    }

    private static WorkloadReservedParameterDescriptor Describe(
        WorkloadManifest workload,
        string name,
        WorkloadParameterType type,
        WorkloadProviderDescriptor? provider,
        bool applicable)
    {
        var descriptor = NonRenderable(name, type, WorkloadReservedParameters.SynthesizedDefault(workload, name))
            with { ProviderApplicable = applicable };

        if (string.Equals(name, WorkloadNetworkAttachmentGrammar.NetworkModeParameter, StringComparison.Ordinal))
        {
            return descriptor with
            {
                EnumValues =
                [
                    WorkloadNetworkAttachmentGrammar.ClusterMode,
                    WorkloadNetworkAttachmentGrammar.HostNetworkMode,
                    WorkloadNetworkAttachmentGrammar.OperatorNetworkMode,
                ],
                Hint = "Where this workload's network interface lives. 'cluster' keeps it on Reeve's own network; "
                    + "'hostNetwork' shares the host's network stack; 'operatorNetwork' attaches it directly to a "
                    + "network you supply, which makes it reachable on — and able to reach — that segment.",
                ApplicableModes = ApplicableModesFor(provider),
                Renderable = true,
            };
        }

        if (string.Equals(name, WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter, StringComparison.Ordinal))
        {
            return descriptor with
            {
                Hint = "The network(s) to attach, in order — the first is what the workload's routing prefers. "
                    + $"At most {WorkloadNetworkAttachmentGrammar.MaxAttachments}. Only used in 'operatorNetwork' "
                    + "mode; a value left here in any other mode is dropped rather than rejected.",
                Renderable = true,
            };
        }

        if (string.Equals(name, WorkloadParameterResolver.NodeSelectorParameterName, StringComparison.Ordinal))
        {
            return descriptor with
            {
                Hint = "Which nodes may run this workload, as `label=value` — several separated by commas, e.g. "
                    + "`intel.feature.node.kubernetes.io/gpu=true`. Leave it empty to let the scheduler choose. "
                    + "Pin it when the workload needs hardware that only some nodes have.",
                Renderable = true,
            };
        }

        if (string.Equals(name, WorkloadParameterResolver.UserNamespaceParameterName, StringComparison.Ordinal))
        {
            return descriptor with
            {
                Default = "false",
                Hint = "Run this workload in its own user namespace, so root inside the container is an unprivileged "
                    + "user on the node. Needs Kubernetes 1.33 or newer and nodes whose runtime, kernel and volumes "
                    + "support it; turn it off if the pod does not start. A workload on the host network runs without "
                    + "it, and the preflight says so.",
                Renderable = applicable,
            };
        }

        return descriptor;
    }

    private static void AddDeviceKeys(WorkloadManifest workload, List<WorkloadReservedParameterDescriptor> descriptors)
    {
        foreach (var device in workload.Privileges?.Devices ?? [])
        {
            if (string.IsNullOrEmpty(device.Parameter))
            {
                continue;
            }

            IReadOnlyList<string> deviceKeys = device.MaxCount > 1
                ?
                [
                    .. Enumerable.Range(0, device.MaxCount)
                        .Select(i => WorkloadParameterResolver.DeviceIndexKey(device.Parameter, i)),
                ]
                : [device.Parameter];

            if (device.MaxCount > 1)
            {
                foreach (var key in deviceKeys)
                {
                    descriptors.Add(NonRenderable(key, WorkloadParameterType.DevicePath, defaultValue: null));
                }
            }

            if (device.Group != WorkloadDeviceGroup.Auto)
            {
                continue;
            }

            foreach (var key in deviceKeys)
            {
                descriptors.Add(NonRenderable(
                    WorkloadParameterResolver.DeviceGroupIdKey(key),
                    WorkloadParameterType.Text,
                    defaultValue: null));
            }
        }
    }

    private static IReadOnlyList<string>? ApplicableModesFor(WorkloadProviderDescriptor? provider)
        => provider is null
            ? null
            :
            [
                WorkloadNetworkAttachmentGrammar.ClusterMode,
                WorkloadNetworkAttachmentGrammar.HostNetworkMode,
                WorkloadNetworkAttachmentGrammar.OperatorNetworkMode,
            ];

    private static WorkloadReservedParameterDescriptor NonRenderable(
        string name, WorkloadParameterType type, string? defaultValue) => new()
        {
            Name = name,
            Type = type,
            Default = defaultValue,
            ProviderApplicable = true,
            Renderable = false,
        };
}
