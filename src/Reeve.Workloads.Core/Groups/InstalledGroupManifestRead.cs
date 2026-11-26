using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Core.Groups;

internal sealed record InstalledGroupManifestRead
{
    public static InstalledGroupManifestRead Nothing { get; } = new();

    public string? InstalledVersion { get; init; }

    public DeploymentManifest? AtInstalledVersion { get; init; }

    public DeploymentManifest? HighestAvailable { get; init; }

    public DeploymentManifest? Bundled { get; init; }

    public bool Refused { get; init; }

    public static async Task<InstalledGroupManifestRead> ReadAsync(
        IWorkloadGroupManifestSource manifests,
        IWorkloadGroupRepository modules,
        string groupId,
        CancellationToken cancellationToken)
    {
        var available = await manifests.GetAvailableAsync(groupId, cancellationToken).ConfigureAwait(false);
        var row = await modules.GetAsync(groupId, cancellationToken).ConfigureAwait(false);

        var atInstalled = row?.InstalledVersion is { } version
            ? (await manifests.GetAtVersionAsync(row.SourceKey, groupId, version, cancellationToken)
                .ConfigureAwait(false)).Manifest
            : null;

        return new InstalledGroupManifestRead
        {
            InstalledVersion = row?.InstalledVersion,
            AtInstalledVersion = atInstalled,
            HighestAvailable = available.HighestAvailable,
            Bundled = available.Bundled,
            Refused = available.Refused,
        };
    }
}
