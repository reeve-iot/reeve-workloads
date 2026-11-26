using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core.HealthGate;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Seams;

public sealed class SeamRegistrationCheckTests
{
    private static readonly WorkloadHealthMode HostMode = new("adapter-session");
    private static readonly WorkloadHealthMode OtherHostMode = new("acme-heartbeat");

    private sealed class Contributor(params WorkloadHealthMode[] observed) : IWorkloadHealthContributor
    {
        public IReadOnlySet<WorkloadHealthMode> ObservedModes { get; } = new HashSet<WorkloadHealthMode>(observed);

        public Task<WorkloadHealthContribution> ObserveAsync(
            WorkloadHealthObservation observation, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadHealthContribution.None);
    }

    private sealed class Certifier : IRolloutCertifier
    {
        public IReadOnlySet<WorkloadHealthMode> RequiredModes { get; } = new HashSet<WorkloadHealthMode> { HostMode };

        public Task<RolloutEvidence> CertifyAsync(
            RolloutCertificationRequest request, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(RolloutEvidence.NoCandidate);
    }

    private static void Start(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        WorkloadSeamRegistrationCheck.Run(provider.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public void Two_contributors_observing_one_mode_fail_start_naming_the_mode()
    {
        var start = () => Start(services =>
        {
            services.AddScoped<IWorkloadHealthContributor>(_ => new Contributor(HostMode));
            services.AddScoped<IWorkloadHealthContributor>(_ => new Contributor(OtherHostMode, HostMode));
        });

        start.Should().Throw<InvalidOperationException>().WithMessage("*adapter-session*");
    }

    [Fact]
    public void Two_certifiers_fail_start()
    {
        var start = () => Start(services =>
        {
            services.AddScoped<IRolloutCertifier, Certifier>();
            services.AddScoped<IRolloutCertifier, Certifier>();
        });

        start.Should().Throw<InvalidOperationException>().WithMessage("*IRolloutCertifier*");
    }

    [Fact]
    public void Contributors_with_disjoint_modes_and_one_certifier_start()
    {
        var start = () => Start(services =>
        {
            services.AddScoped<IWorkloadHealthContributor>(_ => new Contributor(HostMode));
            services.AddScoped<IWorkloadHealthContributor>(_ => new Contributor(OtherHostMode));
            services.AddSingleton<IWorkloadHealthContributor, ProviderReadyHealthContributor>();
            services.AddScoped<IRolloutCertifier, Certifier>();
        });

        start.Should().NotThrow();
    }

    [Fact]
    public void Nothing_registered_starts()
    {
        var start = () => Start(_ => { });

        start.Should().NotThrow();
    }
}
