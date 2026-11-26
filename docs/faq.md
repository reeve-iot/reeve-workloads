# FAQ

Known problems come first. Each one gives the symptom or exact error, its cause and its fix. Common questions
follow.

## Known problems

### The Reeve workloads engine was composed but never started

Symptom: the host logs this at `Critical` and stops shortly after startup.

Cause: the host composed the engine (`AddEngine()`) and nothing called `RunReeveWorkloadsAsync`, or it was called
after the application had started. The start guard checks once, on `ApplicationStarted`, and stops the host so it
does not look healthy while reconciling nothing.

Fix: call `app.Services.RunReeveWorkloadsAsync()` after `Build()` and before `RunAsync()`, or from the
`StartAsync` of a hosted service of your own. See [host setup](operations/host-setup.md#starting-the-engine).

### The workloads context resolved with a migrations assembly of '...' rather than the library's '...'

Symptom: the host throws this when the workloads context is first configured.

Cause: the host registers its own `AddDbContext<WorkloadsDbContext>(...)` after `AddEntityFrameworkStores`, usually
with `MigrationsAssembly(...)` or `MigrationsHistoryTable(...)` lines. The last registration wins per setting, so
the context would migrate the host's chain into the library's history.

Fix: delete the host's own `AddDbContext<WorkloadsDbContext>` and pass only the provider and the connection to
`AddEntityFrameworkStores`. Keep no copy of the library's migrations. See
[the schema guard](operations/database-and-upgrades.md#the-schema-guard).

### The workloads engine was granted its PostgreSQL advisory lock, but a following statement ran on a different database session

Symptom: an `Error` on every leadership retry, and no replica leads.

Cause: the workloads connection is not pinned to one database session. A transaction-pooling proxy such as
pgbouncer in transaction mode sits between the host and PostgreSQL. A session-scoped advisory lock cannot survive
that, so the election refuses to lead.

Fix: give the workloads `DbContext` a connection with session-level pooling, or register an
`IWorkloadLeaderElection` of your own. See [one pinned session](operations/configuration.md#one-pinned-session).

### Two replicas both reconcile the same fleet

Symptom: every replica logs `Workloads engine leadership taken by the single-replica election` at startup, and
both run a full engine.

Cause: no lock-backed election is registered, so each replica runs `SingleReplicaLeaderElection`, which always wins
and cannot see the other.

Fix: call `AddPostgreSqlLeaderElection()`, or register an election of your own, before scaling past one replica,
and supply a shared `IWorkloadLockProvider`. See
[running more than one replica](operations/configuration.md#running-more-than-one-replica).

### After a node failure no replica takes over for about two hours

Symptom: after a power loss or network partition, nothing is deployed, updated, reconciled or repaired for more
than two hours.

Cause: the lost leader's PostgreSQL session holds the advisory lock until the server's TCP keepalive budget runs
out: `tcp_keepalives_idle + tcp_keepalives_interval * tcp_keepalives_count`, which is 7875 seconds on stock
settings. A killed process is detected at once. A powered-off node or a partition is not.

Fix: connect over TCP and set the three `tcp_keepalives_*` settings on the server to the failover you want. The
election logs the budget it found once per process. See
[TCP keepalives](operations/configuration.md#tcp-keepalives-and-the-failover-window).

### The leadership flaps on a server with `idle_session_timeout` set

Symptom: the leader keeps losing the leadership, and the replicas keep trading it.

Cause: a leader is an idle session between probes. An `idle_session_timeout` shorter than
`Workloads:EngineHosting:LeadershipProbeInterval` ends the leader's session and releases the lock.

Fix: leave `idle_session_timeout` unset for the application role, or set it well above the probe interval.

### A workload is held in PendingConfiguration with an undecryptable parameter

Symptom: a deploy leaves the instance in `PendingConfiguration` with the reason `ConfigRequired`. The state message
names the undecryptable password parameter.

Cause: a stored `enc:v1:` password parameter fails authentication under the current key ring, almost always
because the key ring was lost or is not shared between replicas. A stored value that is neither ciphertext nor a
`ref:` reference is treated the same way. The value is never treated as plaintext, so the deploy stops.

Fix: restore the key ring. If the key is lost for good, edit the parameter and enter the value again, or replace it
with a reference. An edit does not need to decrypt the old value. See
[the Data Protection key ring](operations/configuration.md#data-protection-key-ring).

### `42P01` or `42703` when several replicas start together on an empty database

Symptom: some replicas fail their migration with one of these PostgreSQL error codes.

Cause: EF Core's migration lock is taken on the history table, which does not exist yet on an empty database. The
replicas race, one applies the migrations and the others fail.

Fix: nothing is damaged, and the failed replicas succeed on their next start. For a fresh multi-replica install,
start one replica first or migrate in a step of your own.

### `fail: Microsoft.EntityFrameworkCore.Database.Command[20102] Failed executing DbCommand ... __WorkloadsMigrationsHistory`

Symptom: this line appears in the log on the first start against a fresh database.

Cause: EF queries the migrations history before it creates it, and logs the miss at `fail`.

Fix: none needed. It does not appear once the history table exists.

### Files in a Docker volume are not writable by the workload after an update

Symptom: the updated workload cannot write files that exist in one of its volumes.

Cause: before creating a container, the Docker provider hands the workload's named volumes to `privileges.fsGroup` with
a short-lived helper container from the workload's own image (which therefore needs `sh`, `find`, `chgrp` and `chmod`,
or the apply is refused), skipping a volume whose root already has the right group and mode. A previous version of the
workload that is still running during that pass and writes files as root can leave files the pass does not see. The root
already matches, so they stay unwritable until the volume is next found mismatched.

Fix: fix the ownership of the affected files by hand, or change the volume root's group or mode so the next apply
re-owns the volume recursively.

### A Kubernetes volume is stranded after its node is lost

Symptom: after a node failure, a workload's claim cannot be used on another node.

Cause: the volume's StorageClass is topology-bound (`WaitForFirstConsumer`, such as k3s `local-path`), so the
claim is pinned to the node where the pod first scheduled. The preflight warns about this as
`storageclass-topology`.

Fix: this is expected on a single node. For high availability, use a network or replicated StorageClass with
immediate binding, set as `defaultStorageClassName` or named by the volume.

### A workload with `security.userNamespace` turned on does not start

Symptom: the workload does not start on Kubernetes with the parameter turned on.

Cause: user namespaces need Kubernetes 1.33 or newer and nodes whose container runtime, kernel and volume types
support them.

Fix: upgrade the nodes, or turn the parameter off for that workload. A workload on the host network, or one that
binds a device, runs without a user namespace regardless, and the preflight says so.

### A daemon or cluster test suite passes with every test skipped

Symptom: the run exits 0 and every test in the suite is skipped.

Cause: the Docker, EF integration and Kubernetes suites skip instead of failing when their daemon, database or
cluster is not reachable.

Fix: read the skip count, and run the suite where its dependency exists. See
[CONTRIBUTING](../CONTRIBUTING.md#testing).

### `NU1101` or `NU1100` on restore

Symptom: restore fails with `NU1101` (a package is not found) or `NU1100` (a package cannot be resolved).

Cause: for `NU1101`, a machine-wide `packageSourceMapping` claims `Reeve.*` for a feed these packages are not on.
For `NU1100`, a source is mapped but not listed.

Fix: every package, including `Reeve.Pipeline` and `Reeve.EntityFrameworkCore.AdvisoryLocks`, comes from nuget.org
and needs no source mapping. Make sure nuget.org is a listed source and that no machine-wide source mapping claims
`Reeve.*` for another feed.

### A test passes locally against an older dependency than the one a host deploys

Symptom: a test passes in the repository but the same code misbehaves against the dependency version a host
deploys.

Cause: shipped projects declare their dependencies as floors (`VersionOverride`), and a test project that reaches
them only through a project reference resolves the floor, not the central pin.

Fix: add the package to `tests/Directory.Build.props` in the same change that gives it a `VersionOverride`. See
[CONTRIBUTING](../CONTRIBUTING.md#packages-and-versions).

## Questions

### Why does `1.0.0-alpha.2` count as newer than `1.0.0-alpha.10`?

The engine compares prerelease labels ordinally, not by full semantic-version precedence, so `alpha.2` sorts after
`alpha.10`. This is deliberate. It is the comparison the reference host's loader makes, and a stricter order would
disagree with the loader about which install is newer and raise false version skew. Use zero-padded or single-digit
prerelease counters if the order matters to you.

### Can the engine run on SQLite?

No. The migrations live in the PostgreSQL package, and `EnsureCreated()` with `runMigrations: false` does not carry
it either. The SQLite provider cannot order by a `DateTimeOffset`, so update advancement fails on every pass. The
retained-volume scan's query does not translate. SQLite has no schemas, and with no conventions package the
active-row unique indexes are created unfiltered. An install still reaches `Healthy`, which is why it can look like
it works. Use PostgreSQL, or EF Core InMemory for a throwaway host.

### Can the Docker provider run behind more than one host replica?

No. Containers on one daemon are invisible to a leader on another machine, so the Docker provider runs on a single
host instance. Kubernetes supports several replicas.

### Does the engine ever delete retained volumes?

No. Volumes retained at uninstall are kept indefinitely. The retained-volume scan raises one informational alert
for volumes past the age threshold (90 days by default), and an operator purges them by hand. See
[monitoring](operations/monitoring.md#the-retained-volume-alert).

### What does Reeve check about an operator network?

Its name and whether it exists. A workload on an operator network passes when every attachment it names is listed
by the provider, and a changed network behind the same name never re-asks consent or parks the workload. Reeve does
not read a network's driver, routes or addressing. A setup that cannot work fails when the container starts.

### Which ports does a host have to implement?

`IDataProtectionProvider` (through `AddDataProtection()`), `IWorkloadGroupManifestSource` and
`IWorkloadOrchestratorSettingsSource`, plus `IWorkloadExternalLiveness` once the Manual provider is registered.
`ValidateHostPorts()` names whatever is missing at boot. Everything else has a default. See [host
setup](operations/host-setup.md#what-a-host-supplies).
