using Reeve.Workloads.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadScanTriggerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 34, 56, 789, TimeSpan.Zero);
    private static readonly Guid InstanceId = Guid.Parse("4d7b2e90-6a1c-4358-9e2f-0b3c8d5a7614");
    private static readonly Guid GroupInstanceId = Guid.Parse("aabbccdd-eeff-4011-8223-445566778899");
    private const string NodesReadableCheckId = "scan-nodes-readable";

    private readonly IWorkloadInstanceRepository repository = Substitute.For<IWorkloadInstanceRepository>();
    private readonly IActiveWorkloadIntentProvider intentProvider = Substitute.For<IActiveWorkloadIntentProvider>();
    private readonly IWorkloadClusterBus clusterBus = Substitute.For<IWorkloadClusterBus>();
    private readonly RecordingReconcileTrigger localTrigger = new();
    private readonly IWorkloadClock clock = Substitute.For<IWorkloadClock>();
    private readonly FakeInputsProvider inputsProvider = new();
    private readonly IWorkloadProviderRegistry registry = Substitute.For<IWorkloadProviderRegistry>();
    private readonly IWorkloadProvider provider = Substitute.For<IWorkloadProvider>();

    private int writes;

    public WorkloadScanTriggerTests()
    {
        clock.UtcNow.Returns(Now);
        inputsProvider.Manifest = ScanManifest();
        registry.TryResolve(Arg.Any<WorkloadProviderId>()).Returns(provider);
        registry.Describe(Arg.Any<WorkloadProviderId>()).Returns(KubernetesProvider.Descriptor);
        provider.GetStatusAsync(Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkloadStatus { Phase = WorkloadProviderPhase.Ready, ProviderReady = true }));
        provider.PreflightAsync(Arg.Any<WorkloadDesiredSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Preflight(ready: ["node-a", "node-b"])));
        repository.When(r => r.UpdateAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>()))
            .Do(_ => writes++);
    }

    private WorkloadOperationsService CreateService() => new(
        repository, intentProvider, new WorkloadReconcileNudge(clusterBus, localTrigger), clock,
        NullLogger<WorkloadOperationsService>.Instance, inputsProvider, registry);

    private WorkloadInstance Seed(
        WorkloadInstanceState state = WorkloadInstanceState.Healthy,
        WorkloadProviderDescriptor? declared = null)
    {
        var bound = declared ?? KubernetesProvider.Descriptor;
        registry.Describe(bound.Id).Returns(bound);

        var instance = WorkloadInstance.Create(
            InstanceId, GroupInstanceId, "acme.survey", "node-scan", Now - TimeSpan.FromDays(1));
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.SetProvider(bound.Id, Now);
        instance.SetAppliedSpecHash("sha256:applied", Now);
        if (state != WorkloadInstanceState.Healthy)
        {
            instance.TransitionTo(state, WorkloadStateReason.HealthLost, null, Now);
        }

        repository.GetByIdAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WorkloadInstance?>(instance));
        return instance;
    }

    private static WorkloadManifest ScanManifest(WorkloadKind kind = WorkloadKind.ScanPerNode) => new()
    {
        Id = "node-scan",
        DisplayName = "Node Survey",
        Image = new WorkloadImage { Ref = "registry.example/survey", Digest = "sha256:" + new string('b', 64) },
        Kind = kind,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
    };

    private static WorkloadPreflightResult Preflight(
        IReadOnlyList<string>? ready = null,
        IReadOnlyList<string>? ineligible = null,
        PreflightCheck? blocking = null) => new()
        {
            Checks = blocking is null ? [] : [blocking],
            ReadyNodeNames = ready,
            IneligibleNodeNames = ineligible,
        };

    private static PreflightCheck Blocking(string id, string message, string? fixSnippet = null) => new()
    {
        Id = id,
        Severity = PreflightSeverity.Error,
        Passed = false,
        Message = message,
        FixSnippet = fixSnippet,
    };

    private Task<WorkloadOperationResult> ScanAsync(CancellationToken cancellationToken = default)
        => CreateService().ScanAsync(InstanceId, cancellationToken);

    private async Task AssertMarkerUnsetAsync(WorkloadInstance instance)
    {
        instance.HasPendingScanRun.Should().BeFalse("a refused trigger must not leave a pending-run marker");
        writes.Should().Be(0);
        await clusterBus.DidNotReceiveWithAnyArgs().PublishAsync(default!, default(object)!, default);
    }

    [Fact]
    public async Task An_unknown_instance_is_not_found()
    {
        repository.GetByIdAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WorkloadInstance?>(null));

        (await ScanAsync()).Outcome.Should().Be(WorkloadOperationOutcome.NotFound);
    }

    [Fact]
    public async Task A_host_refusal_refuses_the_scan_with_the_hosts_reason_and_sets_no_marker()
    {
        var instance = Seed();
        inputsProvider.Refusal = "host says no";

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.HostRefused);
        result.Error.Should().Contain("host says no");
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task An_active_update_intent_queues_the_scan_behind_it()
    {
        var intentId = Guid.Parse("99887766-5544-4332-9110-fedcba987654");
        var instance = Seed();
        intentProvider.GetActiveIntentIdAsync(GroupInstanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(intentId));

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.Conflict);
        result.ActiveIntentId.Should().Be(intentId);
        await AssertMarkerUnsetAsync(instance);
    }

    [Theory]
    [InlineData(WorkloadInstanceState.ExternallyManaged)]
    [InlineData(WorkloadInstanceState.Switching)]
    [InlineData(WorkloadInstanceState.Terminating)]
    [InlineData(WorkloadInstanceState.Unknown)]
    public async Task A_frozen_or_immutable_instance_cannot_be_scanned(WorkloadInstanceState state)
    {
        var instance = Seed();
        instance.TransitionTo(state, WorkloadStateReason.ExternalHealthObserved, null, Now);

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.NotMutable);
        result.BlockingState.Should().Be(state);
        await AssertMarkerUnsetAsync(instance);
    }

    [Theory]
    [InlineData(WorkloadStateReason.OperatorPaused)]
    [InlineData(WorkloadStateReason.SuspendedByHost)]
    public async Task A_suspended_instance_is_refused_rather_than_queued_behind_the_resume(WorkloadStateReason held)
    {
        var instance = Seed();
        instance.TransitionTo(WorkloadInstanceState.Suspended, held, null, Now);

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.NotMutable,
            "a run that cannot start must be refused outright, not reported as a Success the operator will wait on");
        result.BlockingState.Should().Be(WorkloadInstanceState.Suspended,
            "the refusal names the hold, so the operator is told to resume rather than left guessing");
        await provider.DidNotReceiveWithAnyArgs().PreflightAsync(default!, default);
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task A_workload_that_is_no_longer_declared_cannot_be_scanned()
    {
        var instance = Seed();
        inputsProvider.Manifest = null;

        (await ScanAsync()).Outcome.Should().Be(WorkloadOperationOutcome.NotMutable);
        await AssertMarkerUnsetAsync(instance);
    }

    [Theory]
    [InlineData(WorkloadKind.Service)]
    [InlineData(WorkloadKind.SingleNode)]
    public async Task A_workload_that_is_not_scan_per_node_is_refused_before_any_enumeration(WorkloadKind kind)
    {
        var instance = Seed();
        inputsProvider.Manifest = ScanManifest(kind);

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.NotMutable);
        await provider.DidNotReceiveWithAnyArgs().PreflightAsync(default!, default);
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task An_instance_whose_provider_is_not_registered_cannot_be_scanned()
    {
        var instance = Seed();
        registry.TryResolve(Arg.Any<WorkloadProviderId>()).Returns((IWorkloadProvider?)null);

        (await ScanAsync()).Outcome.Should().Be(WorkloadOperationOutcome.NotMutable);
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task A_scan_on_a_provider_that_cannot_scan_is_refused_by_name_and_touches_nothing()
    {
        var instance = Seed(declared: ManualProvider.Descriptor);

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.ProviderCannotScan,
            "an empty target set would report a cluster with no Ready nodes instead of a provider that cannot scan");
        result.Error.Should().Contain(ManualProvider.Descriptor.Label,
            "the refusal names the provider it is about, so the operator is not left guessing which one");
        instance.AppliedSpecHash.Should().Be("sha256:applied", "a refused scan applies nothing");
        await provider.DidNotReceiveWithAnyArgs().PreflightAsync(default!, default);
        await provider.DidNotReceiveWithAnyArgs().GetStatusAsync(default, default);
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task A_connected_manual_scan_is_refused_because_the_provider_cannot_scan_not_as_disowned()
    {
        var instance = Seed(declared: ManualProvider.Descriptor);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.ProviderCannotScan);
        await AssertMarkerUnsetAsync(instance);
    }

    [Theory]
    [InlineData(WorkloadKind.Service)]
    [InlineData(WorkloadKind.SingleNode)]
    public async Task A_non_scan_workload_on_a_provider_that_cannot_scan_is_still_refused_by_kind(WorkloadKind kind)
    {
        var instance = Seed(declared: ManualProvider.Descriptor);
        inputsProvider.Manifest = ScanManifest(kind);

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.NotMutable);
        await provider.DidNotReceiveWithAnyArgs().PreflightAsync(default!, default);
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task A_pending_marker_does_not_mask_the_provider_refusal()
    {
        var instance = Seed(declared: ManualProvider.Descriptor);
        instance.BeginScanRun("scan-20260910120000000", Now - TimeSpan.FromMinutes(1));

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.ProviderCannotScan);
        instance.PendingScanRunId.Should().Be("scan-20260910120000000", "the refusal clears nothing");
        instance.AppliedSpecHash.Should().Be("sha256:applied");
        writes.Should().Be(0);
        await clusterBus.DidNotReceiveWithAnyArgs().PublishAsync(default!, default(object)!, default);
        await provider.DidNotReceiveWithAnyArgs().GetStatusAsync(default, default);
    }

    [Theory]
    [MemberData(nameof(MaterialisingProviders))]
    public async Task A_scan_with_no_nodes_still_answers_NoTargets_on_a_provider_that_can_scan(
        WorkloadProviderDescriptor declared)
    {
        var instance = Seed(declared: declared);
        provider.PreflightAsync(Arg.Any<WorkloadDesiredSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Preflight(ready: [])));

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.NoTargets);
        await AssertMarkerUnsetAsync(instance);
    }

    [Theory]
    [MemberData(nameof(MaterialisingProviders))]
    public async Task A_scan_with_targets_still_starts_a_run_on_a_provider_that_can_scan(
        WorkloadProviderDescriptor declared)
    {
        var instance = Seed(declared: declared);

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        instance.HasPendingScanRun.Should().BeTrue();
        writes.Should().Be(1);
    }

    [Fact]
    public async Task A_non_scan_operation_on_a_provider_that_cannot_scan_is_unchanged()
    {
        var instance = Seed(WorkloadInstanceState.Suspended, declared: ManualProvider.Descriptor);

        var result = await CreateService().ResumeAsync(InstanceId);

        result.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        instance.AppliedSpecHash.Should().BeNull("a resume forces a fresh apply here as on any provider");
        await clusterBus.Received(1).PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            Arg.Is<WorkloadReconcileRequestMessage>(m => m.InstanceId == InstanceId && m.Reason == "workload-resume"),
            Arg.Any<CancellationToken>());
    }

    public static TheoryData<WorkloadProviderDescriptor> MaterialisingProviders => new()
    {
        KubernetesProvider.Descriptor,
        DockerProvider.Descriptor,
    };

    [Fact]
    public async Task A_pending_marker_refuses_a_second_run_without_asking_the_provider()
    {
        var instance = Seed();
        instance.BeginScanRun("scan-20260910120000000", Now - TimeSpan.FromMinutes(1));

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.ScanAlreadyInProgress);
        instance.PendingScanRunId.Should().Be("scan-20260910120000000", "the first run's id must survive intact");
        await provider.DidNotReceiveWithAnyArgs().GetStatusAsync(default, default);
        writes.Should().Be(0);
    }

    [Fact]
    public async Task A_run_still_active_on_the_provider_refuses_a_second_run()
    {
        var instance = Seed();
        provider.GetStatusAsync(Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkloadStatus { Phase = WorkloadProviderPhase.Running, ProviderReady = false }));

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.ScanAlreadyInProgress);
        await AssertMarkerUnsetAsync(instance);
    }

    [Theory]
    [InlineData(WorkloadProviderPhase.Ready)]
    [InlineData(WorkloadProviderPhase.NotFound)]
    [InlineData(WorkloadProviderPhase.Failed)]
    public async Task A_finished_run_does_not_count_as_in_flight(WorkloadProviderPhase phase)
    {
        Seed();
        provider.GetStatusAsync(Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkloadStatus { Phase = phase, ProviderReady = false }));

        (await ScanAsync()).Outcome.Should().Be(WorkloadOperationOutcome.Success);
    }

    [Fact]
    public async Task A_nodes_readability_block_fails_loud_as_its_own_outcome()
    {
        var instance = Seed();
        provider.PreflightAsync(Arg.Any<WorkloadDesiredSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Preflight(blocking: Blocking(NodesReadableCheckId, "nodes list forbidden"))));

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.NodesUnreadable);
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task Another_blocking_check_fails_loud_carrying_its_own_message_and_fix()
    {
        var instance = Seed();
        provider.PreflightAsync(Arg.Any<WorkloadDesiredSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Preflight(
                blocking: Blocking("scan-image-pullable", "the scan image cannot be pulled", "imagePullSecrets: [...]"))));

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.PreflightFailed);
        result.PreflightFailureMessage.Should().Be("the scan image cannot be pulled");
        result.PreflightFixSnippet.Should().Be("imagePullSecrets: [...]");
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task An_empty_cluster_mints_no_run()
    {
        var instance = Seed();
        provider.PreflightAsync(Arg.Any<WorkloadDesiredSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Preflight(ready: [])));

        (await ScanAsync()).Outcome.Should().Be(WorkloadOperationOutcome.NoTargets);
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task A_cluster_whose_ready_nodes_are_all_ineligible_mints_no_run()
    {
        var instance = Seed();
        provider.PreflightAsync(Arg.Any<WorkloadDesiredSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Preflight(ready: [], ineligible: ["node-c"])));

        (await ScanAsync()).Outcome.Should().Be(WorkloadOperationOutcome.NoTargets);
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task A_provider_that_reports_no_ready_node_list_at_all_mints_no_run()
    {
        var instance = Seed();
        provider.PreflightAsync(Arg.Any<WorkloadDesiredSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Preflight(ready: null)));

        (await ScanAsync()).Outcome.Should().Be(WorkloadOperationOutcome.NoTargets);
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task A_desired_spec_that_cannot_be_computed_is_a_clean_refusal()
    {
        var instance = Seed();
        inputsProvider.ComputeFault = new WorkloadParameterResolutionException("hostPath", "refused by the path allowlist");

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.NotMutable);
        await AssertMarkerUnsetAsync(instance);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_degrading_to_a_config_refusal()
    {
        Seed();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = async () => await ScanAsync(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_started_run_sets_the_marker_and_leaves_the_deployment_alone()
    {
        var instance = Seed();

        var result = await ScanAsync();

        result.Outcome.Should().Be(WorkloadOperationOutcome.Success);
        instance.HasPendingScanRun.Should().BeTrue();
        instance.PendingScanRunId.Should().NotBeNullOrWhiteSpace();
        instance.AppliedSpecHash.Should().Be("sha256:applied", "a scan is not a redeploy");
        instance.State.Should().Be(WorkloadInstanceState.Healthy, "a scan is not a state transition");
        writes.Should().Be(1);
        await clusterBus.Received(1).PublishAsync(
            WorkloadClusterChannels.ReconcileRequest,
            Arg.Is<WorkloadReconcileRequestMessage>(m => m.InstanceId == InstanceId && m.Reason == "workload-scan-trigger"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_minted_run_id_is_dns_safe_and_orders_by_creation()
    {
        var earlier = Seed();
        await ScanAsync();
        var earlierId = earlier.PendingScanRunId!;

        earlierId.Should().MatchRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$").And.HaveLength(earlierId.Length);
        earlierId.Length.Should().BeLessThanOrEqualTo(63);

        clock.UtcNow.Returns(Now.AddSeconds(1));
        var later = Seed();
        await ScanAsync();

        string.CompareOrdinal(later.PendingScanRunId, earlierId).Should().BePositive(
            "a later run must sort after an earlier one");
    }

    [Fact]
    public async Task Ineligible_nodes_do_not_block_a_run_that_has_eligible_ones()
    {
        var instance = Seed();
        provider.PreflightAsync(Arg.Any<WorkloadDesiredSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Preflight(ready: ["node-a"], ineligible: ["node-c", "node-d"])));

        (await ScanAsync()).Outcome.Should().Be(WorkloadOperationOutcome.Success);
        instance.HasPendingScanRun.Should().BeTrue();
    }

    private sealed class FakeInputsProvider : IWorkloadReconcileInputsProvider
    {
        public WorkloadManifest? Manifest { get; set; }

        public Exception? ComputeFault { get; set; }

        public string? Refusal { get; set; }

        public Task<WorkloadReconcileInputsResult> GetInputsAsync(
            WorkloadInstance instance, CancellationToken cancellationToken = default)
            => ReconcileInputsAnswers.Of(Manifest is null
                ? null
                : new WorkloadReconcileInputs
                {
                    Namer = DefaultWorkloadResourceNamer.Instance,
                    WorkloadManifest = Manifest,
                    ResolvedParameters = ComputeFault is null
                        ? instance.GetResolvedParameters()
                        : new ThrowingParameters(ComputeFault),
                    Settings = new WorkloadOrchestratorSettings(),
                    Bootstrap = new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "reeve-wl-secret" },
                    EffectiveProvider = KubernetesProvider.Descriptor,
                    InstallationId = "test-install",
                }, Refusal);
    }

    private sealed class ThrowingParameters(Exception fault) : IReadOnlyDictionary<string, string?>
    {
        public string? this[string key] => throw fault;

        public IEnumerable<string> Keys => throw fault;

        public IEnumerable<string?> Values => throw fault;

        public int Count => throw fault;

        public bool ContainsKey(string key) => throw fault;

        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => throw fault;

        public bool TryGetValue(string key, out string? value) => throw fault;

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw fault;
    }
}
