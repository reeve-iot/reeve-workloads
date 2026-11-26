using System.Text.Json;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class KubernetesProviderSettingsCodec : IWorkloadProviderSettingsCodec
{
    private static readonly IReadOnlySet<string> OwnedKeys = ProviderSettingsPassThrough.OwnedKeys<KubernetesProviderSettings>();

    public WorkloadProviderId Provider => KubernetesProvider.Id;

    public WorkloadProviderSettingsValidation Validate(JsonElement entry)
    {
        var submitted = KubernetesProviderSettings.From(entry);
        if (WorkloadSecretMask.IsSentinel(submitted.Kubeconfig))
        {
            return WorkloadProviderSettingsValidation.Invalid(
                "kubernetes.kubeconfig sent the masked value but has no stored secret to preserve — supply the real kubeconfig.");
        }

        if (submitted.ConnectionMode != KubernetesConnectionMode.RemoteKubeconfig)
        {
            return WorkloadProviderSettingsValidation.Valid;
        }

        var result = KubeconfigValidator.Validate(submitted.Kubeconfig, submitted.KubeconfigContext);
        return result.IsValid
            ? WorkloadProviderSettingsValidation.Valid
            : WorkloadProviderSettingsValidation.Invalid($"kubernetes.kubeconfig is invalid: {result.Message}");
    }

    public JsonElement ResolveMasked(JsonElement submitted, JsonElement? stored)
    {
        var settings = KubernetesProviderSettings.From(submitted);
        if (WorkloadSecretMask.IsSentinel(settings.Kubeconfig))
        {
            var prior = stored is { } s ? KubernetesProviderSettings.From(s).Kubeconfig : null;
            settings = settings with
            {
                Kubeconfig = string.IsNullOrEmpty(prior)
                    ? throw new WorkloadOrchestratorSettingsValidationException(
                        "kubernetes.kubeconfig sent the masked value but has no stored secret to preserve — supply the real kubeconfig.")
                    : prior,
            };
        }

        return ProviderSettingsPassThrough.Merge(JsonSerializer.SerializeToElement(settings), submitted, stored, OwnedKeys);
    }

    public JsonElement Mask(JsonElement entry)
    {
        var settings = KubernetesProviderSettings.From(entry);
        var masked = JsonSerializer.SerializeToElement(settings with
        {
            Kubeconfig = string.IsNullOrEmpty(settings.Kubeconfig) ? settings.Kubeconfig : WorkloadSecretMask.Sentinel,
        });
        return ProviderSettingsPassThrough.Carry(masked, entry, OwnedKeys);
    }

    public IReadOnlyList<WorkloadSecretPath> SecretFields(JsonElement entry)
        => KubernetesProviderSettings.From(entry).Kubeconfig is { Length: > 0 } kubeconfig
            ? [new WorkloadSecretPath("/kubeconfig", kubeconfig)]
            : [];

    public IReadOnlyDictionary<string, string> Placement(JsonElement entry)
    {
        var settings = KubernetesProviderSettings.From(entry);
        var placement = new Dictionary<string, string>
        {
            ["namespace"] = settings.Namespace ?? string.Empty,
            ["connectionMode"] = settings.ConnectionMode.ToString(),
        };
        if (settings.ConnectionMode == KubernetesConnectionMode.RemoteKubeconfig)
        {
            if (KubeconfigValidator.ResolveServer(settings.Kubeconfig, settings.KubeconfigContext) is { } server)
            {
                placement["server"] = server;
            }
        }

        return placement;
    }
}
