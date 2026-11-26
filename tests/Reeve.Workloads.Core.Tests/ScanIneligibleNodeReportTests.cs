using Reeve.Workloads.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Core.Tests;

public sealed class ScanIneligibleNodeReportTests
{
    private const string PluginTypeId = "acme.zwave";
    private const string WorkloadId = "scanner";

    private const string EligibleNode = "node-with-vlan";
    private const string ExcludedNode = "node-no-vlan";
    private const string CordonedNode = "node-cordoned";

    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private readonly ScanRepository repository = new();
    private readonly FakeWorkloadClusterBus bus = new();
    private readonly NoIntentProvider intentProvider = new();
    private readonly RecordingLogger<WorkloadOperationsService> opsLog = new();
    private readonly RecordingScanProvider provider = new();

    [Fact]
    public async Task Scan_trigger_with_ZERO_eligible_but_SOME_excluded_reports_the_selector_exclusion_not_an_empty_cluster()
    {
        var instance = SeedScanInstance();
        provider.Preflight = new WorkloadPreflightResult
        {
            Checks = [],
            ReadyNodeNames = [],
            IneligibleNodeNames = [ExcludedNode, "node-no-vlan-2"],
        };

        var result = await CreateService().ScanAsync(instance.Id, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadOperationOutcome.NoTargets,
            "the outcome for both zero-eligible states stays NoTargets — the report is what distinguishes them");
        instance.HasPendingScanRun.Should().BeFalse("a NoTargets trigger leaves the pending-run marker UNSET");
        bus.Published.Should().BeEmpty("no run was started, so the reconciler is not nudged");

        var report = opsLog.Text;
        report.Should().Contain(ExcludedNode, "the *skipped, ineligible* report NAMES the excluded node");
        report.Should().Contain("node-no-vlan-2", "every excluded node is named, not just the first");
        report.Should().Contain("selector",
            "the report points at the node-eligibility SELECTOR — the operator configuration that caused the exclusion");
    }

    [Fact]
    public async Task Scan_trigger_with_a_genuinely_empty_cluster_does_NOT_claim_nodes_were_excluded()
    {
        var instance = SeedScanInstance();
        provider.Preflight = new WorkloadPreflightResult
        {
            Checks = [],
            ReadyNodeNames = [],
            IneligibleNodeNames = [],
        };

        var result = await CreateService().ScanAsync(instance.Id, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadOperationOutcome.NoTargets);
        opsLog.Text.Should().NotContain("selector",
            "a genuinely zero-Ready cluster is NOT a node-eligibility configuration problem and must not be reported as one");
        opsLog.Text.Should().NotContain("skipped",
            "nothing was skipped — there was nothing to skip");
    }

    [Fact]
    public async Task Scan_trigger_that_DOES_start_a_run_still_reports_the_skipped_nodes_beside_the_eligible_set()
    {
        var instance = SeedScanInstance();
        provider.Preflight = new WorkloadPreflightResult
        {
            Checks = [],
            ReadyNodeNames = [EligibleNode],
            IneligibleNodeNames = [ExcludedNode],
        };

        var result = await CreateService().ScanAsync(instance.Id, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        instance.HasPendingScanRun.Should().BeTrue("≥1 eligible node ⇒ the run is armed");
        opsLog.Text.Should().Contain(ExcludedNode,
            "the skipped node is named beside the eligible set — the *skipped, ineligible* report");
        opsLog.Text.Should().Contain("skipped",
            "the excluded node is reported as SKIPPED, not merely omitted");
    }

    [Fact]
    public async Task Scan_trigger_drops_the_previous_runs_recorded_failure_when_the_new_run_starts()
    {
        var instance = SeedScanInstance();
        instance.BeginScanRun("scan-earlier", DateTimeOffset.UtcNow);
        instance.CompleteScanRun(["control-plane"], DateTimeOffset.UtcNow);
        provider.Preflight = new WorkloadPreflightResult { Checks = [], ReadyNodeNames = [EligibleNode] };

        var result = await CreateService().ScanAsync(instance.Id, CancellationToken.None);

        result.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        instance.HasPendingScanRun.Should().BeTrue();
        instance.GetLastScanFailedNodes().Should().BeEmpty("the newly armed run has produced no outcome yet");
    }

    [Fact]
    public async Task Engine_writes_the_ineligible_set_onto_the_scan_spec_carrier_and_fans_out_over_the_eligible_set_ONLY()
    {
        var instance = SeedScanInstance();
        instance.BeginScanRun("run-1", Now);
        provider.Preflight = new WorkloadPreflightResult
        {
            Checks = [],
            ReadyNodeNames = [EligibleNode],
            IneligibleNodeNames = [ExcludedNode],
        };

        await CreateEngine().ReconcileInstanceAsync(instance, CancellationToken.None);

        var applied = provider.LastAppliedSpec;
        applied.Should().NotBeNull("the fan-out apply ran, so the engine handed the provider a scan spec");
        applied!.ScanIneligibleNodeNames.Should().Equal([ExcludedNode],
            "the *skipped, ineligible* report is threaded onto the SpecHash-excluded carrier by the engine");
        applied.ScanReadyNodeNames.Should().Equal([EligibleNode],
            "the fan-out renders exactly the ELIGIBLE names — the set arrives already filtered at the node read");

        applied.ScanReadyNodeNames!.Should().NotContain(ExcludedNode,
            "the whole point: an excluded node gets NO Job, so no pod is left pending in ContainerCreating forever");
        applied.ScanReadyNodeNames!.Intersect(applied.ScanIneligibleNodeNames!).Should().BeEmpty(
            "the eligible and ineligible sets are a PARTITION — no name may appear in both");
        applied.ScanReadyNodeNames!.Should().NotContain(CordonedNode,
            "a node the readiness predicate rejected was never a candidate");
        applied.ScanIneligibleNodeNames!.Should().NotContain(CordonedNode,
            "a cordoned node is NOT *skipped as ineligible* — reporting it would blame its labels for a cordon");
    }

    [Fact]
    public async Task Engine_NoTargets_outcome_message_names_the_nodes_skipped_as_ineligible()
    {
        var instance = SeedScanInstance();
        instance.BeginScanRun("run-1", Now);
        provider.Preflight = new WorkloadPreflightResult
        {
            Checks = [],
            ReadyNodeNames = [],
            IneligibleNodeNames = [ExcludedNode, "node-no-vlan-2"],
        };

        var outcome = await CreateEngine().ReconcileInstanceAsync(instance, CancellationToken.None);

        outcome.Message.Should().Contain("no eligible",
            "the run is still surfaced as NoTargets — the report is additive, not a replacement");
        outcome.Message.Should().Contain(ExcludedNode,
            "the *skipped, ineligible* report names the skipped node(s) on the outcome the operator reads");
        outcome.Message.Should().Contain("node-no-vlan-2");
        provider.ApplyCount.Should().Be(0, "zero eligible nodes ⇒ zero Jobs created");
        instance.HasPendingScanRun.Should().BeFalse("the marker is cleared VACUOUSLY the same pass");
    }

    [Fact]
    public async Task Engine_NoTargets_on_a_genuinely_empty_cluster_reports_NO_skipped_nodes()
    {
        var instance = SeedScanInstance();
        instance.BeginScanRun("run-1", Now);
        provider.Preflight = new WorkloadPreflightResult
        {
            Checks = [],
            ReadyNodeNames = [],
            IneligibleNodeNames = [],
        };

        var outcome = await CreateEngine().ReconcileInstanceAsync(instance, CancellationToken.None);

        outcome.Message.Should().Contain("no eligible");
        outcome.Message.Should().NotContain("skipped as ineligible",
            "an empty excluded set is not a *skipped, ineligible* report");
    }

    private WorkloadOperationsService CreateService()
        => new(repository, intentProvider, bus.Nudge, new ScanClock(Now), opsLog,
            new FixedInputsProvider(BuildInputs()), new SingleProviderRegistry(provider));

    private WorkloadReconcileEngine CreateEngine()
        => new(
            new FixedInputsProvider(BuildInputs()),
            new PassthroughResolver(),
            new SingleProviderRegistry(provider),
            repository,
            new Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch([]),
            new ScanClock(Now),
            NullLogger<WorkloadReconcileEngine>.Instance,
            NSubstitute.Substitute.For<IRetainedVolumeService>(),
            new Reeve.Workloads.Abstractions.NullActiveWorkloadIntentProvider(),
            new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver(),
            new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider()),
            null,
            new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider()),
            new Reeve.Workloads.Abstractions.NoCredentialPort());

    private WorkloadInstance SeedScanInstance()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), PluginTypeId, WorkloadId, Now);
        instance.SetProvider(KubernetesProvider.Id, Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        repository.Add(instance);
        return instance;
    }

    private static WorkloadReconcileInputs BuildInputs() => new()
    {
        Namer = DefaultWorkloadResourceNamer.Instance,
        WorkloadManifest = new WorkloadManifest
        {
            Id = WorkloadId,
            DisplayName = "Node scanner",
            Image = new WorkloadImage { Ref = "registry.example/scanner", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.ScanPerNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        },
        ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal),
        Settings = new WorkloadOrchestratorSettings(),
        Bootstrap = new WorkloadBootstrapContext { InstanceId = Guid.NewGuid(), SecretName = "reeve-wl-secret" },
        EffectiveProvider = KubernetesProvider.Descriptor,
        InstallationId = "install-1",
    };

    private sealed class ScanClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class NoIntentProvider : IActiveWorkloadIntentProvider
    {
        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<WorkloadDesiredSpec?>(null);

        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken)
            => Task.FromResult<Guid?>(null);
    }

    private sealed class FixedInputsProvider(WorkloadReconcileInputs inputs) : IWorkloadReconcileInputsProvider
    {
        public Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => ReconcileInputsAnswers.Of(inputs);
    }

    private sealed class PassthroughResolver : IDesiredSpecResolver
    {
        public Task<WorkloadDesiredSpec> ResolveAsync(
            Guid pluginInstanceId, string workloadId, Func<WorkloadDesiredSpec> computeFresh, CancellationToken cancellationToken)
            => Task.FromResult(computeFresh());
    }

    private sealed class SingleProviderRegistry(IWorkloadProvider provider) : IWorkloadProviderRegistry
    {
        public IWorkloadProvider? TryResolve(WorkloadProviderId kind) => provider;

        public IWorkloadProvider Resolve(WorkloadProviderId kind) => provider;

        public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders => new[] { provider.Provider };

        public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors
            => [.. RegisteredProviders.Select(Describe)];

        public WorkloadProviderDescriptor Describe(WorkloadProviderId kind)
            => TryResolve(kind)?.Descriptor ?? ShippedProviders.For(kind);
    }

    private sealed class RecordingScanProvider : IWorkloadProvider
    {
        public WorkloadProviderDescriptor Descriptor { get; } = new() { Id = KubernetesProvider.Id };

        public WorkloadPreflightResult Preflight { get; set; } = WorkloadPreflightResult.Ok();

        public WorkloadDesiredSpec? LastAppliedSpec { get; private set; }

        public int ApplyCount { get; private set; }

        public Task<WorkloadApplyResult> ApplyAsync(
            WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress,
            CancellationToken cancellationToken = default, bool forceConflicts = false)
        {
            LastAppliedSpec = spec;
            ApplyCount++;
            return Task.FromResult(WorkloadApplyResult.Applied(spec.SpecHash));
        }

        public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true });

        public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(Preflight);

        public Task<WorkloadLogs> GetRecentLogsAsync(
            WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadLogs.Empty());

        public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadAttachmentExistence.Read([]));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> lines = [];

        public string Text => string.Join("\n", lines);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => lines.Add(formatter(state, exception));
    }

    private sealed class ScanRepository : IWorkloadInstanceRepository
    {
        private readonly Dictionary<Guid, WorkloadInstance> store = new();

        public void Add(WorkloadInstance instance) => store[instance.Id] = instance;

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(store.TryGetValue(id, out var i) ? i : null);

        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkloadInstance?>(
                store.Values.FirstOrDefault(i => i.GroupInstanceId == pluginInstanceId && i.WorkloadId == workloadId));

        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.Where(i => i.GroupInstanceId == pluginInstanceId).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.Where(i => i.State == state).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.ToList());

        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(
                store.Values.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count()));

        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { Add(instance); return Task.CompletedTask; }

        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { Add(instance); return Task.CompletedTask; }

        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { store.Remove(instance.Id); return Task.CompletedTask; }
    }
}
