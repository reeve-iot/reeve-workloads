using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests;

internal sealed class SwitchableSettingsSource : IWorkloadOrchestratorSettingsSource
{
    public WorkloadOrchestratorSettings Document { get; set; } = new();

    public ValueTask<WorkloadOrchestratorSettings> GetAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Document);
}

internal static class SettingsKit
{
    public const string Registry = "registry.example:5000";

    public static IWorkloadProviderSettingsCodec[] ShippedCodecs()
        => [new DockerProviderSettingsCodec(), new KubernetesProviderSettingsCodec(), new ManualProviderSettingsCodec()];

    public static ICompiledProviderCatalog ShippedCatalog()
        => new CompiledProviderCatalog(ProviderRegistryFakes.FirstPartyRegistry());

    public static WorkloadOrchestratorSettingsRules ShippedRules()
        => new(ShippedCatalog(), ShippedCodecs());

    public static string Kubeconfig(string server = "https://10.0.0.1:6443", string context = "remote", string token = "token-one")
        => $"""
            apiVersion: v1
            kind: Config
            current-context: {context}
            clusters:
            - name: remote-cluster
              cluster:
                server: {server}
                certificate-authority-data: ZmFrZS1jYS1kYXRh
            contexts:
            - name: {context}
              context:
                cluster: remote-cluster
                user: remote-user
            users:
            - name: remote-user
              user:
                token: {token}
            """;

    public static WorkloadOrchestratorSettings Docker(string token = "docker-secret", string? socket = null)
        => new WorkloadOrchestratorSettings { Provider = DockerProvider.Id }
            .WithProvider(DockerProvider.Id, new DockerProviderSettings
            {
                SocketUri = socket,
                RegistryCredentials = [new RegistryCredential { HostPattern = Registry, Username = "puller", Token = token }],
            });

    public static WorkloadOrchestratorSettings Kubernetes(string? ns = "reeve", string? kubeconfig = null)
        => new WorkloadOrchestratorSettings { Provider = KubernetesProvider.Id }
            .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings
            {
                Namespace = ns,
                ConnectionMode = kubeconfig is null ? KubernetesConnectionMode.InCluster : KubernetesConnectionMode.RemoteKubeconfig,
                Kubeconfig = kubeconfig,
            });
}

internal sealed class ScriptedCodec(
    WorkloadProviderId provider,
    Func<System.Text.Json.JsonElement, IReadOnlyDictionary<string, string>>? placement = null) : IWorkloadProviderSettingsCodec
{
    public System.Text.Json.JsonElement? SeenStored { get; private set; } = System.Text.Json.JsonDocument.Parse("0").RootElement;

    public bool Resolved { get; private set; }

    public WorkloadProviderId Provider { get; } = provider;

    public WorkloadProviderSettingsValidation Validate(System.Text.Json.JsonElement entry) => WorkloadProviderSettingsValidation.Valid;

    public System.Text.Json.JsonElement Mask(System.Text.Json.JsonElement entry) => entry;

    public System.Text.Json.JsonElement ResolveMasked(System.Text.Json.JsonElement submitted, System.Text.Json.JsonElement? stored)
    {
        Resolved = true;
        SeenStored = stored;
        return submitted;
    }

    public IReadOnlyList<WorkloadSecretPath> SecretFields(System.Text.Json.JsonElement entry) => [];

    public IReadOnlyDictionary<string, string> Placement(System.Text.Json.JsonElement entry)
        => placement?.Invoke(entry) ?? new Dictionary<string, string>();
}
