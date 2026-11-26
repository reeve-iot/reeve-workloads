# Writing a provider

A provider is an ordinary NuGet package and needs no change to this library. From this library it needs only
`Reeve.Workloads.Abstractions` and `Reeve.Workloads.DependencyInjection`.

`tests/Acme.Workloads.Nomad` is a worked example that follows this page.
`tests/Reeve.Workloads.ThirdPartyProvider.Tests` composes it into an engine, where it is registered, configured,
selected and named like a shipped provider. How the shipped providers behave is summarised in [the architecture
overview](architecture.md#providers).

## Identity and traits

A provider is identified by `WorkloadProviderId`, a validated string rather than a closed enum, and describes what
it does through a descriptor. The engine asks the descriptor instead of comparing names:

```csharp
public static class NomadProvider
{
    public static WorkloadProviderId Id { get; } = WorkloadProviderId.Parse("nomad");

    public static WorkloadProviderDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        DisplayName = "Nomad",
        LocationNoun = "Nomad cluster",
        PublishesHostPorts = true,               // port.{name}.hostPort is a real operator parameter
        SupportsCpuRequests = true,              // a CPU reservation distinct from its limit
        SchedulesAcrossNodes = true,             // node eligibility is a real axis
        RequiresRfc1123AttachmentNames = false,
    };
}
```

The other traits default to what most runtimes do:

- `Materializes` is true unless the provider deploys nothing itself, as the Manual provider does.
- `NodeStateRoot` is the directory node-local state is built under.
- `ReadsStatusInBatches` is covered [below](#batched-status-reads).
- `DevicePassthroughRequiresPrivileged` is false. Set it when the runtime can hand a container a host device only by
  running it privileged.
- `SupportsUserNamespaces` is false. Set it only when the provider renders the user-namespace setting that the
  reserved `security.userNamespace` parameter asks for. Without it the parameter is not offered, and a submitted value
  is dropped unless the manifest declares a parameter of that name.

With `DevicePassthroughRequiresPrivileged`, a workload that binds a device requires privileged mode. Its desired spec
carries `ResolvedPrivileges.DevicePassthroughPrivileged` next to the declared `Privileged`. A renderer that needs the
privilege reads `EffectivePrivileged`. One whose runtime grants the device itself, such as a Compose `devices:` entry,
reads `Privileged`. The factory also skips a requested user namespace for such a workload and reports it.

## Registration

A package contributes an extension on `ReeveWorkloadsBuilder`, exactly as the shipped providers do:

```csharp
public static ReeveWorkloadsBuilder AddNomadProvider(this ReeveWorkloadsBuilder builder)
{
    builder.AddSelf<NomadWorkloadProvider>(ServiceLifetime.Singleton);
    builder.AddEnumerable<IWorkloadProviderSettingsCodec, NomadProviderSettingsCodec>(ServiceLifetime.Singleton);
    builder.AddEnumerable<IWorkloadProviderAutoDetect, NomadAutoDetect>(ServiceLifetime.Singleton);
    builder.AddEnumerable<IWorkloadResourceNamer, NomadResourceNamer>(ServiceLifetime.Singleton);
    return builder.Forward<IWorkloadProvider, NomadWorkloadProvider>(ServiceLifetime.Singleton);
}

services.AddReeveWorkloads(configuration, ports => ports /* the host's ports */)
    .AddEngine()
    .AddNomadProvider();
```

A provider adds no host port of its own: `FindMissingHostPorts()` reports the same set with or without it. The
Nomad package declares its extension in the `Reeve.Workloads.DependencyInjection` namespace, as the shipped
providers do, so a host composes with one import. Its own types live in `Acme.Workloads.Nomad` (see
[the architecture overview](architecture.md#contracts)).

The engine relies on three behaviours from every provider:

- An unreachable runtime is an answer, not an exception. Status reads report it. A read that cannot decide whether
  the workload exists answers `Unknown`, which holds the row, rather than `NotFound`.
- `GetAttachmentExistenceAsync` answers which of the requested operator-network attachments exist. It works by name
  from one listing and never reads what an attachment contains. It returns `Read` with the missing names (empty when
  all exist), `Unreadable` when the listing faults or is refused, or `NotApplicable` when the runtime cannot be asked.
  Never report a fault as missing: the engine parks a missing attachment as drifted but retries an unreadable one.
- Calls honour the cancellation token they are given. The engine bounds them with the `Workloads:ProviderTimeouts`
  deadlines.

`InMemoryWorkloadProvider` in `Reeve.Workloads.Testing` shows these behaviours and is the provider the engine's own
parity tests run against.

## Configuration: `IWorkloadProviderSettingsCodec`

Configuration goes in the `providers` map of `WorkloadOrchestratorSettings`, keyed by the provider's id. The library
cannot know the shape of an entry, so it holds each one as a `JsonElement`, and the provider's codec says what the
entry means:

- `Validate` rejects a document's entry with a message that names the field. A `***` still in the entry is invalid.
- `SecretFields` names the secret strings as JSON pointers into the entry, for the host to protect at rest.
- `Mask` replaces them with `***` on the way out. Every provider uses that sentinel.
- `ResolveMasked` replaces a `***` in a submission with the stored secret. It throws
  `WorkloadOrchestratorSettingsValidationException` naming the field when nothing is stored.
- `Placement` names the fields that say where the runtime is. The settings judge refuses to change one while the
  entry is in use.

A token of a third-party provider is therefore named to the host for protection and masked on read, like the registry
credentials the Docker provider holds. A provider with no secrets and nothing to reject needs no codec, and its entry
is stored verbatim. Keys of the entry that your typed record does not own belong to the host. The shipped codecs keep
them with the `ProviderSettingsPassThrough` helper, and yours should too.

## Selection: `IWorkloadProviderAutoDetect`

This answers whether the host could deploy through the provider right now. When the operator has selected none, the
library asks the registered providers in ascending `Priority`. The constants on the interface place a provider:

- `InClusterPriority` for a host already running inside the runtime it targets;
- `LocalRuntimePriority` for a daemon reachable from the machine;
- `FallbackPriority` for a provider that is always available and therefore terminal.

Ties break on the provider id, never on registration order.

## Naming: `IWorkloadResourceNamer`

A namer decides what the provider's objects, secrets and volumes are called and which labels they carry. It also
names the `RetainedVolumeSchemeId` its retained volumes are recorded under. A retained row outlives the code that
wrote it, so a restore years later finds the volume by that name and scheme. Namespace the scheme to the vendor
(`acme.nomad`) so it cannot collide with another package's. The namer is optional: without one the engine runs the
provider under the default `reeve-wl-*` names.

## Optional seams

Each is registered with `AddEnumerable`, keyed by the provider id where it has one. An absent seam reports
`notSupported` and does not fail.

| Seam | What it contributes |
| --- | --- |
| `INetworkAttachmentSource` | Lists the attachments by name (`ListAsync`) and words the missing case (`DescribeMissing`). An attachment is judged by its name and whether it is listed, so a source reads nothing from the network object. |
| `IWorkloadProviderProbe` | Answers the settings page's test: can the runtime be reached with these settings. |
| `IWorkloadHostAddressContributor` | Answers the `host:port` a workload of this provider dials to reach a host service named by a `HostEndpoint`, or null when this runtime cannot say. The host composes its own URL around the address. The last one registered per provider wins. |
| `IWorkloadArtifactRenderer` | Renders a spec into a file under a `WorkloadArtifactFormatId` the renderer declares. `IsSelfContained` says an operator can run the workload from that file alone; the managed-to-manual takeover hands over exactly the self-contained formats. |

## Batched status reads

A reconcile pass reads status for the whole fleet in one go when the provider says it can. Set
`ReadsStatusInBatches = true` and override `GetStatusesAsync(IReadOnlyList<WorkloadRef>, CancellationToken)`, a default
interface method. If both are left alone, the SPI loops the single read. The Kubernetes and Docker providers both read
the fleet in batches.

The answers live in a single-use cache bounded by `Workloads:Reconciler:FleetStatusFreshnessSeconds` (default 15), so
the read a drive makes after applying a spec still reaches the runtime.

## Testing a provider

Reference `Reeve.Workloads.Testing` and `Reeve.Workloads.DeploymentFixtures` from your test project: the first
gives the in-memory provider to compare behaviour against, the second gives manifest builders and
`UpdatePipelineConformance`, which checks the update pipeline a composition produces. Compose your provider the way
`tests/Reeve.Workloads.ThirdPartyProvider.Tests` composes the Nomad one, and assert on `FindMissingHostPorts()` and
the provider registry rather than on internal types.
