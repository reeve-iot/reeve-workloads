# The database and upgrades

The workloads database, the migrations the library owns, the guard that keeps them the library's, and how to
upgrade a host without downtime.

## The database

PostgreSQL is the one database. `Reeve.Workloads.EntityFrameworkCore` holds the `DbContext` and works over any
relational EF Core provider in principle. `Reeve.Workloads.EntityFrameworkCore.PostgreSql` supplies the PostgreSQL
model conventions (`jsonb` columns, the partial unique indexes), the migrations and the advisory-lock leader
election. Referencing the PostgreSQL package is the whole of a host's database wiring apart from the connection:
the model finds the conventions on its own when the context runs on Npgsql.

Another database is served by a provider package of its own, through the same seam. The package declares
`[assembly: WorkloadsDatabaseConventions(providerName, typeof(TConventions))]`, where `TConventions` is a public
type implementing `IWorkloadsDatabaseConventions` with a public parameterless constructor, and ships its migrations
in the same assembly. The library matches the attribute to the provider the context reports, applies those
conventions to the model and points EF at that assembly for migrations. Without the interface or the constructor
it throws when it builds the model. A provider that no conventions package serves gets the active-row unique
indexes unfiltered, which blocks re-recording a purged volume or opening a second update after a finished one.
SQLite does not carry the engine at all; see the [FAQ](../faq.md#can-the-engine-run-on-sqlite).

## Library-owned migrations

The schema is `workloads` and the history table is `workloads.__WorkloadsMigrationsHistory`. Neither is
configurable, because the migrations name them literally.

The schema is built by one migration, `20260918041436_InitialWorkloadsSchema`. Besides the tables and indexes of the
model, it adds a unique index over `lower(module_id)` on `workload_modules` by raw SQL, because the model cannot
express it. That index refuses a second module row that differs only by case.

`RunReeveWorkloadsAsync` applies pending migrations by default, each in its own transaction. No `Down` is reachable
from a host, because `IWorkloadsSchemaMigrator` takes no target. To roll a release back, restore a backup of the
`workloads` schema taken before it.

### Migrating from a script

A host that migrates in a deployment step of its own, and passes `runMigrations: false`, can generate an idempotent
script from a checkout of this repository at the version it pins:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations script --idempotent --context WorkloadsDbContext \
  --project src/Reeve.Workloads.EntityFrameworkCore.PostgreSql -o workloads.sql
psql -v ON_ERROR_STOP=1 -f workloads.sql
```

- Write the script with `-o` instead of redirecting it. The EF tools print a version-mismatch warning on standard
  output, and a redirect makes it the first line of the script.
- The repository has no tool manifest, so install `dotnet-ef` globally.
- Run the script with `ON_ERROR_STOP=1`. Without it psql reports the error, carries on and exits 0.

The script is idempotent: applying it a second time to the same database changes nothing.

## The schema guard

The workloads context's migrations assembly and history table are the library's, not the host's.
`AddEntityFrameworkStores(configure)` applies them after the host's `configure`, through EF's provider-agnostic
relational options, and refuses a service collection that already registers `WorkloadsDbContext`. The provider is
read once, at registration, from a throwaway options builder. Two provider packages serving one provider, or no
provider at all, therefore fail the boot instead of every scope. If `configure` constructs a data source, that
throwaway builder creates one extra that is never disposed. A host that wants exactly one builds it itself and
closes over it.

A host that registers its own `AddDbContext<WorkloadsDbContext>(...)` after the library's call takes the migrations
assembly back, because `AddDbContext` does not `TryAdd` its options action and the last writer wins per setting. EF
would then apply the host's migrations and record their ids in the library's history, and a later release would run
the library's own migrations over populated tables. So every workloads context checks its final options when it is
configured, whether it migrates, reads or is merely opened, and throws:

```text
The workloads context resolved with a migrations assembly of '<found>' rather than the library's '<expected>'.
Something re-registered WorkloadsDbContext after AddEntityFrameworkStores ...
```

The fix is to delete the host's own `AddDbContext<WorkloadsDbContext>(...)`, including its `MigrationsAssembly`
and `MigrationsHistoryTable` lines, and pass only the provider and the connection to `AddEntityFrameworkStores`.
The check also runs on `runMigrations: false`. Keep no copy of the library's migrations in the host.

## Upgrading a host

`CHANGELOG.md` lists what each release contains and, for every release, what a consumer or operator must change.
Read the entries between the version you run and the one you move to before upgrading.

### Rolling restarts and mixed-version replicas

Schema changes are expand/contract. A release adds only what a build of the previous release ignores, so replicas on
two adjacent releases can run side by side for the length of one rolling restart. A release whose `CHANGELOG.md` entry
lists a manual database update is the exception: apply that update with every replica of the previous release stopped.
Replicas further apart than that are not supported side by side. A column or table is dropped only in the release
after the one that stopped using it.

How a stalled deploy is reported is in [host setup](host-setup.md#a-workload-that-does-not-come-up).

### Updates in flight during a rollout

A walk whose journal stands at an update stage the walking build does not register is ended where it stands, as
`RollbackFailed` naming the stage. That happens when a host rollout renames or removes a stage of its own, when a
library upgrade adds one, or when a stage is registered only under some configuration. Let open updates finish
before such a rollout, and do not start one while replicas run different builds. To recover a walk that ended this
way, redeploy the plugin at the version you want.

### Rolling back

Restore a backup of the `workloads` schema taken before the upgrade, then deploy the previous build. The stored
password parameters are ciphertext under the Data Protection keys (see
[host setup](host-setup.md#what-a-host-supplies)), so keep the key ring with the backup.
