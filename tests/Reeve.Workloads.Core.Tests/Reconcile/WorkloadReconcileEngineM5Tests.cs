using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Testing;
using Xunit;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Core.Tests.Reconcile;

public sealed class WorkloadReconcileEngineM5Tests
{
    private const string GroupId = "zwave";
    private const string WorkloadId = "gateway";

    [Fact]
    public async Task Installed_module_ahead_of_running_sidecar_with_no_intent_is_VersionSkew_with_banner()
    {
        var harness = new Harness
        {
            InstalledGroupVersion = "2.0.0",
            SidecarAppVersion = "1.0.0",
        };
        var (engine, instance) = harness.BuildHealthy();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.ResultingState.Should().Be(WorkloadInstanceState.VersionSkew,
            "a module ahead of a still-running old sidecar with no active intent is a version skew.");
        instance.State.Should().Be(WorkloadInstanceState.VersionSkew);
        instance.StateMessage.Should().Contain("2.0.0").And.Contain("1.0.0",
            "the banner must name both versions so the skew is diagnosable.");
    }

    [Fact]
    public async Task No_VersionSkew_when_sidecar_matches_the_installed_module()
    {
        var harness = new Harness
        {
            InstalledGroupVersion = "2.0.0",
            SidecarAppVersion = "2.0.0",
        };
        var (engine, instance) = harness.BuildHealthy();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.ResultingState.Should().NotBe(WorkloadInstanceState.VersionSkew);
    }

    [Fact]
    public async Task No_VersionSkew_when_an_active_intent_covers_the_instance()
    {
        var harness = new Harness
        {
            InstalledGroupVersion = "2.0.0",
            SidecarAppVersion = "1.0.0",
            HasActiveIntentTarget = true,
        };
        var (engine, instance) = harness.BuildHealthy();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.ResultingState.Should().NotBe(WorkloadInstanceState.VersionSkew,
            "an in-flight update intent means the reconciler converges on the target — it must not flip to VersionSkew.");
    }

    private sealed class Harness
    {
        public string? InstalledGroupVersion { get; init; }
        public string SidecarAppVersion { get; init; } = "1.0.0";
        public bool HasActiveIntentTarget { get; init; }

        public FakeHostHealthContributor Sessions { get; } = new();
        public InMemoryWorkloadProvider Provider { get; } = new(KubernetesProvider.Descriptor);
        public string IntentTargetHash { get; } = "intent-target-hash";
        public string AppliedHash { get; } = "applied-hash";

        public (WorkloadReconcileEngine engine, WorkloadInstance instance) BuildHealthy()
        {
            var instanceId = Guid.NewGuid();
            var pluginInstanceId = Guid.NewGuid();
            var instance = WorkloadInstance.Create(instanceId, pluginInstanceId, GroupId, WorkloadId, DateTimeOffset.UtcNow);

            var appliedSpec = SpecFor(instanceId, pluginInstanceId, AppliedHash);
            Provider.ApplyAsync(appliedSpec, progress: null).GetAwaiter().GetResult();
            Provider.SetStatus(instanceId, WorkloadProviderPhase.Ready, providerReady: true);
            instance.SetProvider(KubernetesProvider.Id, DateTimeOffset.UtcNow);
            instance.SetAppliedSpecHash(AppliedHash, DateTimeOffset.UtcNow);
            DriveTo(instance, WorkloadInstanceState.Healthy);

            var targetSpec = SpecFor(instanceId, pluginInstanceId, IntentTargetHash);

            Sessions.SeedWithAppVersion(instanceId, AppliedHash, SidecarAppVersion);

            var resolver = new StubResolver(HasActiveIntentTarget ? targetSpec : null);

            var intentTargetForProvider = HasActiveIntentTarget ? targetSpec : null;
            var intentProvider = new StubActiveIntentProvider(intentTargetForProvider);
            var inputsProvider = new StubInputsProvider(() => BuildInputs(pluginInstanceId));
            var registry = new StubRegistry(Provider);

            var engine = new WorkloadReconcileEngine(
                inputsProvider,
                resolver,
                registry,
                new NoOpRepository(),
                Sessions.Dispatch(),
                new FixedClock(),
                NullLogger<WorkloadReconcileEngine>.Instance,
                NSubstitute.Substitute.For<IRetainedVolumeService>(),
                intentProvider, new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver(), new Reeve.Workloads.Core.WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), null,
                new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());

            return (engine, instance);
        }

        private WorkloadReconcileInputs BuildInputs(Guid pluginInstanceId) => new()
        {
            Namer = DefaultWorkloadResourceNamer.Instance,
            WorkloadManifest = ManifestFor(),
            ResolvedParameters = new Dictionary<string, string?>(),
            Settings = new WorkloadOrchestratorSettings(),
            Bootstrap = new WorkloadBootstrapContext { InstanceId = pluginInstanceId, SecretName = "reeve-wl-secret" },
            EffectiveProvider = KubernetesProvider.Descriptor,
            InstallationId = "install-1",
            InstalledGroupVersion = InstalledGroupVersion,
        };
    }

    private static void DriveTo(WorkloadInstance instance, WorkloadInstanceState target)
    {
        var now = DateTimeOffset.UtcNow;
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, now);
        if (target == WorkloadInstanceState.Healthy)
        {
            instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, now);
        }
    }

    private static WorkloadManifest ManifestFor() => new()
    {
        Id = WorkloadId,
        DisplayName = "Z-Wave JS",
        Image = new WorkloadImage { Ref = "registry.example/zwave-js", Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.SingleNode,
        Parameters = Array.Empty<WorkloadParameter>(),
        Volumes = Array.Empty<WorkloadVolume>(),
        HostRequirements = Array.Empty<HostRequirement>(),
        Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
    };

    private static WorkloadDesiredSpec SpecFor(Guid instanceId, Guid pluginInstanceId, string hash)
    {
        var inst8 = WorkloadInstance.ComputeInst8(instanceId);
        return new WorkloadDesiredSpec
        {
            GroupInstanceId = pluginInstanceId,
            GroupId = GroupId,
            WorkloadId = WorkloadId,
            InstanceId = instanceId,
            Inst8 = inst8,
            Provider = KubernetesProvider.Id,
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

    private sealed class StubResolver(WorkloadDesiredSpec? target) : IDesiredSpecResolver
    {
        public Task<WorkloadDesiredSpec> ResolveAsync(Guid pluginInstanceId, string workloadId, Func<WorkloadDesiredSpec> computeFresh, CancellationToken cancellationToken)
            => Task.FromResult(target ?? computeFresh());
    }

    private sealed class StubActiveIntentProvider(WorkloadDesiredSpec? target) : IActiveWorkloadIntentProvider
    {
        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult(target);
        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken)
            => Task.FromResult(target is null ? (Guid?)null : Guid.NewGuid());
    }

    private sealed class StubInputsProvider(Func<WorkloadReconcileInputs> factory) : IWorkloadReconcileInputsProvider
    {
        public Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => ReconcileInputsAnswers.Of(factory());
    }

    private sealed class StubRegistry(IWorkloadProvider provider) : IWorkloadProviderRegistry
    {
        public IWorkloadProvider? TryResolve(WorkloadProviderId kind) => kind == provider.Provider ? provider : null;
        public IWorkloadProvider Resolve(WorkloadProviderId kind) => TryResolve(kind) ?? throw new WorkloadProviderNotRegisteredException(kind);
        public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders => new[] { provider.Provider };

        public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors
            => [.. RegisteredProviders.Select(Describe)];

        public WorkloadProviderDescriptor Describe(WorkloadProviderId kind)
            => TryResolve(kind)?.Descriptor ?? ShippedProviders.For(kind);
    }

    private sealed class NoOpRepository : IWorkloadInstanceRepository
    {
        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<WorkloadInstance?>(null);
        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default) => Task.FromResult<WorkloadInstance?>(null);
        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkloadInstance>>(Array.Empty<WorkloadInstance>());
        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkloadInstance>>(Array.Empty<WorkloadInstance>());
        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkloadInstance>>(Array.Empty<WorkloadInstance>());
        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(new Dictionary<WorkloadInstanceState, int>());
        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedClock : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
    }

}
