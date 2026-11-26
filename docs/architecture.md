# Architecture

A short overview for a developer deciding whether and how to host the engine, or getting oriented before a
contribution. How to wire a host is in [host setup](operations/host-setup.md), and every setting is in
[configuration](operations/configuration.md).

## What the library does

`Reeve.Workloads.*` is the workload engine of [Reeve](https://github.com/reeve-iot/reeve), shipped as NuGet packages so
that any .NET host can run the same reconciler, providers and deployment manifest. A plugin declares the sidecar
workloads it needs in a `deployment` manifest. The engine turns each declared workload into a running container or
Kubernetes object, keeps it matching its desired spec, moves it between releases through a reversible update
pipeline, and tears it down while keeping its data.

The library owns the mechanism and none of the host's vocabulary. Whether a workload may run, which plugin it belongs
to, how a workload proves it is alive and where credentials are stored are the host's answers, supplied through ports
(interfaces the host implements). The host's API, UI, plugin catalogue and consent decisions are not part of the
library.

```text
host
  └─ composes Reeve.Workloads.* through AddReeveWorkloads(...)
       ├─ Core engine ─── Reeve.Pipeline (update stages)
       ├─ providers ───── Docker daemon / Kubernetes API / an operator applying artifacts by hand
       └─ EF Core stores ─ PostgreSQL (advisory locks for leader election)
```

## Packages

| Package | Responsibility |
| --- | --- |
| `Reeve.Workloads.Abstractions` | The vocabulary: entities, the deployment manifest contract, wire contracts, the ports a host implements and the provider interface. |
| `Reeve.Workloads.DependencyInjection` | `AddReeveWorkloads`, the builder every package registers through, and the host-port audit. |
| `Reeve.Workloads.Domain` | Pure rules: desired-spec resolution, parameters, privilege requirements, deployment diffs, resource naming. |
| `Reeve.Workloads.Manifest` | The deployment-manifest parser and the path policy. |
| `Reeve.Workloads.Core` | The engine: reconciliation, health gating, the update pipeline, operator actions and the runner that elects a leader. |
| `Reeve.Workloads.Providers.Docker`, `.Kubernetes`, `.Manual` | One provider each. |
| `Reeve.Workloads.EntityFrameworkCore` | The `DbContext`, repositories and library-owned migrations. |
| `Reeve.Workloads.EntityFrameworkCore.PostgreSql` | PostgreSQL conventions and migrations, and the advisory-lock leader election. |
| `Reeve.Workloads.DeploymentFixtures`, `Reeve.Workloads.Testing` | Manifest builders, an update-pipeline conformance check and an in-memory provider for a host's tests. |

Dependencies point inward. Abstractions references only `Reeve.Pipeline` (the stage engine behind updates), because the
stage interface is on its public surface. Domain and Manifest build on Abstractions. Core references Abstractions,
Domain, Manifest and DependencyInjection. The three providers and the EF Core package reference Core. The PostgreSQL
package builds on the EF Core package. A host references only the providers it ships.

## The host and the library

A host composes the engine in a few calls and then starts it:

```csharp
services.AddDataProtection();

services.AddReeveWorkloads(configuration, ports => ports
        .Use<IWorkloadGroupManifestSource, MyManifestSource>(ServiceLifetime.Scoped)
        .Use<IWorkloadOrchestratorSettingsSource, MySettingsSource>(ServiceLifetime.Scoped))
    .AddEngine()
    .AddEntityFrameworkStores(o => o.UseNpgsql(connectionString))
    .AddPostgreSqlLeaderElection()
    .AddDockerProvider()
    .ValidateHostPorts();

await app.Services.RunReeveWorkloadsAsync();
```

The host must implement a small set of ports that have no default: the manifest source, the orchestrator settings
source, ASP.NET Core Data Protection and, for the Manual provider, a liveness port. `ValidateHostPorts` fails the boot
when one is missing. Everything else has a library default, and `FindHostPortsOnLibraryDefaults` shows which defaults
a host still stands on. Optional seams let a host report health and rollout evidence, issue credentials, add bootstrap
values to workload environments, record audit entries, add its own update stages and share a cluster bus between
replicas. See [host setup](operations/host-setup.md) for the list.

The library registers its own hosting. `RunReeveWorkloadsAsync` migrates the `workloads` schema (unless told not to)
and starts a runner. A host that composes the engine and never calls it is stopped by a start guard.

The library does not judge consent. It runs what the host installed, holds what the host withdrew, and refuses a write
whose privilege requirements changed since the host looked at them. The host reads the requirements, shows them to its
operator and passes back the fingerprint it read.

## The deployment manifest and the desired spec

A plugin's `deployment` manifest is a closed, versioned schema: workloads (image, ports, volumes, devices, health,
resources), parameters the operator can set, and the privileges the workload asks for. The parser rejects anything it
does not know and validates every string that ends up in a generated artifact. Product images can be named by a
`component` slug that the host maps to a concrete image through `Workloads:ProductImages`. Host-specific content goes
into namespaced `extensions`, which the library carries verbatim.

The engine never deploys a manifest directly. It first resolves a provider-neutral desired spec from the manifest
version the module recorded, the operator's parameter values and the bootstrap values the host supplies. Resolution
decides the network mode, device mounts, volume sizes, environment and secrets, and refuses a spec that cannot work
(a missing image configuration, a bad quantity, an unresolved secret reference).

The spec hash is a SHA-256 over the parts of the spec that change what runs. It is the drift signal: the hash of the
last successful apply is stored on the row, and a different desired hash means the workload must be applied again. A
failed apply leaves the stored hash alone. Labels, the storage-class default, the restart nonce, the deploying app
version and environment entries a host marks as unhashed are left out, so ordinary operations do not look like drift.
What the hash covers is a compatibility contract: changing it would re-apply every workload in the fleet.

## Reconciliation

Each workload instance is a database row with a lifecycle state (for example `Deploying`, `Healthy`, `Degraded`,
`Failed`, `PendingConfiguration`, `ExternallyManaged`, `Terminating`). The reconciler converges a row to its desired
spec. A pass runs on a periodic schedule (`StatusResyncSeconds`), when the provider's watch reports a change and when
a write nudges the leader. A queue deduplicates triggers, backs off failing instances with jitter, and by default lets
one instance settle after an apply at a time, so a fleet-wide change rolls instead of restarting everything.

A pass resolves the desired spec, applies it when it differs from the last applied hash, reads the runtime status and
then waits for the instance to become healthy within a bound. An instance can wait in `PendingConfiguration` for
required parameters, hold while the provider is unreachable, and is reported `Degraded` when a deploy is not ready after
`DeployStalledAfterSeconds`. Reads that fail never count as "nothing there": the engine holds, refuses or reports, and
never applies or tears down on missing evidence.

Health is the provider's readiness, optionally combined with evidence from the host (a health contributor) and, during
an update, a rollout certifier. The Repairs scanner turns stuck, crash-looping and parked workloads into issues for the
host to show.

## Updates

An update moves all workloads of one plugin instance from one release to another as a pipeline of reversible stages,
persisted as an update intent so that any replica can continue it after a failover. The stages are: parameters
(verify and snapshot the current values), sidecar (apply each new spec beside the running one), operator redeploy
(Manual provider only; waits until the operator has deployed the new artifacts), health gate, commit version and
commit parameters. A host can add its own stages after commit version and before commit parameters, for example to
reload a plugin.

The health gate certifies the hash the provider actually applied. It waits `HealthGateTimeout` for the new workloads to
become healthy (image pulls and spans where the provider or host evidence is unavailable do not count) and gives up at
`HealthGateMaximumWait`. If a stage fails or the gate times out, the stages unwind in reverse: the workloads the update
added are removed, the previous specs are re-applied and the old version must come back healthy within
`RollbackTimeout`. A rollback that cannot complete is left for a human as `RollbackFailed`.

Commit parameters cannot be undone. Once it is entered the walk only goes forward, an abandon is refused, and the
workloads the new release drops are torn down with their data kept. Before that point an operator can abandon an
update, including one parked on a Manual workload.

## Operator actions

The host calls the engine's services for the operator's commands. Install records the module and creates one row per
workload; a new row waits in `PendingConfiguration` until its required values resolve. Parameter edits, resource
limits and volume resizes change the desired spec, and restart and redeploy re-apply it, all through the reconciler.
Restoring an install can reattach a retained volume of the same plugin.

Uninstall marks each row `Terminating` and the leader finishes it: credentials are revoked, the runtime is torn
down, retained volumes are recorded and only then is the row deleted. A failing step keeps the row `Terminating` and is
retried. A volume retained at uninstall is kept until an operator purges it; the engine never deletes one on its own,
and only raises an informational alert for old ones. If a teardown can never complete, an operator can forget the row,
which deletes it without calling the provider and reports what was left behind.

A workload can be handed to the operator and back (a switch to manual and back), and the host can hold a workload,
which revokes its credential and stops its runtime.

## Persistence

The engine keeps its state in its own PostgreSQL schema, `workloads`, with its own migration history table. The
library owns the migrations: `RunReeveWorkloadsAsync` applies them, or a host generates an idempotent script and
applies it itself. Typed documents (parameters, specs, update intents) are stored as `jsonb`. Races between replicas
are decided by a version token on the instance rows and by unique indexes, for example one active update intent per
instance, not by application checks. Schema changes are expand/contract, so two adjacent releases can run side by side
during a rolling restart. See [database and upgrades](operations/database-and-upgrades.md).

Workload parameters are stored in the instance row. Password parameters are encrypted with ASP.NET Core Data
Protection (stored with an `enc:v1:` prefix), or are opaque `ref:` references that the host resolves. Only
desired-spec construction decrypts or resolves values.

## Leader election and replicas

The engine's background units run on one replica at a time: the reconciler, the provider watch, the update walk, the
retained-volume scan and the Repairs scanner. A runner contends for leadership, starts the units in order while it
leads and stops them in reverse order when leadership is lost or the host shuts down. Only then does it resign.

The default election always wins, which is correct for one replica only. `AddPostgreSqlLeaderElection` uses a
PostgreSQL session advisory lock, so a replica that dies releases it with its session. The connection must be pinned to
one database session, and failover after a lost node depends on the server's TCP keepalive settings; see
[configuration](operations/configuration.md#running-more-than-one-replica).

Every replica serves reads and accepts operator commands. A write nudges the leader through the host's cluster bus and
directly in the local process. The reconcile queue deduplicates the nudges. Every provider call has a deadline and a
metric.

## Providers

A provider turns a desired spec into a runtime and reports its status. Providers are ordinary packages that register
`IWorkloadProvider`, so a third party can ship one ([writing a provider](writing-a-provider.md)). A workload is bound to
one provider, and the settings choose where new installs land.

- **Docker** runs workloads as containers on one daemon, so it belongs to a single host replica. It attaches
  workloads to one network, mounts persistent storage as named volumes, hands volumes to `privileges.fsGroup` with a
  short-lived helper container, and attaches operator networks in declared order. It cannot honour per-node workloads,
  operator ConfigMap mounts, `unsupportedPodPatch` or HTTP health checks, and rejects them. Registry credentials are
  never sent to a loopback registry.
- **Kubernetes** applies Secrets, a Deployment, DaemonSet or scan Jobs and a Service with server-side apply, creates
  PersistentVolumeClaims once and only grows them, and watches them by label. Every container gets a hardened security
  context. It connects in-cluster or through a self-contained remote kubeconfig, and it can adopt existing objects
  after disaster recovery.
  Volumes can grow but never shrink, and a workload that binds a device runs privileged.
- **Manual** creates nothing. The operator deploys from rendered artifacts (Compose, Kubernetes manifests, Helm values),
  and the host's liveness port says whether anything is running. Nothing live reads as `Pending`, never as failed or
  absent, and an update parks until the operator redeploys.

## Security model

- Privileges are declared in the manifest, resolved for the chosen provider, and compared with what the operator
  accepted. A change that widens them (a new device, host network, a patch body, more privileged mode) escalates and
  needs a new decision. Consent itself belongs to the host; the library only refuses a write whose requirements moved.
- The manifest parser is a closed grammar. Host paths must stay under `/sys`, `/proc`, `/run` or `/var/run`, or at or
  under `/dev/serial` or `/dev/dri`. Device paths must be under `/dev`. Neither may name a container-runtime socket or
  escape with `..`. Image references must be pinned by digest unless the settings allow otherwise.
- Password parameters are encrypted at rest, secrets reach workloads as Secrets or per-instance material and never as
  literals in the spec or its hash, and credentials are never logged. A host that issues workload credentials has
  them revoked before teardown, on hold and on uninstall.
- Kubernetes workloads get a hardened baseline, and a raw pod patch is allowed only when the settings enable it, then
  re-validated against that baseline. A remote kubeconfig that skips TLS verification, relies on files or runs an
  external credential process is refused.
- Where evidence is missing the engine fails closed: an unreadable document or cluster is never read as "nothing
  there".

## Contracts

`contracts/wire-shape.json` records the JSON shape of every wire contract and `contracts/api-surface.txt` the public
.NET surface of every package. Both are generated and checked locally before a change that touches them; see
[CONTRIBUTING](../CONTRIBUTING.md#local-checks). Namespaces name the package that ships them, except
`Reeve.Workloads.DependencyInjection`, which every package contributes its registration method to.
