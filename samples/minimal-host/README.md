# minimal-host

A console host of `Reeve.Workloads` that is not Reeve. `Program.cs` and `HostPorts.cs` are short enough to read top to
bottom. The sample composes the engine as
[host setup](../../docs/operations/host-setup.md#composing-the-engine) describes, starts it, installs one
module and prints the rows as the engine deploys them. It only reads: nothing in it drives the engine.

It shows that the library starts, elects a leader, reconciles and deploys outside its reference host. The project is
built with `Reeve.Workloads.slnx` and references the library by project, so a change that breaks what a host writes
breaks this build. It also lets you see the start guard: with `--no-engine` the engine is composed and never started,
and the guard stops the process.

## Run it

```bash
dotnet run --project samples/minimal-host
```

It needs no server, daemon or cluster. The store is Entity Framework's InMemory provider, and the workload provider is
`InMemoryWorkloadProvider` from `Reeve.Workloads.Testing`, registered as one `IWorkloadProvider` the way a provider
package would register itself. A real host references the Docker or Kubernetes package instead. The exit code is 0
when every workload reached `Healthy`.

The output looks like this:

```text
[sample] store         Entity Framework InMemory (no server, no schema, no migrations)
[sample] engine        will be started (RunReeveWorkloadsAsync)
[sample] ports standing on a library default:
[sample]   IObservedWorkloadVersionReader
…                                          ← one line per port
[sample] engine started
info: Reeve.Workloads.Core.SingleReplicaLeaderElection[0]
      Workloads engine leadership taken by the single-replica election: …
[sample] installing acme.stack 1.0.0 as 0a0e9298-…
info: Reeve.Workloads.Core.WorkloadReconcilerUnit[0]
      Workload reconciler started (1 worker(s), …).
…                                          ← the watch plane, the update walk, the retained-volume scan, the repairs
info: Reeve.Workloads.Core.WorkloadsEngineRunner[0]
      Workloads engine holds the leadership and is running 5 units.
[sample] install outcome Materialized
[sample] install done — watching the rows settle
[sample]   api  PendingConfiguration/ConfigRequired applies=0
[sample]   db   PendingConfiguration/ConfigRequired applies=0
[sample]   api  Deploying/Provisioning applies=1
[sample]   api  Healthy/HealthObserved applies=1
[sample]   db   Deploying/Provisioning applies=1
[sample]   db   Healthy/HealthObserved applies=1
[sample] every workload of the module is Healthy — the engine deployed them, and nothing here drove it
[sample] stopped. …
```

Lines without `[sample]` come from the library. The sample installs once. The install asks for its own reconcile,
through the bus for peer replicas and directly for this replica's reconciler. The applies, the health gate and the
state transitions that follow are the engine's own loop, running under a leadership it elected for itself. Rows are
printed when they change, not on every poll.

The library logs at `Information` and uses `Warning` and above for things that need a person. Everything else in the
process is turned down to `Warning`, so the console shows the engine's own output.

## The start guard

```bash
dotnet run --project samples/minimal-host -- --no-engine
```

Setting `MINIMAL_HOST_NO_ENGINE=1` does the same, which helps in an IDE where arguments are awkward. Everything is
composed (`AddReeveWorkloads`, `AddEngine`, `AddEntityFrameworkStores`, the provider, `ValidateHostPorts`) and
`RunReeveWorkloadsAsync` is never called. The run ends like this:

```text
crit: Reeve.Workloads.Core.WorkloadsEngineLifecycle[0]
      The Reeve workloads engine was composed but never started: nothing called RunReeveWorkloadsAsync. No
      workload will be reconciled, deployed, updated or repaired while this process runs, so it is being
      stopped rather than left to look healthy. Call RunReeveWorkloadsAsync on the built service provider
      during startup.
[sample] application started with no engine — the start guard has had its say
[sample] the process stopped itself. A host that composes the engine and never runs it does not boot green.
```

The exit code is 1. The guard fires on `ApplicationStarted`, so call `RunReeveWorkloadsAsync` before `StartAsync`. A
call made after the application has started is too late. After the guard calls `StopApplication()`, waiting for
shutdown returns at once, which is also how a host written as `await app.RunAsync()` unwinds.

## On a real database

```bash
docker run -d --name minimal-host-pg -e POSTGRES_PASSWORD=sample -e POSTGRES_DB=workloads \
  -p 55433:5432 postgres:18-alpine

dotnet run --project samples/minimal-host -- \
  --postgres "Host=localhost;Port=55433;Database=workloads;Username=postgres;Password=sample"
```

`MINIMAL_HOST_POSTGRES=…` does the same and keeps the password out of your shell history. This is the same host on the
store it would deploy on: `RunReeveWorkloadsAsync(runMigrations: true)` applies the library's migrations into the
`workloads` schema, and nothing else changes. To look at the result:

```bash
docker exec minimal-host-pg psql -U postgres -d workloads \
  -c 'SELECT "MigrationId" FROM workloads."__WorkloadsMigrationsHistory" ORDER BY 1;' \
  -c 'SELECT workload_id, state, state_reason FROM workloads.workload_instances ORDER BY workload_id;'
```

Remove the container with `docker rm -f minimal-host-pg` when you are done.

`runMigrations` follows the store. On PostgreSQL the engine applies the library's migrations, which is all it takes to
stand up the schema. On InMemory there is no schema, `AddEntityFrameworkStores` registers no migrator, and asking to
migrate would throw, so the sample passes `runMigrations: false` there.

On a fresh database Entity Framework asks for the migrations history before it creates it, and logs the miss at
`fail:`:

```text
fail: Microsoft.EntityFrameworkCore.Database.Command[20102]
      Failed executing DbCommand … FROM workloads."__WorkloadsMigrationsHistory" …
```

This is Entity Framework probing, not a failure. It does not appear on a second run.

## Why there is no SQLite variant

The library's migrations live in `Reeve.Workloads.EntityFrameworkCore.PostgreSql` and are PostgreSQL-specific, so
`RunReeveWorkloadsAsync` never creates a schema on SQLite. Using `runMigrations: false` and `EnsureCreated()`, which
builds the schema from the model, does not work either:

1. The SQLite provider does not support `DateTimeOffset` in `ORDER BY`. The update-intent repository orders by one, so
   the intent resumer fails at boot and the reconciler fails on every pass. Each time it logs `NotSupportedException`
   and skips update advancement, so no update ever runs and only the log says so.
2. The retained-volume scan's query does not translate. It compares a `DateTimeOffset` inside a correlated `Any(...)`,
   and the expression is refused with `InvalidOperationException: The LINQ expression … could not be translated`.
   Every scan tick fails.
3. SQLite has no schemas. `HasDefaultSchema("workloads")` draws model-validation warnings and the tables are created
   unqualified.
4. The active-row unique indexes are unfiltered, because no `IWorkloadsDatabaseConventions` package serves SQLite. The
   three indexes of `WorkloadsModelContract.ActiveRowIndexes` then hold over every row instead of over live ones. They
   block re-recording a purged volume and opening a second update after a finished one.

A freshly installed module still reaches `Healthy` on SQLite, because none of the four touches an install. The sample
would look like it works, which is why it is not offered. Support for another relational database would come as a
conventions package of its own (see [the database](../../docs/operations/database-and-upgrades.md#the-database)).

## What else to look at

### Host ports

`HostPorts.cs` answers `IWorkloadGroupManifestSource` and `IWorkloadOrchestratorSettingsSource`. The settings come from
a JSON file named by `MinimalHost:SettingsFile` (default `workload-settings.json`; a missing file means all defaults).
The sample reads the file at startup and on each file change without telling the library. A host that wants the
library to notice calls `IWorkloadOrchestratorSettingsChanges.ChangedAsync` after its document changes.

`Program.cs` also adds data protection. Nothing else is required, because the host registers no Manual provider and
so owes no `IWorkloadExternalLiveness`.
`ValidateHostPorts()` fails the boot if that stops being true: delete a line from the callback in `Program.cs` and it
names the port you removed.

Data protection here keeps its keys in memory, which suits a host whose settings store is a field in memory. A real
host persists its keys where every replica can read them, because the orchestrator settings carry registry tokens and
a kubeconfig, and password parameters are protected with the same key ring.

### The boot audit

The sample prints `FindHostPortsOnLibraryDefaults()` at boot, as [host
setup](../../docs/operations/host-setup.md#what-a-host-supplies) recommends. Most entries are something the host runs
without: no audit sink, no device groups, no per-workload credentials. Two become wrong as soon as a second replica
starts: `IWorkloadLockProvider` and `IWorkloadLeaderElection`. The list cannot tell the two kinds apart, but the host
can, because it knows how many replicas it runs. This sample runs one process, so the single-replica election is
correct, and it says so itself at `Information` when it first wins. A host with more than one replica also calls
`AddPostgreSqlLeaderElection()`.

### Engine options

The sample overrides three settings: `Workloads:Reconciler:HealthBarrierTimeoutSeconds`, `HealthBarrierDriveSeconds`
and `HealthBarrierPollMilliseconds`. The health barrier holds a reconcile pass until the instances it applied report
healthy, and waits two minutes per instance by default. That suits containers that take a while to start. With a
runtime that answers on the first poll, it would leave a blank console for two minutes, so a first run looks hung
without the overrides.

### Consent

Before installing, the sample reads what the install would require and passes back the fingerprint it read. It has no
operator to ask and accepts whatever it reads, but it still reads, because the fingerprint is also the
optimistic-concurrency token on those facts.

### What the sample does not show

Updates, rollback, suspend and resume, attachment pins, more than one replica, and a provider that can fail.
`tests/Reeve.Workloads.OffMarketplace.Tests` covers these against the same in-memory provider and is the place to read
next.

## Notes

`AddEntityFrameworkStores` registers an `IWorkloadsSchemaMigrator` only for a relational provider, and
`RunReeveWorkloadsAsync` asks the host for the `runMigrations` decision as well and throws if it does not match. The
sample computes it from the same switch that chose the provider.

The project has `IsPackable=false` and is never published. It is part of `Reeve.Workloads.slnx`, unlike `ci/wire-shape`
and `ci/api-surface`, so the solution build compiles it and breaks when the library's composition surface changes. It
references `Microsoft.Extensions.Hosting` rather than the abstractions the library takes, because it is the host. No CI
job runs it.
