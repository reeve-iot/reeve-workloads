# Host setup

How a host composes the engine, what it has to supply, and how the engine starts. The ports and defaults are listed
below, and [the architecture overview](../architecture.md) shows how the pieces fit.

## Composing the engine

Each package registers its own services, and a host names no class of the library's:

```csharp
using Reeve.Workloads.DependencyInjection;

services.AddDataProtection();   // keys every replica can read; see configuration.md

services.AddReeveWorkloads(configuration, ports => ports
        .Use<IWorkloadGroupManifestSource, MyManifestSource>(ServiceLifetime.Scoped)
        .Use<IWorkloadOrchestratorSettingsSource, MySettingsSource>(ServiceLifetime.Scoped)
        .Use<IWorkloadExternalLiveness, MyLiveness>(ServiceLifetime.Scoped))   // only with AddManualProvider
    .AddEngine()                                                   // Reeve.Workloads.Core
    .AddEntityFrameworkStores(o => o.UseNpgsql(connectionString))  // Reeve.Workloads.EntityFrameworkCore
    .AddPostgreSqlLeaderElection()                                 // Reeve.Workloads.EntityFrameworkCore.PostgreSql
    .AddDockerProvider()                                           // Reeve.Workloads.Providers.Docker
    .AddKubernetesProvider()                                       // Reeve.Workloads.Providers.Kubernetes
    .AddManualProvider()                                           // Reeve.Workloads.Providers.Manual
    .ValidateHostPorts();

var app = builder.Build();
await app.Services.RunReeveWorkloadsAsync();   // Reeve.Workloads.Core
await app.RunAsync();
```

- `AddReeveWorkloads` registers the library's defaults first, then runs the `ports` callback, so anything the host
  supplies wins. It also takes the configuration the engine's options sections are bound from (see
  [configuration](configuration.md#options-sections)).
- `AddEngine` registers Core, binds its options, and registers the engine's own hosting: the single-replica
  election, the runner and the start guard. It also registers the services that read deployment manifests through
  `IWorkloadGroupManifestSource` (`IWorkloadDeploymentContextSource`, `IWorkloadReconcileInputsProvider` and
  `IWorkloadUpdatePlanner`). A host that registers its own implementation of one of them replaces the library's.
- `AddEntityFrameworkStores(configure)` takes the provider and the connection, and nothing else. The library
  registers `WorkloadsDbContext` itself and owns its migrations, history table and schema. A migrations assembly
  set inside `configure` is overwritten, and the call refuses a collection that already registers the context (see
  [the schema guard](database-and-upgrades.md#the-schema-guard)).
- `AddPostgreSqlLeaderElection` replaces the single-replica election with a PostgreSQL advisory lock. A host
  running more than one replica cannot leave it out (see
  [running more than one replica](configuration.md#running-more-than-one-replica)). Referencing the PostgreSQL
  package gives the model conventions and the migrations without a call. It does not give the election, because a
  registration runs before any context can report its provider.
- Reference only the provider packages you ship. The provider registry indexes whichever are present and reports
  `notSupported` for the rest rather than failing. The set is open: a provider of your own registers the same way
  (see [writing a provider](../writing-a-provider.md)).
- `ValidateHostPorts` names every port nothing answers and refuses the boot, so a half-wired engine fails at
  startup instead of degrading later.

The `minimal-host` sample in `samples/minimal-host` is a host of exactly this shape that runs with no server and no
daemon. Its [README](../../samples/minimal-host/README.md) shows what to expect on the console.

## What a host supplies

The library owns the mechanism and none of the host's vocabulary. A host implements the ports in
`Reeve.Workloads.Abstractions`, and the engine does the rest.

These ports are obligatory and have no default:

| Port | What the host owns |
| --- | --- |
| `IDataProtectionProvider` | The keys the library protects password parameters with. Use `AddDataProtection()` with a key ring every replica reads. |
| `IWorkloadGroupManifestSource` | Where a module's deployment manifests live: the highest available, the bundled one, and the one at a given version. |
| `IWorkloadOrchestratorSettingsSource` | The orchestrator settings document, in plaintext. The host loads it, stores it and protects its secrets. A host with nothing to supply registers `InMemoryWorkloadOrchestratorSettingsSource` (from `Reeve.Workloads.Testing`) or its own fixed document. After the document changes, the host calls `IWorkloadOrchestratorSettingsChanges.ChangedAsync` on every replica. |
| `IWorkloadExternalLiveness` | Needed only once you call `AddManualProvider`. Reports whether anything is live for an operator-deployed instance, by the host's own evidence. |

Everything else has a default, and a default is a decision the library made on the host's behalf. Most defaults are
an absence: no audit sink, no bus beyond this process, no device groups, no credentials. The rest are the library's
own implementation of something every host needs, such as the schema migrator, the installation identity and the
clock. Two defaults are correct on one replica and wrong on several: `IWorkloadLockProvider` (in-process) and
`IWorkloadLeaderElection` (`SingleReplicaLeaderElection`).

Three audit calls show where a host stands:

- `FindMissingHostPorts()` returns the ports nothing answers, without throwing.
- `FindHostPortsOnLibraryDefaults()` names the ports standing on a library default. Log it at boot at
  `Information`, in neutral words. A host that knows it runs more than one replica warns for the two ports above
  and for nothing else.
- `FindSuppliedPortsTheEngineDoesNotAskFor()` names the ports the host supplied through the `AddReeveWorkloads`
  callback that no engine type asks for. A port can also go unasked for because the provider package that wants it
  is not in the composition, so this is reported, not thrown.

The audit reads constructors and generic `GetService<T>` and `GetRequiredService<T>` calls in method bodies. It
cannot see a non-generic `GetService(Type)` or a type resolved through a variable, and it must run from an
untrimmed build.

Password parameters are stored encrypted with the Data Protection keys (`enc:v1:` prefix), so a host's own export of
workload parameters must decrypt them or refuse to export. A stored password value that is neither ciphertext nor a
`ref:` reference is treated as undecryptable.
A host that lets operators use `ref:` references for password parameters registers
`IWorkloadParameterReferenceResolver`. Without one, references fail closed.

An unpinned reference whose resolved value changes holds its workload in `PendingConfiguration` until an operator
approves it. Offer that approval through `IWorkloadReferenceRolloutService.ApproveCurrentAsync`
(`GetPendingAsync` lists what waits).

A consumer that trims with `TrimMode=partial` and binds the contracts of `Reeve.Workloads.Abstractions`, `.Domain`,
`.Manifest`, `.DeploymentFixtures` or `.Testing` by reflection must root those assemblies with `TrimmerRootAssembly`,
or members that only reflection reaches are lost. A NativeAOT consumer serializes the contracts through its own
`JsonSerializerContext`, set on a copy of `WorkloadJsonDefaults.CanonicalWire`.

### Optional seams a host should know about

- `IWorkloadCredentialPort` issues and revokes the credentials a workload carries. The default issues nothing, so a
  credential the workload needs is written empty. A host that issues credentials mints them in
  `ResolveOrMintAsync` and revokes them in `RevokeAllAsync`. The engine calls the latter on hold, uninstall and
  teardown, and a teardown does not touch the runtime until the revoke succeeds. Return high-entropy material: a
  digest of the values is stored as a label or annotation on the container or pod.
- `IWorkloadHostBootstrapSource` supplies the values a manifest reads with `bootstrap:<key>` (the keys must be declared
  by a registered `IWorkloadExtensionValidator`) and env entries forced over a manifest's own. It must answer the same
  request the same way every time, apart from secret material. A value marked unhashed reaches a workload only with
  its next apply and never counts as drift.
- `IWorkloadHealthContributor` and `IRolloutCertifier` add the host's own evidence to health and to the update health
  gate. Register at most one certifier, and no two contributors for the same health mode. Without them, readiness
  comes from the provider.
- `IObservedWorkloadVersionReader` reports the version a running workload says it has. Its default reports none, so an
  update of a Manual workload waits until the operator abandons it unless the host registers a reader.
- A host stage in the update pipeline implements `IStage<WorkloadUpdateStageContext>` and is placed after
  `WorkloadUpdateStages.CommitVersion` and before `CommitParams`. The engine refuses to start otherwise.
- `IWorkloadLockProvider` and `IWorkloadClusterBus` default to in-process, which is correct for one replica (see
  [configuration](configuration.md#other-defaults-that-matter-on-several-replicas)).

## What the host decides

The engine does not judge consent. It runs what the host installed, holds what the host withdrew, and refuses a write
whose requirements moved. A host that skips the steps below deploys beyond what its operator accepted.

- Before saving changed orchestrator settings, call `IWorkloadOrchestratorSettingsJudge.JudgeAsync` and refuse the
  save when it returns a violation (`provider_switch_blocked`, `provider_in_use`, `target_not_configured`). Validate
  and mask the document with `IWorkloadOrchestratorSettingsRules`.
- Before an install, a parameter edit or an update, read the requirements from `IWorkloadRequirementsService`
  (`ForInstallAsync`, `ForParameterEditAsync`, `ForUpdateAsync`), show them to the operator, and pass the
  `RequirementsFingerprint` or `WorkloadWritePrecondition` back on `MaterializeAsync`,
  `UpdateParametersAsync` or `InitiateAsync`. The write is refused when the requirements changed in between.
  Whether an update may start unattended, and whether two sides of an update are compatible, are the host's rules.
- A desired spec can widen without a host call: a bundled manifest changes when the host is upgraded, or a settings
  change moves a workload to a provider that needs more (a device workload is privileged on Kubernetes and Manual,
  not on Docker). Read the requirements of the affected instances before the next reconcile pass and suspend what
  the operator has not accepted.
- `SuspendAsync` holds every row of a plugin instance: the credential is revoked and the runtime scaled down.
  `ResumeAsync` re-applies, and `BeginUninstallAsync` tears the instance down. A held row refuses operator commands
  and updates.
- To pin the networks an operator approved, call `PinAttachmentAsync`. A workload whose pinned names differ from its
  spec, or whose attachment does not exist, parks until the pin or the network is fixed.
- A teardown that keeps failing leaves the row `Terminating` and raises the `.stuck` Repairs issue. Offer
  `ForgetAsync` for such a row only behind the permission that guards workload removal. It deletes the row without
  calling the provider and returns what was left behind, including any credential that could not be revoked. Show that
  to the operator, and revoke the credentials yourself when the result says they are still live.

### A workload that does not come up

A workload on Docker or Kubernetes that is not ready `Workloads:Reconciler:DeployStalledAfterSeconds` (10 minutes by
default) after its deploy started is reported `Degraded` with the reason `Provisioning`, and the engine stops
waiting for it. One that was healthy and lost its health signal is `Degraded` with the reason `HealthLost`. Both raise
the `.stuck` Repairs issue (see [monitoring](monitoring.md#repairs-issues)) and recover when the workload becomes
ready. A Manual workload never stalls, because the operator deploys it. A `Degraded` workload can be switched to
manual, which is how an operator takes over a runtime that will not come up.

## Starting the engine

```csharp
public static Task RunReeveWorkloadsAsync(
    this IServiceProvider services,
    bool runMigrations = true,
    CancellationToken cancellationToken = default);
```

`RunReeveWorkloadsAsync` is the one call that starts the engine. It migrates the workloads schema unless
`runMigrations` is false, then starts the engine's loop and returns. The loop contends for the leadership, runs
every engine unit while it holds it, and stops them when it loses it. The call returns once the loop is running,
not once it has won, so a replica that never leads does not block its own startup. `cancellationToken` is the
engine's "run until" token, not a bound on the call.

- Call it after `Build()` and before the application starts, or from the `StartAsync` of a hosted service of your
  own. A host that composes the engine and never calls it is stopped: once the application has started, a start
  guard logs at `Critical` naming the method and calls `StopApplication()`. A call made after the application has
  started is too late.
- There is one engine per process. A second call starts nothing and logs a warning.
- The call refuses to start, before recording anything, when `AddEngine()` was not called, when a host update stage
  is placed outside the span it requires, when more than one `IRolloutCertifier` is registered, and when two
  `IWorkloadHealthContributor`s observe the same health mode. Fix the composition and call again.
- The call throws after recording the start, and un-records it so a retry is a real second attempt, when the
  migration fails or is cancelled, when no migrator is registered and `runMigrations` is true, or when starting the
  runner throws.

### `runMigrations: false`

Pass `runMigrations: false` when:

- The host migrates the workloads schema in a deployment step of its own (an idempotent SQL script, see
  [the database and upgrades](database-and-upgrades.md#migrating-from-a-script)). Call `RunReeveWorkloadsAsync`
  once that step has run.
- The host runs on a provider with no schema, such as EF Core InMemory. There `AddEntityFrameworkStores` registers
  no migrator, and asking to migrate throws.

A host that lets the call migrate needs no step of its own.

## Shutdown

On shutdown the start guard's hosted service stops the runner and does not return until every unit has stopped
and the leadership has been resigned. A resigning PostgreSQL leader unlocks explicitly, with a 5-second bound. A
lock whose unlock did not complete is held until the server ends or resets that session.
