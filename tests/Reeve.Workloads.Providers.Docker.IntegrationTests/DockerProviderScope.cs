using Microsoft.Extensions.DependencyInjection;
using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

internal static class DockerProviderScope
{
    public static IServiceScopeFactory For<TSettings>(TSettings settings, IWorkloadCredentialPort credentialPort)
        where TSettings : class, IWorkloadOrchestratorSettingsService, IWorkloadOrchestratorSecretReader
        => new ServiceCollection()
            .AddSingleton<IWorkloadOrchestratorSettingsService>(settings)
            .AddSingleton<IWorkloadOrchestratorSecretReader>(settings)
            .AddSingleton(credentialPort)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
}
