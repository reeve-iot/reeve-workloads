# Reeve.Workloads

The workload engine of Reeve, published as .NET NuGet packages so that any host, not only Reeve, can run the same
reconciler, providers and deployment manifest.

A plugin declares the sidecar workloads it needs in a `deployment` manifest. The engine turns each declared workload
into a Docker container, a set of Kubernetes objects, or an artifact an operator applies by hand. It keeps the
workload converged with its desired spec, moves it between releases through a reversible update pipeline, and tears
it down while keeping its data. Whether a workload may run, which plugin it belongs to and how credentials are issued
are the host's decisions, supplied through ports.

## Where it sits in Reeve

Reeve is split across repositories:

- [`reeve-iot/reeve`](https://github.com/reeve-iot/reeve) is the platform: the host application, its API and UI, and
  the reference host of this library.
- Plugins ship `deployment` manifests describing their workloads.
- This library depends on two others:
  [`reeve-pipeline`](https://github.com/reeve-iot/reeve-pipeline), the stage engine behind the update pipeline
  (package `Reeve.Pipeline`), and
  [`reeve-advisory-locks`](https://github.com/reeve-iot/reeve-advisory-locks), the PostgreSQL advisory-lock primitive
  behind leader election (package `Reeve.EntityFrameworkCore.AdvisoryLocks`).

Nothing in these packages references a particular host.

## The packages

| Package | What it is |
| --- | --- |
| `Reeve.Workloads.Abstractions` | The vocabulary: the domain model, the ports a host implements, the provider SPI, the wire contracts and the deployment manifest a plugin declares. Every other package and every consumer takes this one. |
| `Reeve.Workloads.DependencyInjection` | The composition surface: `AddReeveWorkloads`, the builder every package registers through, and the host-port audit. A provider of your own takes this and Abstractions, not the engine. |
| `Reeve.Workloads.Domain` | The rules over that vocabulary: desired-spec resolution, parameters, resource quantities, privilege requirements, deployment diffs, network-attachment policy, resource naming. Nothing beyond the BCL. |
| `Reeve.Workloads.Manifest` | The deployment-manifest parser. |
| `Reeve.Workloads.Core` | The engine: reconcile, health gating, the update pipeline, and the loop that elects a leader and runs the engine's units on it. |
| `Reeve.Workloads.Providers.Docker` | Runs workloads as Docker containers. |
| `Reeve.Workloads.Providers.Kubernetes` | Runs workloads as Kubernetes objects. |
| `Reeve.Workloads.Providers.Manual` | Renders a deployable artifact for an operator to apply by hand. |
| `Reeve.Workloads.EntityFrameworkCore` | The persistence layer and its `DbContext`. The library registers the context and owns its migrations; the host supplies only the provider and the connection. |
| `Reeve.Workloads.EntityFrameworkCore.PostgreSql` | PostgreSQL support: the migrations, the model conventions, and an advisory-lock leader election for running more than one replica. |
| `Reeve.Workloads.DeploymentFixtures` | Manifest builders and update-pipeline conformance fixtures, for a host's own tests. |
| `Reeve.Workloads.Testing` | Test doubles for a host's own tests, including an in-memory provider that drives the reconciler with no cluster and no daemon. |

All packages share one version and target .NET 11 (`net11.0`).

## Requirements

- .NET 11.
- PostgreSQL 11 or newer (tested against 18). PostgreSQL is the one database the library ships migrations for.
- A Docker daemon, a Kubernetes cluster, or an operator applying artifacts by hand.
- An ASP.NET Core Data Protection key ring that survives restarts and is shared by every replica.

## Installation

```bash
dotnet add package Reeve.Workloads.DependencyInjection
dotnet add package Reeve.Workloads.Core
dotnet add package Reeve.Workloads.EntityFrameworkCore.PostgreSql
dotnet add package Reeve.Workloads.Providers.Docker       # and/or Providers.Kubernetes, Providers.Manual
```

A typical host takes these four: the composition surface, the engine, persistence with PostgreSQL leader
election, and the provider packages it ships. `Reeve.Workloads.Abstractions`, `Domain` and `Manifest` come in
transitively. `Reeve.Workloads.Testing` and `Reeve.Workloads.DeploymentFixtures` are for the host's own tests.

## Quickstart

```csharp
using Reeve.Workloads.DependencyInjection;

builder.Services.AddDataProtection();   // persist the keys where every replica can read them

builder.Services
    .AddReeveWorkloads(builder.Configuration, ports => ports
        .Use<IWorkloadGroupManifestSource, MyManifestSource>(ServiceLifetime.Scoped)
        .Use<IWorkloadOrchestratorSettingsSource, MySettingsSource>(ServiceLifetime.Scoped))
    .AddEngine()
    .AddEntityFrameworkStores(o => o.UseNpgsql(connectionString))
    .AddPostgreSqlLeaderElection()   // required when more than one replica runs
    .AddDockerProvider()             // reference only the provider packages you ship
    .AddKubernetesProvider()
    .ValidateHostPorts();            // fails the boot and names any port nothing answers

var app = builder.Build();
await app.Services.RunReeveWorkloadsAsync();   // migrates the schema, then starts the engine
await app.RunAsync();
```

The host implements two ports: where manifests come from and where the orchestrator settings are stored. Password
parameters are encrypted at rest with the host's Data Protection keys. Every other port has a default, and
`FindHostPortsOnLibraryDefaults()` lists the ones a host is standing on.

`RunReeveWorkloadsAsync` is the one call that starts the engine. Call it after `Build()` and before the application
starts. A host that composes the engine and never calls it is stopped at startup with a `Critical` log line. Pass
`runMigrations: false` when the host migrates the `workloads` schema in a step of its own.

With more than one replica, the PostgreSQL connection must stay on one database session, so no transaction-pooling
proxy. Failover after a lost node takes as long as the server's TCP keepalive budget, about two hours on stock
settings, so set `tcp_keepalives_*` on the server.

[`samples/minimal-host`](https://github.com/reeve-iot/reeve-workloads/blob/main/samples/minimal-host/README.md) is a
host of this shape that runs with no server and no daemon.

## Telemetry

The library publishes one meter and one activity source, both named `Reeve.Workloads`:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Reeve.Workloads"))
    .WithTracing(t => t.AddSource("Reeve.Workloads"));
```

## Documentation

- [Operations](https://github.com/reeve-iot/reeve-workloads/blob/main/docs/operations/README.md): host setup,
  configuration reference, the database and upgrades, monitoring.
- [FAQ](https://github.com/reeve-iot/reeve-workloads/blob/main/docs/faq.md): known problems and common questions.
- [Architecture](https://github.com/reeve-iot/reeve-workloads/blob/main/docs/architecture.md): a short overview of the
  packages, the host and library split, reconciliation, updates and providers.
- [Writing a provider](https://github.com/reeve-iot/reeve-workloads/blob/main/docs/writing-a-provider.md): shipping a
  provider of your own.
- [Contributing](https://github.com/reeve-iot/reeve-workloads/blob/main/CONTRIBUTING.md): building, testing,
  releasing.
- [Changelog](https://github.com/reeve-iot/reeve-workloads/blob/main/CHANGELOG.md).

## Licence

MIT. See [LICENSE](https://github.com/reeve-iot/reeve-workloads/blob/main/LICENSE).
