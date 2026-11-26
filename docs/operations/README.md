# Operating a host built on Reeve.Workloads

This guide is for whoever composes, deploys and runs a host on the `Reeve.Workloads.*` packages, whether that is
the Reeve platform or a host of your own. The library is not a service. It runs inside the host's process, so
running it means running that host.

## Pages

| Page | What it covers |
| --- | --- |
| [host-setup.md](host-setup.md) | Composing the engine into a host, what a host supplies, starting the engine |
| [configuration.md](configuration.md) | Running more than one replica, PostgreSQL connection requirements, the Data Protection key ring, orchestrator settings per provider, every options section |
| [database-and-upgrades.md](database-and-upgrades.md) | The database, the library's migrations and the schema guard, upgrading a host, mixed-version replicas |
| [monitoring.md](monitoring.md) | Metrics and traces, the Repairs issues the engine raises, the retained-volume alert, log lines worth alerting on |
| [../faq.md](../faq.md) | Known problems and common questions |

## Requirements

- .NET 11. Every package targets `net11.0` only and is built against EF Core 11 and Npgsql 11
  (`Npgsql.EntityFrameworkCore.PostgreSQL` 11).
- PostgreSQL 11 or newer for production. The advisory-lock leader election uses `hashtextextended`, which
  PostgreSQL 10 lacks. The library is tested against PostgreSQL 18. PostgreSQL is the one database the library
  ships migrations and conventions for (see [the database](database-and-upgrades.md#the-database)).
- A runtime for the workloads, one or more of:
  - A Docker daemon reachable from the host (`Reeve.Workloads.Providers.Docker`). The Docker provider runs on a
    single host instance, because containers on one daemon are invisible to a leader on another machine.
  - A Kubernetes cluster (`Reeve.Workloads.Providers.Kubernetes`), in-cluster through the host's ServiceAccount
    or remote through a self-contained kubeconfig. Several host replicas are supported. The per-workload
    user-namespace option needs Kubernetes 1.33 or newer.
  - An operator who applies rendered artifacts by hand (`Reeve.Workloads.Providers.Manual`).
- An ASP.NET Core Data Protection key ring that every replica reads and that survives restarts (see
  [configuration](configuration.md#data-protection-key-ring)).

## The short version

1. Compose the engine and supply the obligatory ports ([host-setup.md](host-setup.md)).
2. Point `AddEntityFrameworkStores` at PostgreSQL over a connection pinned to one session.
3. To run more than one replica, call `AddPostgreSqlLeaderElection()`, set the server's `tcp_keepalives_*`
   settings and share the Data Protection keys ([configuration.md](configuration.md)).
4. Call `RunReeveWorkloadsAsync` after `Build()` and before the application starts. It applies the library's
   migrations unless told not to ([database-and-upgrades.md](database-and-upgrades.md)).
5. Export the `Reeve.Workloads` meter and activity source, and show the Repairs issues to the operator
   ([monitoring.md](monitoring.md)).
