using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Core.Groups;


/// <summary>The materialize context, assembled over the manifest source and the module row.</summary>
public sealed class GroupManifestDeploymentContextSource : IWorkloadDeploymentContextSource
{
    private readonly IWorkloadGroupManifestSource manifests;
    private readonly IWorkloadGroupRepository groups;

    public GroupManifestDeploymentContextSource(
        IWorkloadGroupManifestSource manifests, IWorkloadGroupRepository groups)
    {
        this.manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        this.groups = groups ?? throw new ArgumentNullException(nameof(groups));
    }

    /// <inheritdoc />
    public async Task<WorkloadDeploymentContext?> GetForGroupAsync(
        string groupId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        var read = await InstalledGroupManifestRead.ReadAsync(manifests, groups, groupId, cancellationToken)
            .ConfigureAwait(false);
        return ForTheEditForm(read) is { } deployment
            ? new WorkloadDeploymentContext { GroupId = groupId, Deployment = deployment }
            : null;
    }

    internal static DeploymentManifest? ForTheEditForm(InstalledGroupManifestRead read)
    {
        if (read.HighestAvailable is null)
        {
            return read.Refused ? null : read.Bundled;
        }

        return read.InstalledVersion is not null ? read.AtInstalledVersion : read.HighestAvailable;
    }
}
