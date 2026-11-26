# Monitoring

What the engine tells an operator: its metrics and traces, the Repairs issues it raises, the retained-volume
alert, and the log lines worth alerting on.

## Telemetry

The library publishes one meter and one activity source, both named `Reeve.Workloads`.

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Reeve.Workloads"))
    .WithTracing(t => t.AddSource("Reeve.Workloads"));
```

| Instrument | Unit | What it answers |
| --- | --- | --- |
| `reeve.workloads.reconcile.duration` | s | How long one reconcile of one instance took, by `trigger` and `outcome` (`applied`, `settling`, `settled`, `noop`, `conflict`, `fault`, `barrier-timeout`). |
| `reeve.workloads.reconcile.errors` | `{error}` | Reconciles that did not complete, by `trigger` and `kind`. A lost write race is not a fault. |
| `reeve.workloads.pass.duration` | s | Whether the fleet pass keeps cadence. Measured from enqueue to a quiet queue, so a pass that enqueued fast and then settled for two minutes reads as two minutes. |
| `reeve.workloads.provider.call.duration` | s | How long a provider call took, by `provider`, `operation` and `outcome`. Recorded at the provider registry, so a third-party provider is covered too. |
| `reeve.workloads.queue.depth` | `{instance}` | Instances with reconcile work scheduled, running or waiting on a backoff. |
| `reeve.workloads.queue.settling` | `{instance}` | Instances that applied a spec and are waiting on the health barrier. |
| `reeve.workloads.barrier.timeouts` | `{timeout}` | Instances that applied a spec and never reached `Healthy` within the barrier. |

The queue gauges report nothing on a replica that is not the leader, because it has no queue to describe.

Spans are `workload.reconcile` and `workload.provider.<operation>`. The instance id is a span tag and not a metric
tag. It is the dimension you need to chase one workload, and it would blow up cardinality across a fleet.

## Repairs issues

The leader's Repairs scanner sweeps every instance every `Workloads:Repairs:ScanCadenceSeconds` (2 minutes) and
raises issues through `IWorkloadDiagnosticsSink`. The host decides how to show them. Codes are
`workload.<workloadId>.<kind>`:

| Code suffix | Severity | Raised when |
| --- | --- | --- |
| `.stuck` | Warning, or Error while `Failed` | The instance has been continuously `Degraded`, `Unknown` or `Terminating` (Warning) or `Failed` (Error) for more than `StuckWarningAfterSeconds` (15 minutes). A `Terminating` instance is a removal that keeps failing, and its message names the failure. A stalled deploy (`Degraded` / `Provisioning`) raises at once, because it has already waited out `Workloads:Reconciler:DeployStalledAfterSeconds`. The message quotes the instance's `StateMessage`. |
| `.crashloop` | Warning | The restart count rose by `CrashLoopRestartThreshold` (3) within `CrashLoopWindowSeconds` (15 minutes). Not gated on the stuck timer. A provider that reset its counter on a fresh deploy is not looping. |
| `.version_skew` | Warning | A Manual-provider instance has been in version skew for more than `ManualVersionSkewWarningAfterSeconds` (10 minutes). The operator's own deployment lags, and nothing Reeve does closes the gap. Skew on Docker or Kubernetes is an update in flight and never raises. |
| `.parked` | Info, then Warning | A release is parked by a rolled-back update. It escalates to Warning after `ParkedWarningAfterSeconds` (7 days). Raised only when the latest finished update of the instance is `RolledBack`. A newer completed update supersedes it. |
| `.rollback_failed` | Error | The latest update of the instance ended `RollbackFailed`. Not age-escalated. The text depends on what the journal shows: a forward walk that never turned leaves whatever it did, an unreadable journal leaves the state unknown, and otherwise the new version may still run or the old one may be back but unhealthy. It names only controls that exist: the evidence, and Redeploy, which re-asserts whatever Reeve currently holds as desired. Cleared when a newer update supersedes it. |

Rules that keep the issue list quiet:

- The stuck clock starts when an instance enters the bad set and resets only when it leaves it, not when it moves
  between `Degraded`, `Unknown` and `Failed`.
- A `Failed` instance does not prove nothing runs. A failed apply on Docker leaves the old container up, and one
  failing Kubernetes pod fails the instance.
- Messages name the fixed threshold, never the elapsed time, which goes in the issue's metadata. An unchanged issue
  is therefore not re-pushed and re-audited every pass. Issues are deduplicated per instance and code by signature,
  and a severity change counts as new.
- Issues of a deleted instance are pruned, and an uninstall clears its instance's issues explicitly.
- A lookup fault leaves an open issue as it is. It never raises a spurious one.

## The retained-volume alert

Volumes retained at uninstall are kept indefinitely. Nothing deletes them automatically, and an operator purges
them by hand. The leader scans them every `Workloads:RetainedScan:ScanIntervalHours` (6 hours) and publishes one
informational alert, code `workloads.retained-volumes.90d`, listing the active retained volumes older than
`ThresholdDays` (90). The code stays the same whatever the threshold, so an operator's alert history is not split.

The alert bus has no deduplication of its own, so the scan publishes only when the qualifying set changes. It
compares an order-independent signature of each volume's id and size, so a volume that grows republishes too. An
empty set publishes nothing and resets the signature. The signature is held in memory, so a new leader publishes
the current set once. A size that cannot be parsed counts as zero, which affects only the total-size hint.

## Log lines worth alerting on

All come from loggers in the `Reeve.Workloads.*` namespaces.

| Level | Message begins | What it means |
| --- | --- | --- |
| Critical | `The Reeve workloads engine was composed but never started` | Nothing called `RunReeveWorkloadsAsync`. The start guard is stopping the host. |
| Critical | `The workloads engine's leadership pass faulted.` | The runner's own pass failed. It waits and contends again. If it repeats, the leadership may still be held by this process. |
| Error | `The workloads engine was granted its PostgreSQL advisory lock, but a following statement ran on a different database session` | The connection is not pinned to one session (a transaction-pooling proxy). Logged on every retry. See [configuration](configuration.md#one-pinned-session). |
| Error | `The workloads engine could not contend for the leadership.` | The election threw, for example on a database it cannot reach or PostgreSQL 10. |
| Error | `Workloads engine unit <unit> failed to start.` | The leader resigns rather than run some of its units. |
| Warning | `The workloads engine lost the leadership.` | Every unit stops. The replica contends again after `LeaderRetryInterval`. |
| Warning | `The workloads engine lost the leadership while starting its units.` | The unit start in progress is cancelled, the rest are not started, and every unit stops. The replica contends again after `LeaderRetryInterval`. |
| Warning | `The session the workloads engine holds its advisory lock on no longer holds it` | The lock was released behind the leader's back, typically by a pool reset. |
| Warning | `The workloads engine could not release its PostgreSQL advisory lock.` | The lock stays held until the server ends or resets that session. No replica leads before then. |
| Warning | `RunReeveWorkloadsAsync was called more than once.` | A host with two startup paths into the engine. |
| Information | `Workloads engine leadership taken by the single-replica election` | Logged once. Correct on one replica. On more than one, register a lock-backed election. |
| Information | `The workloads engine holds its leadership as a PostgreSQL session-scoped advisory lock.` | Logged once, with the failover budget in seconds. Check the number against the failover you want. |
| Information | `A workloads engine leadership probe failed, but the session still holds the lock` | The database was slow enough to nearly cost the leadership, and nothing was lost. Repeated, it points at `statement_timeout` or load. |
