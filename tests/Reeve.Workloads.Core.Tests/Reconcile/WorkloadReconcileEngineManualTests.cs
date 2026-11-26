using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Testing;
using Xunit;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Core.Tests.Reconcile;

public sealed class WorkloadReconcileEngineManualTests
{
    private const string GroupId = "acme.zwave";
    private const string WorkloadId = "controller";

    [Fact]
    public async Task Manual_provider_ready_maps_to_ExternallyManaged()
    {
        var instance = NewInstance();
        var harness = new ManualHarness("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged,
            "a manual-provider instance with a live external health signal is externally managed");
        instance.StateReason.Should().Be(WorkloadStateReason.ExternalHealthObserved);
    }

    [Fact]
    public async Task Manual_provider_without_a_live_session_does_not_become_ExternallyManaged()
    {
        var instance = NewInstance();
        var harness = new ManualHarness("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Pending, providerReady: false);
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().NotBe(WorkloadInstanceState.ExternallyManaged,
            "no live external session ⇒ the engine never fabricates ExternallyManaged");
    }

    [Fact]
    public async Task Manual_adapter_recovering_from_VersionSkew_lands_in_ExternallyManaged()
    {
        var instance = NewAdapterInstance();
        var harness = new AdapterManualHarness(installedGroupVersion: "2.0.0");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);

        harness.Presence.SeedWithAppVersion(instance.Id, "hash-live", appVersion: "1.0.0");
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.VersionSkew,
            "an AdapterSession manual sidecar reporting an appVersion older than the installed module enters VersionSkew");
        var transitionedIntoSkewAt = instance.LastTransitionAtUtc;

        harness.Presence.SeedWithAppVersion(instance.Id, "hash-live", appVersion: "2.0.0");

        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged,
            "once the manual sidecar catches up to the installed module, the recovered adapter must LEAVE VersionSkew and land in ExternallyManaged — not stay stuck");
        instance.StateReason.Should().Be(WorkloadStateReason.ExternalHealthObserved);
        instance.LastTransitionAtUtc.Should().BeAfter(transitionedIntoSkewAt,
            "leaving VersionSkew advances LastTransitionAtUtc — the state-leave that clears the manual-skew Repairs Warning");
    }

    [Fact]
    public async Task A_live_manual_runtime_reporting_an_older_spec_awaits_the_operators_redeploy()
    {
        var instance = NewAdapterInstance();
        var harness = new AdapterManualHarness(installedGroupVersion: "1.0.0");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        harness.Presence.SeedWithAppVersion(instance.Id, "hash-live", appVersion: "1.0.0");
        var engine = harness.Build();
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);

        harness.Presence.SeedWithAppVersion(instance.Id, "hash-before-the-edit", appVersion: "1.0.0");
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.Deploying,
            "the operator still runs the artifact rendered before the edit, so the current spec is not live");
        instance.StateMessage.Should().Contain("redeploy");

        harness.Presence.SeedWithAppVersion(instance.Id, "hash-live", appVersion: "1.0.0");
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);
        instance.StateReason.Should().Be(WorkloadStateReason.ExternalHealthObserved);
    }

    [Fact]
    public async Task During_a_parked_manual_update_only_the_updates_target_spec_reads_live()
    {
        var instance = NewAdapterInstance();
        var harness = new AdapterManualHarness(installedGroupVersion: "1.0.0");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        harness.Presence.SeedWithAppVersion(instance.Id, "hash-live", appVersion: "1.0.0");
        harness.Intents = new ParkedUpdate(AdapterManualHarness.SpecFor(instance.Id, "hash-target"));
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        harness.Provider.SimulateReady(instance.Id);
        await engine.ReconcileInstanceAsync(instance);

        instance.DesiredSpecHash.Should().Be("hash-target");
        instance.State.Should().Be(WorkloadInstanceState.Deploying,
            "the operator still runs the installed version's artifact, not the one the update deploys");
        instance.StateMessage.Should().Contain("redeploy");

        harness.Presence.SeedWithAppVersion(instance.Id, "hash-target", appVersion: "1.0.0");
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged);
    }

    [Fact]
    public async Task Genuine_manual_instance_that_loses_its_session_leaves_ExternallyManaged_and_is_not_frozen()
    {
        var instance = NewInstance();
        var harness = new ManualHarness("hash-live");
        harness.PreApply(instance, "hash-live");
        var engine = harness.Build();

        harness.Provider.SimulateReady(instance.Id);
        await engine.ReconcileInstanceAsync(instance);
        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged,
            "a manual instance with a live session is externally managed — the fix keeps this happy path");

        harness.Provider.SetStatus(instance.Id, WorkloadProviderPhase.Pending, providerReady: false);
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().NotBe(WorkloadInstanceState.ExternallyManaged,
            "the genuine-manual observe path is RESTORED — a lost external session is not shadowed by the ownership-switch hold");
        instance.State.Should().Be(WorkloadInstanceState.Deploying,
            "manual no-session ⇒ the awaiting/Deploying state (ManualStatusMapper); the lost-signal transition is reactive again");
    }

    [Fact]
    public async Task A_primed_fleet_status_replaces_the_per_instance_read()
    {
        var instance = NewInstance();
        var harness = new ManualHarness("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        harness.FleetStatus = FleetCache();
        harness.FleetStatus.Prime(new Dictionary<WorkloadRef, WorkloadStatus>
        {
            [WorkloadRef.FromInstance(instance)] = new() { Phase = WorkloadProviderPhase.Unknown, ProviderReady = false },
        });

        await harness.Build().ReconcileInstanceAsync(instance);

        instance.StateReason.Should().Be(WorkloadStateReason.ProviderUnreachable,
            "the status the pass already read for the whole fleet is the one the engine must use");
    }

    [Fact]
    public async Task The_read_after_an_apply_still_reaches_the_provider()
    {
        var instance = NewInstance();
        var harness = new ManualHarness("hash-live");
        harness.PreApply(instance, "hash-live");
        harness.Provider.SimulateReady(instance.Id);
        harness.FleetStatus = FleetCache();
        harness.FleetStatus.Prime(new Dictionary<WorkloadRef, WorkloadStatus>
        {
            [WorkloadRef.FromInstance(instance)] = new() { Phase = WorkloadProviderPhase.Unknown, ProviderReady = false },
        });
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        await engine.ReconcileInstanceAsync(instance);

        instance.State.Should().Be(WorkloadInstanceState.ExternallyManaged,
            "the second read found the cache empty and asked the provider, which has been ready all along");
    }

    private sealed class FrozenClock : IWorkloadClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private static WorkloadFleetStatusCache FleetCache()
        => new(
            new FrozenClock(),
            Microsoft.Extensions.Options.Options.Create(new WorkloadReconcilerOptions()),
            NullLogger<WorkloadFleetStatusCache>.Instance);

    private static WorkloadInstance NewInstance()
    {
        var now = DateTimeOffset.UnixEpoch;
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
        return instance;
    }

    private static WorkloadInstance NewAdapterInstance()
    {
        var now = DateTimeOffset.UnixEpoch;
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
        return instance;
    }

    private sealed class ManualHarness(string desiredHash)
    {
        public InMemoryWorkloadProvider Provider { get; } = new(ManualProvider.Descriptor);
        public FakeHostHealthContributor Presence { get; } = new();
        public ManualRepository Repository { get; } = new();

        public WorkloadFleetStatusCache? FleetStatus { get; set; }

        public void PreApply(WorkloadInstance instance, string hash)
        {
            var spec = SpecFor(instance.Id, hash);
            Provider.ApplyAsync(spec, progress: null).GetAwaiter().GetResult();
            instance.SetProvider(ManualProvider.Id, DateTimeOffset.UtcNow);
            instance.SetAppliedSpecHash(hash, DateTimeOffset.UtcNow);
        }

        public WorkloadReconcileEngine Build()
        {
            var inputsProvider = new ManualInputsProvider();
            var resolver = new PinnedResolver(desiredHash);
            var registry = new SingleProviderRegistry(ManualProvider.Id, Provider);
            return new WorkloadReconcileEngine(
                inputsProvider,
                resolver,
                registry,
                Repository,
                Presence.Dispatch(),
                new UnixEpochClock(),
                NullLogger<WorkloadReconcileEngine>.Instance,
                NSubstitute.Substitute.For<IRetainedVolumeService>(),
                new Reeve.Workloads.Abstractions.NullActiveWorkloadIntentProvider(), new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver(), new Reeve.Workloads.Core.WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()),
                FleetStatus,
                new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());
        }

        private static WorkloadDesiredSpec SpecFor(Guid instanceId, string hash)
        {
            var inst8 = WorkloadInstance.ComputeInst8(instanceId);
            return new WorkloadDesiredSpec
            {
                GroupInstanceId = Guid.NewGuid(),
                GroupId = GroupId,
                WorkloadId = WorkloadId,
                InstanceId = instanceId,
                Inst8 = inst8,
                Provider = ManualProvider.Id,
                ResourceName = DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        }),
                SecretName = DefaultWorkloadResourceNamer.Instance.SecretName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        }),
                Labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                        {
                            InstanceId = instanceId,
                            InstallationId = "install-1",
                            SpecHash = hash,
                        }),
                Image = "registry.example/zwave-js@sha256:" + new string('a', 64),
                Kind = WorkloadKind.SingleNode,
                Health = new WorkloadHealth { Mode = WorkloadHealthMode.Http, HeartbeatSeconds = 30 },
                SpecHash = hash,
            };
        }

        private sealed class ManualInputsProvider : IWorkloadReconcileInputsProvider
        {
            public Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
                => ReconcileInputsAnswers.Of(new WorkloadReconcileInputs
                {
                    Namer = DefaultWorkloadResourceNamer.Instance,
                    WorkloadManifest = new WorkloadManifest
                    {
                        Id = WorkloadId,
                        DisplayName = "Z-Wave JS",
                        Image = new WorkloadImage { Ref = "registry.example/zwave-js", Digest = "sha256:" + new string('a', 64) },
                        Kind = WorkloadKind.SingleNode,
                        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Http, HeartbeatSeconds = 30 },
                    },
                    ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal),
                    Settings = new WorkloadOrchestratorSettings(),
                    Bootstrap = new WorkloadBootstrapContext
            {
                InstanceId = instance.Id,
                SecretName = "workload-token",
                Values = FakeHostBootstrapSource.AllValues(
                    instance.Id, "wss://gw.example/hubs/workloads", "workload-token"),
            },
                    EffectiveProvider = ManualProvider.Descriptor,
                    InstallationId = "install-1",
                });
        }

        private sealed class PinnedResolver(string hash) : IDesiredSpecResolver
        {
            public Task<WorkloadDesiredSpec> ResolveAsync(
                Guid pluginInstanceId, string workloadId, Func<WorkloadDesiredSpec> computeFresh, CancellationToken cancellationToken)
                => Task.FromResult(computeFresh() with { SpecHash = hash });
        }

        private sealed class SingleProviderRegistry(WorkloadProviderId kind, IWorkloadProvider provider) : IWorkloadProviderRegistry
        {
            public IWorkloadProvider? TryResolve(WorkloadProviderId k) => k == kind ? provider : null;
            public IWorkloadProvider Resolve(WorkloadProviderId k) => TryResolve(k) ?? throw new WorkloadProviderNotRegisteredException(k);
            public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders => new[] { kind };

            public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors
                => [.. RegisteredProviders.Select(Describe)];

            public WorkloadProviderDescriptor Describe(WorkloadProviderId kind)
                => TryResolve(kind)?.Descriptor ?? ShippedProviders.For(kind);
        }

        private sealed class UnixEpochClock : IWorkloadClock
        {
            public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        }

        public sealed class ManualRepository : IWorkloadInstanceRepository
        {
            public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
                => Task.FromResult<WorkloadInstance?>(null);

            public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
                => Task.FromResult<WorkloadInstance?>(null);

            public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<WorkloadInstance>>([]);

            public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<WorkloadInstance>>([]);

            public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<WorkloadInstance>>([]);

            public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(new Dictionary<WorkloadInstanceState, int>());

            public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult(false);

            public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }

    private sealed class ParkedUpdate(WorkloadDesiredSpec target) : IActiveWorkloadIntentProvider
    {
        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<WorkloadDesiredSpec?>(target);
    }

    private sealed class AdapterManualHarness(string installedGroupVersion)
    {
        public InMemoryWorkloadProvider Provider { get; } = new(ManualProvider.Descriptor);
        public FakeHostHealthContributor Presence { get; } = new();
        public ManualHarness.ManualRepository Repository { get; } = new();
        public IActiveWorkloadIntentProvider Intents { get; set; } = new Reeve.Workloads.Abstractions.NullActiveWorkloadIntentProvider();

        public void PreApply(WorkloadInstance instance, string hash)
        {
            var spec = SpecFor(instance.Id, hash);
            Provider.ApplyAsync(spec, progress: null).GetAwaiter().GetResult();
            instance.SetProvider(ManualProvider.Id, DateTimeOffset.UnixEpoch);
            instance.SetAppliedSpecHash(hash, DateTimeOffset.UnixEpoch);
        }

        public WorkloadReconcileEngine Build()
        {
            var inputsProvider = new AdapterInputsProvider(installedGroupVersion);
            var resolver = new PinnedResolver("hash-live");
            var registry = new SingleProviderRegistry(ManualProvider.Id, Provider);
            return new WorkloadReconcileEngine(
                inputsProvider,
                resolver,
                registry,
                Repository,
                Presence.Dispatch(),
                new AdvancingClock(),
                NullLogger<WorkloadReconcileEngine>.Instance,
                NSubstitute.Substitute.For<IRetainedVolumeService>(),
                Intents, new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver(), new Reeve.Workloads.Core.WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), null,
                new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());
        }

        internal static WorkloadDesiredSpec SpecFor(Guid instanceId, string hash)
        {
            var inst8 = WorkloadInstance.ComputeInst8(instanceId);
            return new WorkloadDesiredSpec
            {
                GroupInstanceId = Guid.NewGuid(),
                GroupId = GroupId,
                WorkloadId = WorkloadId,
                InstanceId = instanceId,
                Inst8 = inst8,
                Provider = ManualProvider.Id,
                ResourceName = DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        }),
                SecretName = DefaultWorkloadResourceNamer.Instance.SecretName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        }),
                Labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                        {
                            InstanceId = instanceId,
                            InstallationId = "install-1",
                            SpecHash = hash,
                        }),
                Image = "registry.example/zwave-js@sha256:" + new string('a', 64),
                Kind = WorkloadKind.SingleNode,
                Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
                SpecHash = hash,
            };
        }

        private sealed class AdapterInputsProvider(string installedGroupVersion) : IWorkloadReconcileInputsProvider
        {
            public Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
                => ReconcileInputsAnswers.Of(new WorkloadReconcileInputs
                {
                    Namer = DefaultWorkloadResourceNamer.Instance,
                    WorkloadManifest = new WorkloadManifest
                    {
                        Id = WorkloadId,
                        DisplayName = "Z-Wave JS",
                        Image = new WorkloadImage { Ref = "registry.example/zwave-js", Digest = "sha256:" + new string('a', 64) },
                        Kind = WorkloadKind.SingleNode,
                        Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
                    },
                    ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal),
                    Settings = new WorkloadOrchestratorSettings(),
                    Bootstrap = new WorkloadBootstrapContext
            {
                InstanceId = instance.Id,
                SecretName = "workload-token",
                Values = FakeHostBootstrapSource.AllValues(
                    instance.Id, "wss://gw.example/hubs/workloads", "workload-token"),
            },
                    EffectiveProvider = ManualProvider.Descriptor,
                    InstallationId = "install-1",
                    InstalledGroupVersion = installedGroupVersion,
                });
        }

        private sealed class PinnedResolver(string hash) : IDesiredSpecResolver
        {
            public Task<WorkloadDesiredSpec> ResolveAsync(
                Guid pluginInstanceId, string workloadId, Func<WorkloadDesiredSpec> computeFresh, CancellationToken cancellationToken)
                => Task.FromResult(computeFresh() with { SpecHash = hash });
        }

        private sealed class SingleProviderRegistry(WorkloadProviderId kind, IWorkloadProvider provider) : IWorkloadProviderRegistry
        {
            public IWorkloadProvider? TryResolve(WorkloadProviderId k) => k == kind ? provider : null;
            public IWorkloadProvider Resolve(WorkloadProviderId k) => TryResolve(k) ?? throw new WorkloadProviderNotRegisteredException(k);
            public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders => new[] { kind };

            public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors
                => [.. RegisteredProviders.Select(Describe)];

            public WorkloadProviderDescriptor Describe(WorkloadProviderId kind)
                => TryResolve(kind)?.Descriptor ?? ShippedProviders.For(kind);
        }

        private sealed class AdvancingClock : IWorkloadClock
        {
            private long _ticks;
            public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref _ticks));
        }
    }
}
