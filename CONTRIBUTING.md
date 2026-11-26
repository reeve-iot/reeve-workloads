# Contributing

Pull requests are welcome. Open them against `main`.

## Building

You need the .NET 11 SDK. Every dependency restores from nuget.org.

```bash
dotnet build Reeve.Workloads.slnx -c Release
```

The solution holds the packages under `src/`, the test projects under `tests/` and `samples/minimal-host`. The
`ci/wire-shape` and `ci/api-surface` tools and their test projects are not in the solution, so `dotnet pack` never
publishes them. Build them on their own.

Warnings do not fail the build, but keep `src` free of them. Code in `src` awaits with `ConfigureAwait(false)`
(`CA2007`).

### Packages and versions

Package versions are central, in `Directory.Packages.props`. A shipped project can declare a runtime dependency with
`VersionOverride`, so the package floor is not raised to the pinned version. In that case reference the package in
`tests/Directory.Build.props` too, so the tests build against the pinned version. Do not repeat the reference in a
test `.csproj`: a second `PackageReference` to the same id fails with NU1504.

## Testing

Run the tests your change can reach, with a filter:

```bash
dotnet test tests/Reeve.Workloads.Core.Tests -c Release --filter "FullyQualifiedName~WorkloadRepairsScanner"
```

| Project | Needs |
| --- | --- |
| `Reeve.Workloads.Abstractions.Tests`, `.Core.Tests`, `.Composition.Tests`, `.EntityFrameworkCore.PostgreSql.Tests`, `.OffMarketplace.Tests`, `.ThirdPartyProvider.Tests`, `.Providers.Kubernetes.Tests`, `.Providers.Docker.Tests`, `.Providers.Manual.Tests`, `.DeploymentFixtures.Tests`, `.Testing.Tests` | Nothing |
| `Reeve.Workloads.EntityFrameworkCore.IntegrationTests` | Docker. Testcontainers starts PostgreSQL 18. |
| `Reeve.Workloads.Providers.Docker.IntegrationTests` | A Docker daemon. The macvlan cases also need the variables below. |
| `Reeve.Workloads.Providers.Kubernetes.IntegrationTests` | A throwaway Kubernetes cluster. The Multus case also needs Multus. |

A suite that needs a daemon, database or cluster skips when it is not reachable, and the run still exits 0. Read the
skip count, not only the exit code. If Testcontainers cannot reach its published ports (connection refused on
localhost), set `TESTCONTAINERS_HOST_OVERRIDE`.

`samples/minimal-host` is built with the solution and no test runs it. Its README says how to run it by hand.

### Docker macvlan cases

The macvlan cases skip unless these variables are set. None has a default: the cases create a macvlan on the parent
interface you name.

```bash
export REEVE_E2E_MACVLAN_PARENT=<parent-interface>
export REEVE_E2E_MACVLAN_SUBNET=<subnet-cidr>
export REEVE_E2E_MACVLAN_IP_RANGE=<ip-range-cidr>      # outside the segment's DHCP scope
export REEVE_E2E_MACVLAN_GATEWAY=<gateway-ip>
export REEVE_E2E_MACVLAN_DHCP_POOL=<first-ip>-<last-ip> # optional: the run stops if the range overlaps it
dotnet test tests/Reeve.Workloads.Providers.Docker.IntegrationTests -c Release
```

Everything the suite creates carries the label `com.reeve.test.harness=docker-integration` and is removed at the end.

### Kubernetes

The suite skips unless `REEVE_E2E_KUBECONFIG` is set. CI uses a kind cluster. Locally, a k3d cluster works:

```bash
k3d cluster create reeve-e2e --servers 1 --agents 1 --kubeconfig-update-default=false
k3d kubeconfig write reeve-e2e --output ./reeve-e2e.kubeconfig
kubectl --kubeconfig ./reeve-e2e.kubeconfig create namespace reeve-e2e

REEVE_E2E_KUBECONFIG=./reeve-e2e.kubeconfig \
  dotnet test tests/Reeve.Workloads.Providers.Kubernetes.IntegrationTests \
  --filter FullyQualifiedName~KubernetesProviderK3dTests

k3d cluster delete reeve-e2e
```

The suite creates and deletes objects in the `reeve-e2e` namespace of whatever cluster the kubeconfig points at. Check
the server and context first.

### Multus

`KubernetesMultusAttachmentTests` runs only with `REEVE_E2E_KUBECONFIG`, `REEVE_E2E_MULTUS=1` and
`REEVE_E2E_MULTUS_VLAN_CIDR`. The cluster needs
Multus, the `route-override` CNI plugin, the per-node `cni-dhcp` daemon and two NetworkAttachmentDefinitions in the
`reeve-e2e` namespace:

- a macvlan over a VLAN parent with DHCP IPAM and `route-override` chained after it, named by
  `REEVE_E2E_MULTUS_NAD` (default `iot-macvlan`);
- the same without `route-override`, named by `REEVE_E2E_MULTUS_UNSUPPRESSED_NAD` (default
  `iot-macvlan-unsuppressed`).

| Variable | Default | Meaning |
| --- | --- | --- |
| `REEVE_E2E_MULTUS_VLAN_CIDR` | none, required | The CIDR the DHCP lease must come from. |
| `REEVE_E2E_MULTUS_PRIMARY_IF` | `eth0` | The pod's primary interface. |
| `REEVE_E2E_MULTUS_ATTACH_IF` | `net1` | The interface Multus gives the attachment. |

```bash
REEVE_E2E_KUBECONFIG=<kubeconfig> REEVE_E2E_MULTUS=1 REEVE_E2E_MULTUS_VLAN_CIDR=<vlan-cidr> \
  dotnet test tests/Reeve.Workloads.Providers.Kubernetes.IntegrationTests \
  --filter FullyQualifiedName~KubernetesMultusAttachmentTests
```

The test uses `registry.k8s.io/pause` (pinned by digest) as the workload and `busybox:1.36` as a helper. A cluster that
mirrors registries or has no internet access needs both available.

Jobs the test creates carry the label `reeve.io/e2e-multus`. Clean up after a failed run with
`kubectl -n reeve-e2e delete jobs -l reeve.io/e2e-multus`.

## Pull requests

CI runs on every pull request and every push to `main`:

| Job | What it runs |
| --- | --- |
| `build-and-test` | A Release build of `Reeve.Workloads.slnx`, the eleven unit test projects and `dotnet pack`. |
| `ef-integration` | The EF integration tests, with PostgreSQL 18 started by Testcontainers. |
| `docker-integration` | The whole Docker integration project. The macvlan cases skip without `REEVE_E2E_MACVLAN_*`. |
| `kubernetes-integration` | The Kubernetes integration tests on a kind cluster, with `REEVE_E2E_KUBECONFIG` set and a `reeve-e2e` namespace. The Multus case skips without `REEVE_E2E_MULTUS`. |

### Local checks

CI does not run these. Run the ones your change needs before you open a pull request, and commit what they
regenerate in the same pull request.

| Check | Command | Run it when |
| --- | --- | --- |
| Wire shape | `dotnet run --project ci/wire-shape -- check` (`-- emit` regenerates `contracts/wire-shape.json`) | You change a wire contract. Tests: `ci/wire-shape.tests`. |
| API surface | `dotnet run --project ci/api-surface -c Release -- check` (`-- emit` regenerates `contracts/api-surface.txt`) | You change a public type. Build the solution in Release first. Tests: `ci/api-surface.tests`. |
| Spec hash | `python3 ci/spec-hash-completeness.py --self-test`, then `--check` | You change the desired spec or `SpecHasher`. Every spec property is hashed or listed on a `// spec-hash-excluded: Type.Property -- reason` line. |

After an intended public API change, run `-- emit` for the API surface and commit `contracts/api-surface.txt`. See
[the architecture overview](docs/architecture.md#contracts) for what the wire-shape and API-surface files record. A
provider package of your own is described in [Writing a provider](docs/writing-a-provider.md).

## Releasing

1. Run `dotnet run --project ci/api-surface -c Release -- check` (see Local checks). CI does not run it, and nothing
   else catches an unintended public API break.
2. Move the changes under a new heading `## X.Y.Z - YYYY-MM-DD` in `CHANGELOG.md` and set `<Version>` in
   `Directory.Build.props` to that version. The file holds exactly one `<Version>` line. For every release the changelog
   records what a consumer or operator must change.
3. Merge to `main`, then push the tag `vX.Y.Z` on `main`.

The `Release` workflow takes the version from the tag, builds, runs the unit tests, packs, and pushes every package,
symbols included, to nuget.org. A second job then creates the GitHub Release for the tag, with the tag's `CHANGELOG.md`
section as its notes; the first job checks that the section exists before anything is pushed. If only the second job
fails, re-run that job. The push does not skip duplicates: a stable version is published once, and a re-cut release
needs a new version.

Publishing uses nuget.org trusted publishing. `NuGet/login@v1` exchanges the workflow's GitHub OIDC token for a
short-lived API key, so no key is stored. The account name is the `NUGET_USER` Actions variable, and the account
holds a trusted publishing policy for this repository and `release.yml`.

`README.md` is packed into every package as the NuGet readme, so every link in it is absolute.

## Dependency updates

Renovate, configured in `renovate.json`, opens pull requests for NuGet packages and GitHub Actions versions. It
excludes `Reeve.*` packages. .NET major versions (`Microsoft.Extensions.*`, `Microsoft.AspNetCore.*`, EF Core, Npgsql
and the `dotnet-version` in the workflows) move by hand together with the target framework, and EF Core stays below
12. The `VersionOverride` floors in `src/` are not updated by Renovate either; raise one by hand when the library
needs a newer version.
