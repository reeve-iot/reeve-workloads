using Reeve.Workloads.Domain;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Groups;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests.Groups;

internal sealed class FakeManifests : IWorkloadGroupManifestSource
{
    public const string SourceKey = "55555555-5555-5555-5555-555555555555";

    public AvailableGroupManifestRead Available { get; set; } = AvailableGroupManifestRead.Nothing;

    public Dictionary<string, GroupManifestRead> AtVersion { get; } = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = [];

    public FakeManifests Install(
        InMemoryWorkloadGroupRepository modules, string groupId, InstalledGroupManifestRead read)
    {
        Available = new AvailableGroupManifestRead
        {
            HighestAvailable = read.HighestAvailable,
            Bundled = read.Bundled,
            Refused = read.Refused,
        };

        if (read.InstalledVersion is { } version)
        {
            modules.Seed(groupId, SourceKey, version);
            if (read.AtInstalledVersion is { } atVersion)
            {
                AtVersion[version] = new GroupManifestRead(atVersion, Refused: false);
            }
        }

        return this;
    }

    public Task<AvailableGroupManifestRead> GetAvailableAsync(
        string groupId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"GetAvailable({groupId})");
        return Task.FromResult(Available);
    }

    public Task<GroupManifestRead> GetAtVersionAsync(
        string sourceKey, string groupId, string version, CancellationToken cancellationToken = default)
    {
        Calls.Add($"GetAtVersion({sourceKey}:{groupId}@{version})");
        return Task.FromResult(string.Equals(sourceKey, SourceKey, StringComparison.Ordinal)
            ? AtVersion.GetValueOrDefault(version, GroupManifestRead.Absent)
            : GroupManifestRead.Absent);
    }
}

internal sealed class FakeHostBootstrapSource : IWorkloadHostBootstrapSource
{
    public const string TokenCredentialId = "workload-token";

    public string? GatewayBaseUrl { get; init; } = "https://reeve.example";

    public string? BrokerUrl { get; init; } = "mqtt://broker:1883";

    public string? Username { get; init; } = "reeve";

    public string? Password { get; init; } = "not-a-real-password";

    public IReadOnlyList<ResolvedEnvVar> HostEnv { get; init; } = [];

    public IReadOnlyList<ProvisionedCredential> ProvisionedCredentials { get; init; } = [];

    public string? Refusal { get; init; }

    public Func<WorkloadHostBootstrapRequest, bool>? RefuseWhen { get; init; }

    public List<WorkloadHostBootstrapRequest> Requests { get; } = [];

    public Task<WorkloadHostBootstrapResult> GetAsync(
        WorkloadHostBootstrapRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(Refusal is { } refused && (RefuseWhen?.Invoke(request) ?? true)
            ? WorkloadHostBootstrapResult.Refused(refused)
            : new WorkloadHostBootstrapResult
            {
                Values = HostValues(request.InstanceId, GatewayBaseUrl, request.SecretName, BrokerUrl, Username, Password),
                HostEnv = HostEnv,
                ProvisionedCredentials = ProvisionedCredentials,
            });
    }

    public static Dictionary<string, BootstrapValue> HostValues(
        Guid instanceId, string? gatewayBaseUrl, string secretName,
        string? brokerUrl = null, string? username = null, string? password = null)
        => new(StringComparer.Ordinal)
        {
            ["gatewayUrl"] = BootstrapValue.Literal(GatewayUrl(gatewayBaseUrl, instanceId)),
            ["backendBaseUrl"] = BootstrapValue.Literal(gatewayBaseUrl),
            ["workloadToken"] = BootstrapValue.HostCredential(TokenCredentialId),
            ["mqttBrokerUrl"] = BootstrapValue.Literal(brokerUrl),
            ["mqttUsername"] = BootstrapValue.Literal(username),
            ["mqttPassword"] = string.IsNullOrEmpty(password)
                ? BootstrapValue.SecretRef(null)
                : BootstrapValue.SecretRef(secretName, password),
        };

    private static string? GatewayUrl(string? baseUrl, Guid instanceId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var trimmed = baseUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith("/ws/workloads", StringComparison.OrdinalIgnoreCase)
            ? $"{trimmed}/{instanceId:D}"
            : $"{trimmed}/ws/workloads/{instanceId:D}";
    }

    public static Dictionary<string, BootstrapValue> AllValues(
        Guid instanceId, string? gatewayBaseUrl, string secretName)
    {
        var values = HostValues(instanceId, gatewayBaseUrl, secretName);
        values["instanceId"] = BootstrapValue.Literal(instanceId.ToString("D"));
        values["specHash"] = BootstrapValue.SpecHash;
        return values;
    }
}

internal sealed class DefaultSettings(WorkloadProviderId? effectiveProvider = null) : IWorkloadOrchestratorSettingsService
{
    public Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new WorkloadOrchestratorSettingsResponse
        {
            Settings = new WorkloadOrchestratorSettings(),
            EffectiveProvider = effectiveProvider ?? ManualProvider.Id,
            AutoDetected = true,
            Providers = [],
        });


    public Task<WorkloadProviderProbeResult> TestAsync(
        WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

internal sealed class NoIdentity : IWorkloadInstallationIdentity
{
    public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken = default)
        => Task.FromResult("11111111-1111-1111-1111-111111111111");
}

internal sealed class FakeInstances : IWorkloadInstanceRepository
{
    public List<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance> Instances { get; } = [];

    public List<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance> Updated { get; } = [];

    public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>> ListByPluginInstanceAsync(
        Guid pluginInstanceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>>(
            Instances.Where(instance => instance.GroupInstanceId == pluginInstanceId).ToList());

    public Task<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance?> GetByIdAsync(
        Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(Instances.FirstOrDefault(instance => instance.Id == id));

    public Task<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance?> GetByWorkloadAsync(
        Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>> ListByStateAsync(
        Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstanceState state, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>> ListAllAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance>>(Instances.ToList());

    public Task<IReadOnlyDictionary<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstanceState, int>> CountByStateAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public List<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance> Added { get; } = [];

    public Task AddAsync(Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance instance, CancellationToken cancellationToken = default)
    {
        Added.Add(instance);
        if (Instances.Exists(row => row.Id == instance.Id
                || (row.GroupInstanceId == instance.GroupInstanceId && row.WorkloadId == instance.WorkloadId)))
        {
            throw new InvalidOperationException(
                $"A row for {instance.GroupInstanceId}/{instance.WorkloadId} or with id {instance.Id} already exists.");
        }

        Instances.Add(instance);
        return Task.CompletedTask;
    }

    public int ConflictsBeforeSuccess { get; set; }

    public Guid? ConflictsOn { get; set; }

    public List<Guid> StaleRows { get; } = [];

    public Dictionary<Guid, Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance> ReloadAnswersById { get; } = [];

    public List<Guid> Reloaded { get; } = [];

    public Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance? ReloadAnswer { get; set; }

    public bool ReloadAnswersNull { get; set; }

    public Task UpdateAsync(Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance instance, CancellationToken cancellationToken = default)
    {
        if (ConflictsBeforeSuccess > 0)
        {
            ConflictsBeforeSuccess--;
            throw new WorkloadConcurrencyConflictException(
                $"Workload instance {instance.Id} was written by someone else; this update was refused.")
            {
                EntityId = ConflictsOn ?? instance.Id,
            };
        }

        if (StaleRows.Count > 0)
        {
            throw new WorkloadConcurrencyConflictException(
                $"Workload instance {StaleRows[0]} was written by someone else; this update was refused.")
            {
                EntityId = StaleRows[0],
            };
        }

        Updated.Add(instance);
        return Task.CompletedTask;
    }

    public Task<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance?> ReloadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Reloaded.Add(id);
        StaleRows.Remove(id);
        if (ReloadAnswersById.TryGetValue(id, out var answer))
        {
            return Task.FromResult<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance?>(answer);
        }

        return Task.FromResult(ReloadAnswersNull
            ? null
            : ReloadAnswer ?? Instances.FirstOrDefault(instance => instance.Id == id));
    }

    public Task<bool> TrySetRestoreBindingUnresolvedAsync(
        Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public List<Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance> Deleted { get; } = [];

    public Task DeleteAsync(Reeve.Workloads.Abstractions.Domain.Entities.WorkloadInstance instance, CancellationToken cancellationToken = default)
    {
        Deleted.Add(instance);
        Instances.RemoveAll(row => row.Id == instance.Id);
        return Task.CompletedTask;
    }
}
