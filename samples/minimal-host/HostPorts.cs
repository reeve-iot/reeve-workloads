using System.Text.Json;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Samples.MinimalHost;

internal static class HostPorts
{
    public const string SourceKey = "local";
}

internal sealed class InMemoryManifestSource : IWorkloadGroupManifestSource
{
    private readonly Dictionary<(string Module, string Version), DeploymentManifest> published = [];

    public void Publish(string groupId, string version, DeploymentManifest manifest)
        => published[(groupId.ToLowerInvariant(), version)] = manifest;

    public Task<AvailableGroupManifestRead> GetAvailableAsync(
        string groupId, CancellationToken cancellationToken = default)
        => Task.FromResult(new AvailableGroupManifestRead
        {
            HighestAvailable = published
                .Where(entry => entry.Key.Module == groupId.ToLowerInvariant())
                .OrderByDescending(entry => entry.Key.Version, StringComparer.Ordinal)
                .Select(entry => entry.Value)
                .FirstOrDefault(),
        });

    public Task<GroupManifestRead> GetAtVersionAsync(
        string sourceKey, string groupId, string version, CancellationToken cancellationToken = default)
        => Task.FromResult(published.GetValueOrDefault((groupId.ToLowerInvariant(), version)) is { } manifest
            ? new GroupManifestRead(manifest, Refused: false)
            : GroupManifestRead.Absent);
}

internal sealed class JsonFileSettingsSource(string path) : IWorkloadOrchestratorSettingsSource
{
    private DateTime stamp = DateTime.MinValue;
    private WorkloadOrchestratorSettings cached = new();

    public ValueTask<WorkloadOrchestratorSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return ValueTask.FromResult(new WorkloadOrchestratorSettings());
        }

        var written = File.GetLastWriteTimeUtc(path);
        if (written != stamp)
        {
            cached = JsonSerializer.Deserialize<WorkloadOrchestratorSettings>(File.ReadAllText(path)) ?? new WorkloadOrchestratorSettings();
            stamp = written;
        }

        return ValueTask.FromResult(cached);
    }
}
