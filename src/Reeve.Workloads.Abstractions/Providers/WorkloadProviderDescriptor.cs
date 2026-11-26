using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>What a provider IS, declared once at registration, so the reconciler, the spec factory and the
/// settings surface can ask what a provider DOES instead of asking which of three names it has.</summary>
/// <remarks>
/// A third-party provider can be strict about attachment names, or publish host ports, without the
/// library having heard of it. Traits are declarative and cheap to read — no I/O, no settings.
/// <para>
/// The defaults are what the library assumes of any provider it asks about, and a provider this build has no
/// package for is described by them. The shipped providers declare their own descriptors in their packages (<c>KubernetesProvider.Descriptor</c> and its siblings), exactly as a third party does.
/// </para>
/// </remarks>
public sealed record WorkloadProviderDescriptor
{
    /// <summary>The provider's id, unique across the registered set.</summary>
    public required WorkloadProviderId Id { get; init; }

    /// <summary>Operator-facing name for the provider-selection UI. Defaults to the id.</summary>
    public string? DisplayName { get; init; }

    /// <summary>False when the provider creates NOTHING at runtime and the operator applies the rendered
    /// artifacts by hand (that is <c>manual</c>). A non-materialising provider is never reconciled, torn down,
    /// suspended, drift-checked or log-scraped, and its workloads report no runtime status.</summary>
    public bool Materializes { get; init; } = true;

    /// <summary>True when a network attachment name must be an RFC 1123 DNS subdomain. Kubernetes needs it;
    /// so does <c>manual</c>, because one manual install renders Kubernetes, Compose and Helm artifacts alike.</summary>
    public bool RequiresRfc1123AttachmentNames { get; init; } = true;

    /// <summary>True when the provider binds container ports onto the host, which makes
    /// <c>port.{name}.hostPort</c> a real operator parameter. Kubernetes exposes through a Service instead
    /// and leaves the parameter out entirely.</summary>
    public bool PublishesHostPorts { get; init; }

    /// <summary>True when the provider honours a CPU REQUEST as distinct from a limit. Docker has only limits,
    /// so the resources surface marks the request inapplicable rather than accepting a value it would drop.</summary>
    public bool SupportsCpuRequests { get; init; } = true;

    /// <summary>True when the provider places workloads across a FLEET of nodes by selector, which is what makes
    /// node eligibility a real axis. A single-host provider has one node and nothing to select between.</summary>
    public bool SchedulesAcrossNodes { get; init; } = true;

    /// <summary>True when the provider can run a workload in its own user namespace through
    /// <c>security.userNamespace</c>. Default false.</summary>
    /// <remarks>A descriptor copied from <c>KubernetesProvider.Descriptor</c> inherits true; clear it unless the
    /// provider renders the user-namespace field.</remarks>
    public bool SupportsUserNamespaces { get; init; }

    /// <summary>True when the provider can hand a container a host device only by running it privileged. Kubernetes
    /// mounts a device as a <c>hostPath</c>, and the container runtime's device cgroup refuses to open a bind-mounted
    /// device in an unprivileged container, so a pod that binds a device must be privileged to use it. On such a provider
    /// a workload that binds at least one device path requires, and is rendered with, a privileged container, and its
    /// requirements say so. Default false: Docker grants the device itself.</summary>
    public bool DevicePassthroughRequiresPrivileged { get; init; }

    /// <summary>True when <see cref="IWorkloadProvider.GetStatusesAsync"/> answers a whole set in fewer
    /// round-trips than one call each. False keeps the reconciler on the per-instance read: priming a fleet
    /// cache through the SPI's looping default would pay for every read up front and still make N calls.</summary>
    /// <remarks>A provider that starts from a shipped preset (<c>KubernetesProvider.Descriptor with { Id = … }</c>) INHERITS this
    /// as true. Clear it unless you also override the batch read, or the reconciler will front-load reads it
    /// could have made lazily.</remarks>
    public bool ReadsStatusInBatches { get; init; }

    /// <summary>The operator-facing word for where this provider puts things, used in preflight and probe
    /// messages ("reachable from the cluster" / "from the docker host"). Defaults to the display name.</summary>
    public string? LocationNoun { get; init; }

    /// <summary>Root directory for curated node-local state, under which the spec factory builds
    /// <c>{NodeStateRoot}/{pluginTypeId}/{workloadId}</c>.</summary>
    /// <remarks>
    /// A DECLARED trait rather than an operator setting.
    /// The path it builds is written into the desired spec and IS hashed, so a change to it rolls every workload
    /// that declares node-local state. That is not something a settings page should be able to do by accident,
    /// and the spec factory is provider-agnostic, so it does not read one provider's settings for it.
    /// </remarks>
    public string NodeStateRoot { get; init; } = DefaultNodeStateRoot;

    /// <summary>The default for <see cref="NodeStateRoot"/>.</summary>
    public const string DefaultNodeStateRoot = "/var/lib/reeve/workloads";

    /// <summary>The id's canonical string — the form persisted, hashed into the spec, and sent on the wire.</summary>
    public string Key => Id.Value;

    /// <summary>The name to show an operator: <see cref="DisplayName"/> when set, else the id.</summary>
    public string Label => DisplayName ?? Id.Value;

    /// <summary>The noun for operator-facing location text: <see cref="LocationNoun"/> when set, else <see cref="Label"/>.</summary>
    public string Location => LocationNoun ?? Label;
}
