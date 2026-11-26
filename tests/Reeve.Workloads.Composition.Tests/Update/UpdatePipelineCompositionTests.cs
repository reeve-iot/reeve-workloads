using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Update.Stages;
using Reeve.Workloads.DependencyInjection;
using Reeve.Workloads.Testing;

namespace Reeve.Workloads.Composition.Tests.Update;

public sealed class UpdatePipelineCompositionTests
{
    private const string HostStageId = "stage.host.plugin-roll";

    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    [Fact]
    public void The_engine_registers_the_six_core_stages_and_resolves_them_in_the_declared_order()
    {
        using var provider = TheEnginesPipeline(host: null);
        using var scope = provider.CreateScope();

        var pipeline = scope.ServiceProvider.GetRequiredService<Pipeline<WorkloadUpdateStageContext>>();

        pipeline.Order.Should().Equal(
            WorkloadUpdateStages.Params,
            WorkloadUpdateStages.Sidecar,
            WorkloadUpdateStages.OperatorRedeploy,
            WorkloadUpdateStages.HealthGate,
            WorkloadUpdateStages.CommitVersion,
            WorkloadUpdateStages.CommitParams);
    }

    [Fact]
    public void A_host_stage_registered_beside_the_engine_lands_between_the_two_commits()
    {
        using var provider = TheEnginesPipeline(host: services => services.AddScoped<IStage<WorkloadUpdateStageContext>>(
            _ => new HostStage(HostStageId, StagePlacement.After(WorkloadUpdateStages.CommitVersion))));
        using var scope = provider.CreateScope();

        var pipeline = scope.ServiceProvider.GetRequiredService<Pipeline<WorkloadUpdateStageContext>>();

        pipeline.Order.Should().Equal(
            WorkloadUpdateStages.Params,
            WorkloadUpdateStages.Sidecar,
            WorkloadUpdateStages.OperatorRedeploy,
            WorkloadUpdateStages.HealthGate,
            WorkloadUpdateStages.CommitVersion,
            HostStageId,
            WorkloadUpdateStages.CommitParams);
    }

    [Fact]
    public void The_engine_resolves_its_pipeline_through_the_librarys_rule()
    {
        using var provider = TheEnginesPipeline(host: services => services.AddScoped<IStage<WorkloadUpdateStageContext>>(
            _ => new HostStage("stage.host.too-late", StagePlacement.After(WorkloadUpdateStages.CommitParams))));
        using var scope = provider.CreateScope();

        var resolve = () => scope.ServiceProvider.GetRequiredService<Pipeline<WorkloadUpdateStageContext>>();

        resolve.Should().Throw<PipelineDefinitionException>().WithMessage("*stage.host.too-late*");
    }

    [Fact]
    public void The_EF_composition_resolves_cohort_services_through_their_longest_constructors()
    {
        var services = new ServiceCollection();
        var builder = services.AddReeveWorkloads(EmptyConfiguration)
            .AddEngine()
            .AddEntityFrameworkStores(options => options.UseNpgsql(
                "Host=composition-tests.invalid;Database=workloads;Username=none;Password=none"));
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IDataProtectionProvider>(new PassThroughDataProtection());
        builder.Services.AddScoped<IWorkloadUpdatePlanner>(_ => NeverCalled<IWorkloadUpdatePlanner>());
        builder.Services.AddScoped<IWorkloadUpdateReconcileNudge>(_ => NeverCalled<IWorkloadUpdateReconcileNudge>());
        builder.Services.AddScoped<IWorkloadAuditSink>(_ => NeverCalled<IWorkloadAuditSink>());
        builder.Services.AddScoped(_ => NeverCalled<IWorkloadUpdateEffects>());
        builder.Services.AddScoped(_ => NeverCalled<IWorkloadUpdateRowEffects>());
        builder.Services.AddScoped(_ => NeverCalled<IWorkloadUpdateHealthGate>());
        builder.Services.AddSingleton(_ => NeverCalled<IWorkloadProviderRegistry>());
        builder.Services.AddScoped(_ => NeverCalled<IObservedWorkloadVersionReader>());
        builder.Services.AddSingleton(_ => NeverCalled<IWorkloadDiagnosticsSinkFactory>());

        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var serviceProvider = scope.ServiceProvider;
        var abandon = serviceProvider.GetRequiredService<IWorkloadUpdateAbandonService>();
        var resumer = serviceProvider.GetRequiredService<IWorkloadIntentResumer>();
        var initiator = serviceProvider.GetRequiredService<IWorkloadUpdateCohortInitiator>();

        abandon.GetType().GetField("_groupRepository", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(abandon).Should().NotBeNull();
        resumer.GetType().GetField("_groupRepository", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(resumer).Should().NotBeNull();
        initiator.GetType().GetField("_groupRepository", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(initiator).Should().NotBeNull();
        serviceProvider.GetRequiredService<IWorkloadUpdateIntentRepository>().SupportsCohorts.Should().BeTrue();
    }

    private static ServiceProvider TheEnginesPipeline(Action<IServiceCollection>? host)
    {
        var composed = new ServiceCollection();
        host?.Invoke(composed);
        var engine = composed.AddReeveWorkloads(EmptyConfiguration).AddEngine();

        IServiceCollection services = new ServiceCollection();
        foreach (var descriptor in engine.Services.Where(descriptor =>
                     descriptor.ServiceType == typeof(IStage<WorkloadUpdateStageContext>)
                     || descriptor.ServiceType == typeof(Pipeline<WorkloadUpdateStageContext>)))
        {
            services.Add(descriptor);
        }

        services.Add(engine.Services.Last(descriptor => descriptor.ServiceType == typeof(IWorkloadClock)));
        services.Add(engine.Services.Last(descriptor =>
            descriptor.ServiceType == typeof(WorkloadUpdateOrchestratorOptions)));
        services.Add(engine.Services.Last(descriptor =>
            descriptor.ServiceType == typeof(Reeve.Workloads.Core.Health.WorkloadHealthContributorDispatch)));
        services.AddScoped(_ => NeverCalled<IWorkloadUpdateEffects>());
        services.AddScoped(_ => NeverCalled<Reeve.Workloads.Core.Update.IWorkloadUpdateRowEffects>());
        services.AddScoped(_ => NeverCalled<IWorkloadUpdateHealthGate>());
        services.AddScoped(_ => NeverCalled<IWorkloadInstanceRepository>());
        services.AddScoped(_ => NeverCalled<Reeve.Workloads.Abstractions.IWorkloadCredentialPort>());
        services.AddSingleton<IDataProtectionProvider>(new PassThroughDataProtection());
        services.AddScoped<UnpinnedReferenceRolloutGuard>();
        services.AddSingleton(_ => NeverCalled<IWorkloadProviderRegistry>());
        services.AddScoped(_ => NeverCalled<Reeve.Workloads.Core.Update.IObservedWorkloadVersionReader>());
        services.AddSingleton(_ => NeverCalled<IWorkloadDiagnosticsSinkFactory>());
        services.AddLogging();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static T NeverCalled<T>()
        where T : class
        => DispatchProxy.Create<T, NeverCalledProxy>();

    private sealed class PassThroughDataProtection : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }

    public class NeverCalledProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException($"Resolving the pipeline ran {targetMethod?.Name}.");
    }

    private sealed record HostStage(string Id, StagePlacement Placement) : IStage<WorkloadUpdateStageContext>
    {
        public StageReversibility Reversibility => StageReversibility.Reversible;

        public StageBudget Budget => StageBudget.None;

        public ValueTask<StageOutcome> UpAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);

        public ValueTask<StageOutcome> DownAsync(WorkloadUpdateStageContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(StageOutcome.Done);
    }
}
