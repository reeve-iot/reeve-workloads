using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Domain.Network;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Testing;
using Xunit;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Core.Tests.Reconcile;

public sealed class ApplyTimeAttachmentPinTests
{
    private const string PluginTypeId = "acme.zwave";
    private const string WorkloadId = "controller";

    private const string IotAttachment = "iot-macvlan";

    private const string CamAttachment = "cam-macvlan";

    private static readonly string[] DeclaredAttachments = [IotAttachment, CamAttachment];

    [Fact]
    public async Task Re_apply_of_an_unchanged_spec_against_an_unchanged_network_converges_and_records_no_reason()
    {
        var harness = new Harness();
        var instance = harness.NewInstance();
        var engine = harness.Build();

        var first = await engine.ReconcileInstanceAsync(instance);
        first.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentConfigDrifted);
        first.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentExistenceUnverifiable);
        instance.AppliedSpecHash.Should().NotBeNull("the first apply really happened");

        var reapply = await engine.ReconcileInstanceAsync(instance);

        reapply.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentConfigDrifted,
            "an unchanged spec against an unchanged network is exactly the case that must NOT park");
        reapply.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentExistenceUnverifiable);
        reapply.PendingApply.Should().BeFalse("nothing is pending, the workload is converged");

        harness.Provider.AttachmentExistenceCallCount.Should().BeGreaterThan(0,
            "the check must actually have looked the attachments up, or 'it converged' proves nothing");
    }

    [Fact]
    public async Task An_edited_network_configuration_does_not_park_the_next_apply_and_launches_no_probe()
    {
        var harness = new Harness();
        var instance = harness.NewInstance();
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        instance.AppliedSpecHash.Should().NotBeNull("the workload is CONVERGED before the network moves under it");
        var appliesBefore = harness.Provider.ApplyCountFor(instance.Id);

        instance.SetAppliedSpecHash(null, DateTimeOffset.UnixEpoch);
        harness.Provider.AttachmentExistenceOverride = _ => WorkloadAttachmentExistence.Read([]);
        var callsBefore = harness.Provider.AttachmentExistenceCallCount;

        var next = await engine.ReconcileInstanceAsync(instance);

        next.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentConfigDrifted,
            "the network's parameters are the operator's business; the same attachment still exists");
        next.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentExistenceUnverifiable);
        next.PendingApply.Should().BeFalse();
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(appliesBefore + 1,
            "the pending spec applies onto the edited network");
        (harness.Provider.AttachmentExistenceCallCount - callsBefore).Should().Be(1,
            "exactly ONE read-only existence check per apply, and above all no probe");
        EngineCollaboratorTypeNames().Should().NotContain(
            n => n.Contains("Probe", StringComparison.OrdinalIgnoreCase),
            "the comparison performs a passive read; the reconcile engine has no route-probe seam to launch one with");
    }

    [Fact]
    public async Task A_pinned_attachment_that_no_longer_exists_parks_drifted_until_it_exists_again()
    {
        var harness = new Harness();
        var instance = harness.NewInstance();
        var engine = harness.Build();
        await engine.ReconcileInstanceAsync(instance);

        instance.SetAppliedSpecHash(null, DateTimeOffset.UnixEpoch);
        harness.Provider.SetNetworkAttachmentMissing(CamAttachment);
        var parked = await engine.ReconcileInstanceAsync(instance);
        var appliesBefore = harness.Provider.ApplyCountFor(instance.Id);

        parked.Reason.Should().Be(WorkloadReconcileReason.AttachmentConfigDrifted);
        parked.PendingApply.Should().BeTrue("the desired spec stays pending while the network is gone");
        parked.Message.Should().Contain(CamAttachment).And.Contain("do not exist");

        harness.Provider.SetNetworkAttachmentMissing(CamAttachment, missing: false);
        var released = await engine.ReconcileInstanceAsync(instance);

        released.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentConfigDrifted);
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(appliesBefore + 1,
            "the attachment is read afresh on every pass, so its return releases the park without a restart");
    }

    [Fact]
    public async Task A_faulting_existence_read_parks_the_distinct_retryable_reason_and_never_proceeds()
    {
        var harness = new Harness();
        var instance = harness.NewInstance();
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        var appliesBefore = harness.Provider.ApplyCountFor(instance.Id);

        harness.Provider.AttachmentExistenceOverride = _ =>
            WorkloadAttachmentExistence.Unreadable("forbidden (test)");

        var parked = await engine.ReconcileInstanceAsync(instance);

        parked.Reason.Should().Be(WorkloadReconcileReason.AttachmentExistenceUnverifiable,
            "a flaky read must be a DISTINCT retryable reason, never mistaken for a real drift");
        parked.PendingApply.Should().BeTrue("retryable: the desired spec stays pending and the next pass retries");
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(appliesBefore,
            "it NEVER proceeds: an unverifiable network is not a verified one");

        harness.Provider.AttachmentExistenceOverride = null;
        var recovered = await engine.ReconcileInstanceAsync(instance);
        recovered.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentExistenceUnverifiable);
        recovered.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentConfigDrifted);
    }

    [Fact]
    public async Task A_throwing_existence_read_parks_the_retryable_reason_rather_than_escaping_the_pass()
    {
        var harness = new Harness();
        var instance = harness.NewInstance();
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        harness.Provider.AttachmentExistenceOverride =
            _ => throw new InvalidOperationException("transport reset mid-read (test)");

        var parked = await engine.ReconcileInstanceAsync(instance);

        parked.Reason.Should().Be(WorkloadReconcileReason.AttachmentExistenceUnverifiable,
            "a provider that breaks its no-throw contract must land on the retryable arm, not crash the pass");
    }

    [Fact]
    public async Task A_workload_with_no_pin_applies_without_reading_the_network()
    {
        var harness = new Harness { Pin = null };
        var instance = harness.NewInstance();
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentConfigDrifted);
        outcome.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentExistenceUnverifiable);
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1);
        harness.Provider.AttachmentExistenceCallCount.Should().Be(0, "nothing is pinned, so nothing is checked");
    }

    [Fact]
    public async Task A_pin_that_cannot_be_read_parks_drifted_and_never_applies()
    {
        var harness = new Harness();
        var instance = harness.NewInstance();
        typeof(WorkloadInstance).GetProperty(nameof(WorkloadInstance.AttachmentPinDocument))!
            .SetValue(instance, new WorkloadAttachmentPinDocument { Unreadable = true });
        var engine = harness.Build();

        var parked = await engine.ReconcileInstanceAsync(instance);

        parked.Reason.Should().Be(WorkloadReconcileReason.AttachmentConfigDrifted);
        parked.PendingApply.Should().BeTrue();
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0);
        instance.AppliedSpecHash.Should().BeNull("the workload never reached the runtime");
    }

    [Fact]
    public async Task A_pin_whose_ordered_attachment_names_differ_parks_drifted()
    {
        var harness = new Harness();
        harness.Pin = harness.Pin! with { AttachmentNames = [CamAttachment, IotAttachment] };
        var instance = harness.NewInstance();
        var engine = harness.Build();

        var parked = await engine.ReconcileInstanceAsync(instance);

        parked.Reason.Should().Be(WorkloadReconcileReason.AttachmentConfigDrifted,
            "mode and ORDERED names must match; a sorted comparison would pass this");
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0);
    }

    [Fact]
    public async Task A_pin_for_another_mode_parks_drifted()
    {
        var harness = new Harness();
        harness.Pin = harness.Pin! with { Mode = WorkloadNetworkMode.HostNetwork };
        var instance = harness.NewInstance();
        var engine = harness.Build();

        var parked = await engine.ReconcileInstanceAsync(instance);

        parked.Reason.Should().Be(WorkloadReconcileReason.AttachmentConfigDrifted);
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0);
    }

    [Fact]
    public async Task A_manual_provider_performs_no_apply_time_read_even_with_a_pin()
    {
        var harness = new Harness { EffectiveProvider = ManualProvider.Descriptor };
        var instance = harness.NewInstance();
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentConfigDrifted);
        outcome.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentExistenceUnverifiable);
        harness.Provider.AttachmentExistenceCallCount.Should().Be(0, "Manual performs NO apply-time read at all");
    }

    [Fact]
    public async Task A_provider_that_answers_not_applicable_on_a_non_Manual_provider_parks_drifted()
    {
        var harness = new Harness();
        var instance = harness.NewInstance();
        var engine = harness.Build();
        harness.Provider.AttachmentExistenceOverride = _ =>
            WorkloadAttachmentExistence.NotApplicable("cannot be asked (a default implementation)");

        var parked = await engine.ReconcileInstanceAsync(instance);

        parked.Reason.Should().Be(WorkloadReconcileReason.AttachmentConfigDrifted,
            "a provider that answers 'not applicable' told us nothing about a network the host pinned");
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(0, "and it never proceeds");
    }

    [Fact]
    public async Task A_first_apply_with_a_matching_pin_proceeds()
    {
        var harness = new Harness();
        var instance = harness.NewInstance();
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentConfigDrifted);
        outcome.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentExistenceUnverifiable);
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1, "the comparison lets a matching network through");
        harness.Provider.LastAppliedSpec(instance.Id)!.Network!.Attachments
            .Should().Equal(DeclaredAttachments, "in DECLARED order");
    }

    [Fact]
    public async Task A_non_operatorNetwork_spec_performs_no_apply_time_network_read_even_with_a_pin()
    {
        var harness = new Harness { DeclareNetwork = false };
        var instance = harness.NewInstance();
        var engine = harness.Build();

        var outcome = await engine.ReconcileInstanceAsync(instance);

        outcome.Reason.Should().NotBe(WorkloadReconcileReason.AttachmentConfigDrifted,
            "a plain workload must not be parked by a network comparison it is not in scope for");
        harness.Provider.AttachmentExistenceCallCount.Should().Be(0);
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1);
    }

    [Fact]
    public async Task The_apply_time_existence_read_is_one_read_per_pass_idempotent_and_starts_nothing()
    {
        var harness = new Harness();
        var instance = harness.NewInstance();
        var engine = harness.Build();

        await engine.ReconcileInstanceAsync(instance);
        harness.Provider.AttachmentExistenceCallCount.Should().Be(1);

        await engine.ReconcileInstanceAsync(instance);
        await engine.ReconcileInstanceAsync(instance);
        harness.Provider.AttachmentExistenceCallCount.Should().Be(3, "one read per pass, never more");

        var a = await harness.Provider.GetAttachmentExistenceAsync(DeclaredAttachments);
        var b = await harness.Provider.GetAttachmentExistenceAsync(DeclaredAttachments);
        b.Should().Be(a, "idempotent: reading again observes the same existence");
        harness.Provider.ApplyCountFor(instance.Id).Should().Be(1,
            "the extra passes converged, and the read itself created, mutated and started nothing");
    }

    private static IReadOnlyList<string> EngineCollaboratorTypeNames() =>
        typeof(WorkloadReconcileEngine).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private sealed class Harness
    {
        public WorkloadAttachmentPin? Pin { get; set; } = new()
        {
            Mode = WorkloadNetworkMode.OperatorNetwork,
            AttachmentNames = DeclaredAttachments,
        };

        public bool DeclareNetwork { get; init; } = true;

        public WorkloadProviderDescriptor EffectiveProvider { get; init; } = KubernetesProvider.Descriptor;

        public InMemoryWorkloadProvider Provider { get; private set; } = null!;

        public WorkloadInstance NewInstance()
        {
            var instance = WorkloadInstance.Create(
                Guid.NewGuid(), Guid.NewGuid(), PluginTypeId, WorkloadId, DateTimeOffset.UnixEpoch);
            instance.PinAttachment(Pin, DateTimeOffset.UnixEpoch);
            return instance;
        }

        public WorkloadReconcileEngine Build()
        {
            Provider = new InMemoryWorkloadProvider(EffectiveProvider);

            return new WorkloadReconcileEngine(
                new FakeInputsProvider(BuildInputs),
                new PassthroughResolver(),
                new SingleProviderRegistry(EffectiveProvider.Id, Provider),
                new RecordingRepository(),
                new FakeHostHealthContributor().Dispatch(),
                new FixedClock(),
                NullLogger<WorkloadReconcileEngine>.Instance,
                NSubstitute.Substitute.For<IRetainedVolumeService>(),
                new Reeve.Workloads.Abstractions.NullActiveWorkloadIntentProvider(), new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver(), new Reeve.Workloads.Core.WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), null,
                new Reeve.Workloads.Core.UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());
        }

        private WorkloadManifest Manifest() => new()
        {
            Id = WorkloadId,
            DisplayName = "Z-Wave JS",
            Image = new WorkloadImage { Ref = "registry.example/zwave-js", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.SingleNode,
            Network = DeclareNetwork ? new WorkloadNetwork { HostNetwork = true } : null,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
        };

        private IReadOnlyDictionary<string, string?> OperatorParameters() => DeclareNetwork
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [WorkloadNetworkAttachmentGrammar.NetworkModeParameter] = WorkloadNetworkAttachmentGrammar.OperatorNetworkMode,
                [WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter] = string.Join(",", DeclaredAttachments),
            }
            : new Dictionary<string, string?>(StringComparer.Ordinal);

        private WorkloadReconcileInputs BuildInputs()
        {
            var manifest = Manifest();
            var operatorValues = OperatorParameters();

            var resolved = WorkloadParameterResolver.Resolve(manifest, operatorValues, EffectiveProvider);
            var nonNull = resolved
                .Where(kv => kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal);

            return new WorkloadReconcileInputs
            {
                Namer = DefaultWorkloadResourceNamer.Instance,
                WorkloadManifest = manifest,
                ResolvedParameters = operatorValues,
                Settings = new WorkloadOrchestratorSettings(),
                Bootstrap = new WorkloadBootstrapContext { InstanceId = Guid.NewGuid(), SecretName = "reeve-wl-secret" },
                EffectiveProvider = EffectiveProvider,
                InstallationId = "install-1",
            };
        }
    }

    private sealed class FakeInputsProvider(Func<WorkloadReconcileInputs> factory) : IWorkloadReconcileInputsProvider
    {
        public Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => ReconcileInputsAnswers.Of(factory());
    }

    private sealed class PassthroughResolver : IDesiredSpecResolver
    {
        public Task<WorkloadDesiredSpec> ResolveAsync(
            Guid pluginInstanceId, string workloadId, Func<WorkloadDesiredSpec> computeFresh, CancellationToken cancellationToken)
            => Task.FromResult(computeFresh());
    }

    private sealed class SingleProviderRegistry(WorkloadProviderId kind, IWorkloadProvider provider) : IWorkloadProviderRegistry
    {
        public IWorkloadProvider? TryResolve(WorkloadProviderId p) => p == kind ? provider : null;

        public IWorkloadProvider Resolve(WorkloadProviderId p) => TryResolve(p) ?? throw new WorkloadProviderNotRegisteredException(p);

        public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders => [kind];

        public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors
            => [.. RegisteredProviders.Select(Describe)];

        public WorkloadProviderDescriptor Describe(WorkloadProviderId kind)
            => TryResolve(kind)?.Descriptor ?? ShippedProviders.For(kind);
    }

    private sealed class RecordingRepository : IWorkloadInstanceRepository
    {
        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<WorkloadInstance?>(null);
        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default) => Task.FromResult<WorkloadInstance?>(null);
        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkloadInstance>>([]);
        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkloadInstance>>([]);
        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkloadInstance>>([]);
        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(new Dictionary<WorkloadInstanceState, int>());
        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }


    private sealed class FixedClock : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    }
}
