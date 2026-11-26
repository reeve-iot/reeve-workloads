using Reeve.Workloads.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Core.Tests.Reconcile;

public sealed class WorkloadReconcileEngineOwnedTests
{
    private const string GroupId = "acme.plugin";
    private const string WorkloadId = "web";
    private static readonly DateTimeOffset At = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_undeclared_workload_is_a_no_op_and_never_a_teardown()
    {
        var instance = Instance();
        var inputs = Substitute.For<IWorkloadReconcileInputsProvider>();
        inputs.GetInputsAsync(instance, Arg.Any<CancellationToken>())
            .Returns(WorkloadReconcileInputsResult.NotDeclared);
        var provider = ProviderThatWouldNotice();

        var outcome = await Engine(inputs: inputs, provider: provider).ReconcileInstanceAsync(instance);

        outcome.Action.Should().Be(WorkloadReconcileAction.NoOp);
        outcome.Reason.Should().Be(WorkloadReconcileReason.WorkloadNotDeclared);
        await provider.DidNotReceive().TeardownAsync(Arg.Any<WorkloadRef>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await provider.DidNotReceive().ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task No_registered_provider_is_a_no_op_that_leaves_the_state_alone()
    {
        var instance = Instance();
        var registry = Substitute.For<IWorkloadProviderRegistry>();
        registry.Describe(Arg.Any<WorkloadProviderId>())
            .Returns(call => ShippedProviders.For(call.Arg<WorkloadProviderId>()));
        registry.TryResolve(Arg.Any<WorkloadProviderId>()).Returns((IWorkloadProvider?)null);

        var outcome = await Engine(registry: registry).ReconcileInstanceAsync(instance);

        outcome.Action.Should().Be(WorkloadReconcileAction.NoOp);
        outcome.Reason.Should().Be(WorkloadReconcileReason.NoProviderAvailable);
        outcome.ResultingState.Should().Be(instance.State,
            "a host that registered no provider must not have its instances driven anywhere");
    }

    [Fact]
    public async Task A_spec_that_cannot_be_computed_holds_configuration_and_touches_no_runtime()
    {
        var instance = Instance();
        var provider = ProviderThatWouldNotice();

        var resolver = Substitute.For<IDesiredSpecResolver>();
        resolver.ResolveAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Func<WorkloadDesiredSpec>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkloadParameterResolutionException("hostPath", "refused by the path allowlist"));

        var outcome = await Engine(resolver: resolver, provider: provider).ReconcileInstanceAsync(instance);

        outcome.Reason.Should().BeOneOf(
            WorkloadReconcileReason.ConfigurationRequired, WorkloadReconcileReason.DesiredSpecComputeFailed);
        outcome.Action.Should().NotBe(WorkloadReconcileAction.Applied);
        await provider.DidNotReceive().TeardownAsync(Arg.Any<WorkloadRef>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await provider.DidNotReceive().ApplyAsync(
            Arg.Any<WorkloadDesiredSpec>(), Arg.Any<IProgress<WorkloadApplyProgress>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<bool>());
        await provider.DidNotReceive().SuspendAsync(Arg.Any<WorkloadRef>(), Arg.Any<CancellationToken>());
    }

    private static IWorkloadProvider ProviderThatWouldNotice()
    {
        var provider = Substitute.For<IWorkloadProvider>();
        provider.Provider.Returns(ManualProvider.Id);
        return provider;
    }

    private static WorkloadReconcileEngine Engine(
        IWorkloadReconcileInputsProvider? inputs = null,
        IWorkloadProviderRegistry? registry = null,
        IWorkloadProvider? provider = null,
        IDesiredSpecResolver? resolver = null)
    {
        if (registry is null)
        {
            registry = Substitute.For<IWorkloadProviderRegistry>();
            registry.Describe(Arg.Any<WorkloadProviderId>())
                .Returns(call => ShippedProviders.For(call.Arg<WorkloadProviderId>()));
            registry.TryResolve(Arg.Any<WorkloadProviderId>()).Returns(provider ?? ProviderThatWouldNotice());
        }

        var repository = Substitute.For<IWorkloadInstanceRepository>();
        var health = Substitute.For<IWorkloadHealthContributor>();
        health.ObserveAsync(Arg.Any<WorkloadHealthObservation>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkloadHealthContribution()));

        return new WorkloadReconcileEngine(
            inputs ?? Inputs(),
            resolver ?? Substitute.For<IDesiredSpecResolver>(),
            registry,
            repository,
            new Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch([health]),
            new FixedClock(),
            NullLogger<WorkloadReconcileEngine>.Instance,
            Substitute.For<IRetainedVolumeService>(),
            Substitute.For<IActiveWorkloadIntentProvider>(), new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver(), new Reeve.Workloads.Core.WorkloadParameterSecretCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), null,
            new UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), new Reeve.Workloads.Abstractions.NoCredentialPort());
    }

    private static IWorkloadReconcileInputsProvider Inputs(
        IReadOnlyDictionary<string, string?>? parameters = null)
    {
        var port = Substitute.For<IWorkloadReconcileInputsProvider>();
        port.GetInputsAsync(Arg.Any<WorkloadInstance>(), Arg.Any<CancellationToken>())
            .Returns(ReconcileInputsAnswers.Of(new WorkloadReconcileInputs
            {
                Namer = DefaultWorkloadResourceNamer.Instance,
                WorkloadManifest = Manifest(),
                ResolvedParameters = parameters ?? new Dictionary<string, string?>(StringComparer.Ordinal),
                Settings = new WorkloadOrchestratorSettings { Provider = ManualProvider.Id },
                Bootstrap = new WorkloadBootstrapContext { InstanceId = Guid.NewGuid(), SecretName = "reeve-wl-secret" },
                EffectiveProvider = ManualProvider.Descriptor,
                ProductImages = Images(),
            }));
        return port;
    }

    private static ProductImageOptions Images()
    {
        var options = new ProductImageOptions();
        options.Components["controller"] = new ProductImageEntry
        {
            Ref = "example.test/controller",
            Digest = "sha256:" + new string('1', 64),
        };
        return options;
    }

    private static WorkloadManifest Manifest() => new()
    {
        Id = WorkloadId,
        DisplayName = WorkloadId,
        Image = new WorkloadImage { Component = "controller" },
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
    };

    private static WorkloadInstance Instance()
        => WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, At);

    private sealed class FixedClock : IWorkloadClock
    {
        public DateTimeOffset UtcNow => At;
    }
}
