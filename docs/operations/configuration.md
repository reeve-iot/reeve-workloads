# Configuration

Everything an operator or host author sets: how replicas elect a leader, what the PostgreSQL connection must look
like, the Data Protection key ring, the orchestrator settings each provider reads, and every options section the
engine binds.

## Running more than one replica

### Leader election

The engine runs its units on one replica at a time, the leader. The units are the reconciler, the watch plane, the
update walk, the retained-volume scan and the repairs scanner.

By default the engine uses `SingleReplicaLeaderElection`. With no election registered, this process always wins.
That is correct for one replica and only for one. It cannot stop a second process from also winning, so two
replicas on the default both run a full engine over the same fleet. The only signal is the startup log: the first
win logs once, at `Information`, `Workloads engine leadership taken by the single-replica election: this process
assumes it is the only one.` `FindHostPortsOnLibraryDefaults()` also names `IWorkloadLeaderElection` while it
stands on this default.

For several replicas, call `AddPostgreSqlLeaderElection()`. It registers `PostgreSqlAdvisoryLockLeaderElection`
from `Reeve.Workloads.EntityFrameworkCore.PostgreSql`. It wins over the default whichever order the two calls come
in, and a host's own `IWorkloadLeaderElection` registration wins over both.

To write your own election, implement `IWorkloadLeaderElection`. `TryAcquireAsync` returns `null` when someone else
leads, or an `IWorkloadLeadership` whose `Lost` token is cancelled the moment the leadership ends. Disposing it
resigns. `Lost` must stay readable, and report cancelled, after resign.

Two `Workloads:EngineHosting` options govern the contest (see [options sections](#options-sections)). A replica
that did not win retries every `LeaderRetryInterval`, and a lock-backed leader proves it still holds the lock every
`LeadershipProbeInterval`.

### The PostgreSQL advisory lock

The leadership is a PostgreSQL session-scoped advisory lock on the key
`hashtextextended('ReeveWorkloadsEngine_Leader', 0)`. There is no lease and no row to renew. The server releases
the lock when the holder's session ends, so a replica that dies without resigning is handled by the database. The
lock statements come from the `Reeve.EntityFrameworkCore.AdvisoryLocks` package.

- The key text never changes, because replicas already contend on it. PostgreSQL has one 64-bit advisory-lock
  space per database, so a host that takes its own advisory locks must not hash the same text.
- PostgreSQL 11 or newer is required, for `hashtextextended`. On PostgreSQL 10 the first contest fails, and the
  runner logs it at `Error` on every retry.
- The connection must be pinned to one session. See [the next section](#postgresql-connection-requirements).
- Failover after a lost node is a server setting. See [TCP keepalives](#tcp-keepalives-and-the-failover-window).
- A leader resigns explicitly. The unlock is bounded at 5 seconds and is skipped while a probe still owns the
  connection. Closing a pooled connection does not end its session, so a lock whose unlock failed stays held until
  the server ends or resets that session, and no replica can lead before then. The engine logs a warning when this
  happens.

### Other defaults that matter on several replicas

- `IWorkloadLockProvider` defaults to an in-process lock. It is correct on one replica and wrong on several. A
  multi-replica host supplies a shared one.
- `IWorkloadClusterBus` defaults to in-process. Nothing depends on a hint arriving, so the default is safe, but a
  shared bus lets peer replicas react sooner.
- The Docker provider runs on one host instance, because containers on one daemon are invisible to a leader on
  another machine. Kubernetes supports many replicas.

## PostgreSQL connection requirements

### One pinned session

The advisory lock lives in the database session, so the workloads `DbContext` must run every statement of a
leadership on the same session. That rules out a transaction-pooling proxy, such as pgbouncer in transaction or
statement mode. Session-level pooling and Npgsql's own connection pool are fine. Npgsql 11 has no multiplexing, so
the connection itself cannot spread statements over sessions.

On a connection that is not pinned, a statement lands on another session and two replicas can both believe they
lead. The election checks this when it acquires. It asks the server three times whether the grant is held by the
current session and, if not, refuses to lead. The refusal is an exception, so the runner logs it at `Error` on
every retry, with the full explanation once per process and a one-line form afterwards.

The check cannot prove pinning: a proxy that happens to route the three checks to one session passes it. During a
leadership, the probe asks the same question every `LeadershipProbeInterval`, which also detects a pool reset that
releases the lock while the connection stays alive. Neither replaces a pinned connection. Use a connection with
session-level pooling, or an `IWorkloadLeaderElection` of your own.

### Slow statements and the probe

A probe statement can fail on a live session that still holds the key, for example under a `statement_timeout` on
the application role plus a load spike, or after an administrator's `pg_cancel_backend`. The probe therefore
repeats a failed statement once, after a short jittered pause, before giving up the leadership, and logs the retry
at `Information`. A session that is really gone fails again within a round trip, and the leadership is lost.

### TCP keepalives and the failover window

A killed leader process is detected at once: the kernel closes the socket and the session ends. A lost node (power
loss, a network partition) sends nothing, and its session ends only when the server's TCP keepalive budget runs
out:

```text
tcp_keepalives_idle + tcp_keepalives_interval * tcp_keepalives_count
```

On stock PostgreSQL that is 7200 + 75 * 9 = 7875 seconds, a failover of more than two hours during which nothing is
deployed, updated, reconciled or repaired. A deployment with more than one replica connects over TCP and sets the
three settings to the failover it wants, for example:

```ini
# postgresql.conf: a failover of about one minute after a lost node
tcp_keepalives_idle = 30
tcp_keepalives_interval = 10
tcp_keepalives_count = 3
```

The election logs the budget it found on its own session once per process, at `Information`, at its first
acquisition. A Unix-domain socket, or a platform that cannot report the options, reads as zeros. The log then says
the budget is unknown (the operating system's policy decides), never instant.

Two settings look like they help and do not:

- `client_connection_check_interval` checks only while a query runs, and a leader between probes is idle.
- `idle_session_timeout` ends the leader's own session when it is shorter than `LeadershipProbeInterval`, and the
  leadership then flaps. Keep it unset, or well above the probe interval (which is capped at 10 minutes).

### Migrations on several replicas

EF Core's migration lock serialises replicas that migrate at once, but only once the history table exists. On an
empty database, replicas starting together can fail (`42P01`, `42703`) while another applies the migrations, and they
succeed on their next start. For a fresh multi-replica install, start one replica first or migrate in a step of
your own (see [the database and upgrades](database-and-upgrades.md)).

## Data Protection key ring

The library protects secrets with the host's ASP.NET Core Data Protection, under two purposes:

| Purpose | What it protects |
| --- | --- |
| `Reeve.Workloads.WorkloadParameters` | Password workload parameters, stored with the `enc:v1:` prefix (see [host setup](host-setup.md#what-a-host-supplies)). |
| `Reeve.Workloads.ReferenceRolloutKey` | The key behind the fingerprint of an unpinned parameter reference. |

The key ring must survive restarts and be readable by every replica, for example with `PersistKeysToDbContext`,
`PersistKeysToFileSystem` on a shared volume, or a cloud key store. Keys that live and die with the process are
acceptable only for a host whose workloads database lives and dies with the process too.

A lost or rotated-away key has two effects:

- Password parameters are undecryptable. A deploy holds the instance in `PendingConfiguration` with an
  undecryptable reason, and an edit can replace the value.
- Unpinned references are held until the key ring is restored and the rollout approved again.

## Orchestrator settings

The orchestrator settings are one document the host serves through `IWorkloadOrchestratorSettingsSource`; the host
stores it, protects its secrets and decides who edits it. Every field is optional, so a fresh install is all defaults.

| Key | Default | Effect |
| --- | --- | --- |
| `provider` | null | The selected provider id. Null auto-detects through the registered providers, in ascending priority: in-cluster first, then a local runtime, the always-available fallback last. |
| `providers` | empty | Each provider's own entry, keyed by provider id. The engine never looks inside one. The provider's codec validates and masks it. |
| `allowUnpinnedImages` | false | Permit workload images that are not pinned by digest. |
| `mirrors` | empty | Registry mirror rewrite rules (`matchRegistry`, `mirrorRegistry`), digest-preserving. |
| `reconciliationPaused` | false | Global kill switch: status reads continue, every mutation queues. The per-workload observe-only flag lives on the instance. |

Secrets go out masked as `***`, and `ResolveMasked` turns a `***` sent back into the stored secret. A key in a
provider's entry that the provider's settings record does not own is kept in the row as the host's. The library
neither protects nor masks such keys.

### Docker (`providers.docker`)

| Key | Default | Effect |
| --- | --- | --- |
| `socketUri` | platform default, e.g. `unix:///var/run/docker.sock` | The daemon endpoint. |
| `networkName` | `reeve-workloads` | The Docker network workloads attach to. |
| `registryCredentials` | empty | Per-registry pull credentials: `hostPattern` (glob), `username`, `token` (secret), `authType` (`basic`, the default, or `bearer`). A credential is refused for `localhost` and loopback addresses, which Docker reaches over plain HTTP. For any other host the daemon chooses the scheme, so do not configure a credential for a registry listed in the daemon's `insecure-registries`. |

### Kubernetes (`providers.kubernetes`)

| Key | Default | Effect |
| --- | --- | --- |
| `connectionMode` | `inCluster` | `inCluster` uses the host's ServiceAccount. `remoteKubeconfig` uses `kubeconfig`. |
| `kubeconfig` | null | The inline kubeconfig for `remoteKubeconfig`. Secret, at most 64 KiB, and self-contained (rules below). |
| `kubeconfigContext` | the kubeconfig's `current-context` | The context to select. |
| `namespace` | the host's own namespace in-cluster; none on a remote kubeconfig | The namespace workloads are created in. Required with `remoteKubeconfig` (the context's namespace is not used). Without it no workload deploys and every instance reads `Unknown`. Left empty, moving the host to another namespace makes the reconciler recreate every workload in the new one while the old objects keep running. Name the namespace before moving the host, or remove the old objects. |
| `defaultStorageClassName` | the cluster default | The StorageClass for claims that name none. Applied at render, and outside the spec hash, so a change reaches a volume on the workload's next apply. |
| `imagePullSecretName` | none | An image pull secret attached to workload pods. |
| `allowUnsupportedOverrides` | false | Permits a manifest's `unsupportedPodPatch`. The patched pod is re-validated against the security baseline. |

The Kubernetes client is resolved per request from these settings and cached by a fingerprint of the connection,
so a settings edit takes effect without restarting the host. When the API server is unreachable, the watch falls
back to the reconciler's poll cadence.

#### Remote kubeconfig rules

A remote kubeconfig is validated before it is used, without any cluster I/O, and anything doubtful is refused. Its
contents are never logged or echoed, parse errors included because they can carry fragments, and it is never
written to a temporary file. It is refused when:

- The cluster sets `insecure-skip-tls-verify`, which disables server-certificate checks whatever CA is embedded.
- The cluster does not embed `certificate-authority-data`. A CA file path depends on the host's filesystem.
- The user carries no embedded credential. It needs `client-certificate-data` and `client-key-data`, an inline
  token, or basic auth.
- The user names an `exec` credential (it would run an external process on the host) or an `auth-provider`, or
  any credential read from a file path.

#### Development kubeconfig

In `inCluster` mode the Kubernetes provider reads two keys from the host's configuration:

| Key | Effect |
| --- | --- |
| `Workloads:Kubernetes:AllowKubeconfig` | `true` lets the provider connect through a kubeconfig file instead of the in-cluster ServiceAccount. |
| `Workloads:Kubernetes:KubeconfigPath` | The kubeconfig file it reads. |

Both must be set. This is for development and the live test suites. It bypasses the remote-kubeconfig validation of
the orchestrator settings and reads a file from the host's filesystem, so do not use it in production. Run in-cluster
or use `connectionMode: remoteKubeconfig`.

#### Cluster prerequisites

- Permissions. The library ships no RBAC objects. In the workload namespace the service account needs get, list,
  create, patch and delete on secrets, persistentvolumeclaims, deployments, daemonsets, jobs and services, plus watch
  on deployments, daemonsets and pods, list on pods, events and resourcequotas, get on pods/log and on the namespace
  itself, and list on network-attachment-definitions (k8s.cni.cncf.io) when operator networks are used. Cluster-wide
  read access (list nodes and storageclasses, get persistentvolumes) is optional but recommended. Without it the node,
  StorageClass and PSA preflight checks report that they could not be evaluated, and a node relocation lists the
  volumes it could not verify. A scan-per-node workload does need it: without node access its preflight fails, and
  no scan Jobs are created until the read is granted. The ClusterRole name in the preflight's suggested command is an
  example.
- Devices. A workload that binds a device runs privileged, so its namespace needs the Pod Security Admission level
  `privileged`. Under `baseline` or `restricted` the pod is refused, and the `psa-enforcement` preflight check says
  so.
- Storage classes. The preflight warns (`storageclass-topology`) when a volume's class is topology-bound
  (`WaitForFirstConsumer`, such as k3s `local-path`). The claim pins to the node where the pod first schedules, and
  losing that node strands the volume. That is expected on a single node. For high availability, use a network or
  replicated class with immediate binding. A class that does not exist warns (`storageclass-missing`) and the claim
  stays `Pending` until it does.
- User namespaces. The reserved `security.userNamespace` parameter runs a workload in its own user namespace. It
  needs Kubernetes 1.33 or newer and nodes whose runtime, kernel and volumes support it. A workload on the host
  network, or one that binds a device (which runs privileged on Kubernetes), runs without it, and the preflight
  says so.
- Operator networks. A network attachment is judged by its name and whether it exists. Reeve reads nothing of what
  the network contains, so its driver, routes and addressing are the operator's to get right. A setup that cannot
  work fails when the container starts.

### Manual (`providers.manual`)

The Manual provider reads none of its entry. Whatever an operator sets there is the host's, kept as stored.

## Options sections

`AddEngine()` binds these sections from the configuration passed to `AddReeveWorkloads`, for example from
`appsettings.json` or environment variables (`Workloads__Reconciler__StatusResyncSeconds`). Every default suits a
stock deployment. Values are validated against the ranges below when the engine first reads
them; an out-of-range value fails with an `OptionsValidationException`.

### `Workloads:Reconciler`

| Setting | Default | Range | Effect |
| --- | --- | --- | --- |
| `StatusResyncSeconds` | 60 | 1 to 3600 | Cadence of the periodic full reconcile pass: it reads every instance's runtime status and re-applies a drifted spec, so it catches a flapping runtime or a drift with no event. |
| `WatchEnabled` | true | | Whether provider watch events trigger reconciles. The periodic pass stays the correctness backstop. Read at composition time. |
| `HealthBarrierTimeoutSeconds` | 120 | 1 to 3600 | How long a pass waits for an applied instance to become `Healthy` before moving on. |
| `HealthBarrierPollMilliseconds` | 500 | 10 to 60000 | How often the barrier re-reads the instance row. |
| `HealthBarrierDriveSeconds` | 10 | 1 to 600 | How often a settling instance is driven through the engine again. Without a watch this is what moves it, so keep it well inside the barrier timeout. |
| `ReconcileWorkerCount` | 1 | 1 to 64 | Queue workers reconciling in parallel. |
| `MaxConcurrentSettling` | 1 | 1 to 64 | Instances that may wait on the health barrier at once. 1 makes a fleet-wide change roll instead of restarting everything together. |
| `RetryBackoffBaseMilliseconds` | 1000 | 10 to 60000 | First retry delay after a reconcile throws; doubles per consecutive fault, with jitter. |
| `RetryBackoffMaxSeconds` | 300 | 1 to 3600 | The cap on that doubling. |
| `FleetStatusFreshnessSeconds` | 15 | 1 to 600 | How long a batched fleet status read stays usable within a pass. |
| `DeployStalledAfterSeconds` | 600 | 60 to 86400 | How long after its deploy started a materialized workload may stay not ready before it is reported `Degraded` / `Provisioning`. |

### `Workloads:Watch`

| Setting | Default | Range | Effect |
| --- | --- | --- | --- |
| `DebounceMilliseconds` | 250 | 0 to 60000 | Per-instance window that collapses the events one apply provokes into one reconcile. |

### `Workloads:ProviderTimeouts`

| Setting | Default | Range | Effect |
| --- | --- | --- | --- |
| `CallTimeoutSeconds` | 60 | 1 to 3600 | Deadline for a read or a short mutation: status, preflight, logs, attachment existence, suspend, resume. |
| `MutationTimeoutSeconds` | 300 | 1 to 7200 | Deadline for an apply or a teardown, which pull images and delete volumes. A teardown that runs past it fails and is retried on the next pass. |
| `Enabled` | true | | False hands providers the caller's token unchanged, for a host that enforces its own deadline elsewhere. |

### `Workloads:UpdateWalk`

| Setting | Default | Range | Effect |
| --- | --- | --- | --- |
| `WorkingTickSeconds` | 5 | 1 to 300 | The longest a stage that answered `Working` waits to be asked again. A stage waiting on an event calls `IWorkloadUpdateWalkTrigger.Wake()` instead. |
| `IdleFallbackSeconds` | 60 | 1 to 3600 | How long the walk sleeps with no open update. An initiation wakes it at once. |

### `Workloads:UpdateOrchestrator`

Values are time spans (`hh:mm:ss`). A host that registers its own `WorkloadUpdateOrchestratorOptions` instance before
calling `AddEngine()` replaces this section.

| Setting | Default | Effect |
| --- | --- | --- |
| `HealthGateTimeout` | `00:05:00` | How long the health gate waits for the updated workload to become healthy, counted from when it starts running. Image pulls and spans in which the provider or the host evidence is unavailable do not count. |
| `HealthGateMaximumWait` | `01:00:00` | The health gate's absolute limit, counted from the gate's start with pulls and unavailable spans included. It only ends a gate whose `HealthGateTimeout` never runs out; a value below `HealthGateTimeout` is raised to it. |
| `RollbackTimeout` | `00:05:00` | How long a forward apply may make no progress, and how long the previous version may take to come back healthy during a rollback. |

### `Workloads:RetainedScan`

| Setting | Default | Range | Effect |
| --- | --- | --- | --- |
| `ThresholdDays` | 90 | 1 to 3650 | Age past which a retained volume appears in the informational alert. |
| `ScanIntervalHours` | 6 | 1 to 168 | Scan cadence. |

### `Workloads:Repairs`

| Setting | Default | Range | Effect |
| --- | --- | --- | --- |
| `ScanCadenceSeconds` | 120 | 1 to 3600 | How often the leader sweeps every instance. |
| `StuckWarningAfterSeconds` | 900 | 1 to 86400 | Grace before a `Degraded`, `Failed`, `Unknown` or `Terminating` instance raises a stuck issue. |
| `CrashLoopRestartThreshold` | 3 | 1 to 1000 | Restart-count rise that raises a crash-loop issue. |
| `CrashLoopWindowSeconds` | 900 | 1 to 86400 | Window the restart rise is accumulated over. |
| `ParkedWarningAfterSeconds` | 604800 | 1 to 31536000 | Age after which a release parked by a rolled-back update escalates from Info to Warning. |
| `ManualVersionSkewWarningAfterSeconds` | 600 | 1 to 86400 | Grace before a Manual-provider instance in version skew raises a Warning. |

What each issue means is in [monitoring](monitoring.md#repairs-issues).

### `Workloads:EngineHosting`

| Setting | Default | Range | Effect |
| --- | --- | --- | --- |
| `LeaderRetryInterval` | 00:00:05 | 100 ms to 10 min | How long a replica that did not win waits before contending again. |
| `LeadershipProbeInterval` | 00:00:05 | 100 ms to 10 min | How often a lock-backed leader proves it still holds the lock. The single-replica election ignores it. |

### `Workloads:ProductImages`

Maps a bundled workload's `image.component` slug to concrete image coordinates, so a system workload tracks the
running host's version.

```json
{
  "Workloads": {
    "ProductImages": {
      "Components": {
        "virtual-devices": {
          "Ref": "<your-registry>/reeve/virtual-devices",
          "Tag": "<host-version>",
          "Digest": "sha256:<index-digest>"
        }
      }
    }
  }
}
```

`Ref` carries no tag, so the registry mirror rewrite applies unchanged. With `Digest` set the render pins by digest;
a tag alone is accepted only with `allowUnpinnedImages`. A component with no entry fails closed at render.

### Not configurable

The workloads schema is `workloads` and the history table is `workloads.__WorkloadsMigrationsHistory`
(`WorkloadsStoreOptions` exposes both, read-only). The migrations name them literally, so neither can move.
