using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Tests.Update;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Reeve.Workloads.DependencyInjection;
using Reeve.Workloads.Providers.Manual;
using Xunit;
using static Reeve.Workloads.Core.Tests.Update.WorkloadUpdatePlanBuilder;

namespace Reeve.Workloads.Core.Tests.Seams;

public sealed class ObservedVersionTests
{
    private static readonly Guid Gateway = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bridge = new("aaaaaaaa-0000-0000-0000-000000000002");

    private static WorkloadUpdatePlan TwoModes() => Plan(InstanceGuid,
    [
        Item("gateway", Gateway, "sha-g-new", "sha-g-old", ManualProvider.Id),
        Item("bridge", Bridge, "sha-b-new", "sha-b-old", ManualProvider.Id) with
        {
            Certification = WorkloadHealthGateCertification.ProviderReady,
            PreviousCertification = WorkloadHealthGateCertification.ProviderReady,
        },
    ]);

    private static async Task<(OperatorRedeployStage Stage, WorkloadUpdateStageContext Context)> RigAsync(
        IObservedWorkloadVersionReader reader)
    {
        var plan = TwoModes();
        var harness = new OrchestratorHarness(plan, providerKind: ManualProvider.Id);
        var intent = await harness.StageManualAsync(plan);
        var stage = new OperatorRedeployStage(
            harness.Registry,
            reader,
            harness.Presence.Dispatch(),
            new SpyRepairsFactory(harness.Issues),
            harness.Clock,
            NullLogger<OperatorRedeployStage>.Instance);
        return (stage, WorkloadUpdateStageContexts.For(intent, plan));
    }

    private sealed class RecordingReader(string? answer) : IObservedWorkloadVersionReader
    {
        public List<Guid> Asked { get; } = [];

        public Task<string?> GetObservedAppVersionAsync(Guid instanceId, CancellationToken cancellationToken = default)
        {
            Asked.Add(instanceId);
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task The_reader_is_asked_whatever_the_workloads_mode()
    {
        var plan = TwoModes();
        var reader = new RecordingReader(plan.ToVersion);
        var (stage, context) = await RigAsync(reader);

        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Done, "both report the target");
        reader.Asked.Should().Equal([Gateway, Bridge],
            "a provider-ready workload is asked exactly as a host-evidence one is");
    }

    [Fact]
    public async Task With_no_reader_registered_every_read_is_null_and_the_update_parks()
    {
        var builder = new ServiceCollection()
            .AddReeveWorkloads(new ConfigurationBuilder().Build())
            .AddEngine();
        IServiceCollection services = new ServiceCollection();
        services.Add(builder.Services.Last(d => d.ServiceType == typeof(IObservedWorkloadVersionReader)));
        using var container = services.BuildServiceProvider();
        var reader = container.GetRequiredService<IObservedWorkloadVersionReader>();

        (await reader.GetObservedAppVersionAsync(Gateway)).Should().BeNull();

        var (stage, context) = await RigAsync(reader);
        var outcome = await stage.UpAsync(context, CancellationToken.None);

        outcome.Kind.Should().Be(StageOutcomeKind.Working,
            "null is never 'caught up', so a wholly-manual update waits for the operator or an abandon");
    }
}
