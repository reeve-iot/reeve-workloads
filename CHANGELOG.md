# Changelog

All notable changes to this project are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[Semantic Versioning](https://semver.org/).

## Unreleased

## 1.1.0 - 2026-11-26

### Breaking changes

- Removed `IWorkloadParameterEncryptionGate` and `AlwaysOpenWorkloadParameterEncryptionGate`. Password parameters
  are always encrypted at rest with the host's Data Protection keys. A stored password value that is neither
  `enc:v1:` ciphertext nor a `ref:` reference is undecryptable, so its instance waits in `PendingConfiguration` until
  the value is entered again.
- Removed `WorkloadParameterSecretCodec.ProtectStored`, the `Protect` overload that takes `encryptionEnabled`, and the
  `encryptionEnabled` parameter of `ProtectAgainstStored` and `ProtectNamedParameters`.
- Removed `ReeveWorkloadsBuilder.AddAdapter`, `ReeveWorkloadsBuilder.AdapterPorts` and
  `ReeveWorkloadsHostPortAudit.FindHostPortsOnLibraryAdapters`. `IWorkloadDeploymentContextSource`,
  `IWorkloadReconcileInputsProvider` and `IWorkloadUpdatePlanner` are ordinary library registrations of
  `AddEngine()`, which a host's own registration replaces.
- Removed the `WorkloadIntentResumer` and `WorkloadUpdateAbandonService` constructors without the group and instance
  repositories, and the `GroupManifestReconcileInputsProvider` constructor without the instance repository.
- Removed `WorkloadStateReasonNames` and `WorkloadStateReasonConverter`. `WorkloadStateReason.RunningVersionBehind`
  is stored and sent as `RunningVersionBehind`.
- Removed `WorkloadUpdateIntent.LastSwapAttemptAtUtc` and its `last_swap_attempt_at_utc` column.
- `IWorkloadReferenceRolloutService.GetPendingAsync` has no default implementation; an implementer provides it.
- The migrations are squashed into one, `20260918041436_InitialWorkloadsSchema`. A database created by 1.0.x already
  records that migration, so the migrator applies nothing to it and does not detect the older schema. Update such a
  database by hand before upgrading, with every 1.0.x replica stopped:
  1. Make sure every stored password parameter is ciphertext or a `ref:` reference: 1.0.x encrypts them once its
     encryption gate is open.
  2. In `workloads.workload_instances`, set `state_reason` to `RunningVersionBehind` where it is
     `ProtocolIncompatible`.
  3. Drop the column `workloads.workload_update_intents.last_swap_attempt_at_utc`.
  4. Delete every row of `workloads.__WorkloadsMigrationsHistory` except `20260918041436_InitialWorkloadsSchema`.

### Added

- Every release is also published as a GitHub Release, with its changelog section as the notes.
- `ReeveWorkloadsBuilder.TryAdd` with a factory, for a library service whose construction needs a factory.

### Changed

- The packages depend on `Reeve.Pipeline` 1.0.1 and `Reeve.EntityFrameworkCore.AdvisoryLocks` 1.0.1.
- Documentation fixes: the architecture overview states that the host-path policy allows the whole `/dev/serial` and
  `/dev/dri` trees, as the code always did, and the provider guide describes
  `WorkloadProviderDescriptor.SupportsUserNamespaces`.

## 1.0.1 - 2026-11-25

### Changed

- An update intent with no stage journal, or with one that does not account for its phase, is handled like an
  intent whose journal cannot be read: the walk leaves it as it is and logs why, abandoning it ends it, and a
  rollback of it ends as failed. The library writes a journal for every intent it stages, so only rows created
  outside the library are affected. A host that creates `WorkloadUpdateIntent` rows itself, for example in tests,
  records a journal with `RecordStageJournal(PipelineJournal.New())` before saving the row.
- `WorkloadUpdateIntent.LastSwapAttemptAtUtc` is not read or written by the engine. The column stays in the schema.

## 1.0.0 - 2026-11-25

First public release: twelve packages, `Reeve.Workloads.*`, versioned together and published on nuget.org. All
target `net11.0`. Persistence uses EF Core 11 and Npgsql 11. PostgreSQL 11 or newer is supported, and the tests run
against PostgreSQL 18. The packages depend on `Reeve.Pipeline` and `Reeve.EntityFrameworkCore.AdvisoryLocks`, both
1.0.0 on nuget.org.

### Added

- `Reeve.Workloads.Core`: the engine. It reconciles every workload instance toward its desired spec, using the applied
  spec hash as the drift signal. A failed apply leaves the hash unchanged, and a per-instance queue with backoff
  deduplicates nudges from the host.
- Operator actions: install, edit and restore binding validation, day-2 commands and parameter edits, host hold,
  ownership switch to the Manual provider and back, uninstall with retained volumes, and adoption of workloads that
  already run.
- Fail-closed reads: an unreadable document, network list, node list or certifier is never treated as "nothing
  there".
- A Repairs scanner raises issues for workloads that need an operator, and a retained-volume scan reports data left
  behind after an uninstall.
- A health barrier after every apply. An instance is `Healthy` only when the provider and the host's presence evidence
  agree, and a stalled deploy reads as `Degraded`.
- A reversible update pipeline built on `Reeve.Pipeline`, with the stages params, sidecar, operator redeploy, health
  gate, version commit and params commit, plus any stage the host adds. A journal decides how far a rollback may go,
  and an update can be abandoned until it enters an irreversible stage. A walk that meets a stage its build does not
  register ends where it stands and reports the stage.
- `Reeve.Workloads.Providers.Docker`: containers with journaled recreate, named networks and macvlan attachments,
  volume group ownership, adoption of running containers, and a preflight probe set.
- `Reeve.Workloads.Providers.Kubernetes`: Deployments, DaemonSets, Jobs, Services, Secrets and PVCs, in-cluster
  ServiceAccount or remote kubeconfig, Multus network attachments, and per-workload user namespaces on Kubernetes 1.33
  or newer.
- `Reeve.Workloads.Providers.Manual`: renders a deployable artifact for an operator to apply by hand.
- A provider SPI. A provider is an ordinary package that registers `IWorkloadProvider`, and every SPI call goes through
  a registry wrapper with a deadline and a metric, so third-party providers are covered too.
- `Reeve.Workloads.EntityFrameworkCore` and `Reeve.Workloads.EntityFrameworkCore.PostgreSql`: the `DbContext`,
  repositories and migrations of the `workloads` schema, with `jsonb` conventions and partial unique indexes on
  PostgreSQL. `RunReeveWorkloadsAsync` applies the migrations, or skips them with `runMigrations: false` when the host
  migrates in a step of its own. A schema guard refuses a host that re-registers the context over the library's.
- Optimistic concurrency tokens and unique constraints serialise replicas. Password parameters are encrypted at rest
  behind a host-owned gate.
- Leader election: one replica runs the engine units. With `AddPostgreSqlLeaderElection()` leadership is a
  session-scoped PostgreSQL advisory lock from `Reeve.EntityFrameworkCore.AdvisoryLocks`, probed every `LeadershipProbeInterval` (5 s by default).
  Units start in registration order and stop in reverse when leadership is lost or the host shuts down.
- `Reeve.Workloads.Manifest` and `Reeve.Workloads.Domain`: the parser for the `deployment` manifest a plugin ships,
  with a path policy, and its resolution into a desired spec with parameters, resource quantities, privilege
  requirements, network-attachment policy and deterministic resource names.
- `Reeve.Workloads.Abstractions` and `Reeve.Workloads.DependencyInjection`: `AddReeveWorkloads(...)` composes the
  engine through one builder. The host answers three required ports (manifest source, orchestrator settings source and
  parameter encryption gate), registers ASP.NET Core Data Protection, and every other port has a default.
- `ValidateHostPorts()` fails the boot and names any port nothing answers. `FindHostPortsOnLibraryDefaults()` lists
  the ports a host leaves on defaults. A start guard stops a host that never calls `RunReeveWorkloadsAsync`.
- Committed contracts: `contracts/wire-shape.json` for the JSON wire shape and `contracts/api-surface.txt` for the
  public .NET surface.
- `Reeve.Workloads.Testing`: test doubles, including an in-memory provider that drives the reconciler with no cluster
  and no daemon.
- `Reeve.Workloads.DeploymentFixtures`: manifest builders and update-pipeline conformance fixtures for a host's own
  tests.
- Telemetry: one meter and one activity source, both named `Reeve.Workloads`. They cover reconcile duration and errors,
  pass duration, provider call duration, queue depth, settling and barrier timeouts, with `workload.reconcile` and
  `workload.provider.<operation>` spans.
- `samples/minimal-host`, a console host of the library with no server and no daemon.
