using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.Orphans;

internal sealed class DockerWorkloadOrphanService(
    IDockerClientFactory clientFactory,
    IWorkloadInstallationIdentity installationIdentity,
    IWorkloadInstanceIdReader instanceIdReader,
    IDockerSocketUriReader socketUriReader,
    ILogger<DockerWorkloadOrphanService> logger) : IDockerWorkloadOrphanService
{
    public async Task<IReadOnlyList<WorkloadOrphan>> DetectAsync(CancellationToken cancellationToken = default)
    {
        var thisInstallation = await installationIdentity.GetInstallationIdAsync(cancellationToken).ConfigureAwait(false);
        var known = await instanceIdReader.GetKnownInstanceIdsAsync(cancellationToken).ConfigureAwait(false);

        var containers = await ListManagedContainersAsync(cancellationToken).ConfigureAwait(false);

        return containers
            .Select(container => (
                container,
                instanceId: ParseInstanceId(container),
                installation: Label(container, DockerProviderConstants.InstallationLabelKey)))
            .Where(pair => pair.instanceId is not null)
            .Where(pair => !IsThisInstallation(pair.installation, thisInstallation)
                        || !known.Contains(pair.instanceId!.Value))
            .GroupBy(pair => pair.instanceId!.Value)
            .Select(group => new WorkloadOrphan
            {
                InstanceId = group.Key,
                ContainerNames = [.. group.SelectMany(pair => pair.container.Names ?? []).Select(TrimLeadingSlash)],
                ContainerIds = [.. group.Select(pair => pair.container.ID)],
                GroupId = Label(group.First().container, DockerProviderConstants.GroupIdLabelKey),
                WorkloadId = Label(group.First().container, DockerProviderConstants.WorkloadIdLabelKey),
                ForeignInstallationId = IsThisInstallation(group.First().installation, thisInstallation)
                    ? null
                    : group.First().installation ?? "(no installation label)",
            })
            .ToList();
    }

    public async Task<int> PurgeAsync(
        Guid instanceId,
        bool acknowledgeForeignInstallation = false,
        CancellationToken cancellationToken = default)
    {
        var orphans = await DetectAsync(cancellationToken).ConfigureAwait(false);
        var target = orphans.FirstOrDefault(orphan => orphan.InstanceId == instanceId);
        if (target is null)
        {
            return 0;
        }

        if (target.IsForeign && !acknowledgeForeignInstallation)
        {
            throw new WorkloadOrphanForeignInstallationException(
                $"Container group {instanceId} carries installation '{target.ForeignInstallationId}', which is not "
              + "this installation's. It is either another Reeve sharing this Docker daemon, or this "
              + "installation's own fleet from before its identity changed. Removing it must be acknowledged "
              + "explicitly.");
        }

        var client = clientFactory.CreateClient(await socketUriReader.GetDockerSocketUriAsync(cancellationToken).ConfigureAwait(false));

        var removed = 0;
        foreach (var containerId in target.ContainerIds)
        {
            try
            {
                await client.Containers.RemoveContainerAsync(
                    containerId,
                    new ContainerRemoveParameters { Force = true },
                    cancellationToken).ConfigureAwait(false);
                removed++;
            }
            catch (DockerContainerNotFoundException)
            {
                removed++;
            }
        }

        logger.LogInformation(
            "Purged {Removed} orphaned workload container(s) for instance {InstanceId}.", removed, instanceId);
        return removed;
    }

    private async Task<IList<ContainerListResponse>> ListManagedContainersAsync(CancellationToken cancellationToken)
    {
        var socketUri = await socketUriReader.GetDockerSocketUriAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var client = clientFactory.CreateClient(socketUri);
            return await client.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool>
                        {
                            [$"{DockerProviderConstants.ManagedByLabelKey}={DockerProviderConstants.ManagedByLabelValue}"] = true,
                        },
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WorkloadOrphanProviderUnreachableException(
                "The Docker daemon could not be reached, so orphaned workloads cannot be listed.", ex);
        }
    }

    private static string? Label(ContainerListResponse container, string key)
        => container.Labels is not null && container.Labels.TryGetValue(key, out var value) ? value : null;

    private static bool IsThisInstallation(string? label, string? thisInstallation)
        => label is not null
        && thisInstallation is not null
        && string.Equals(label, thisInstallation, StringComparison.OrdinalIgnoreCase);

    private static Guid? ParseInstanceId(ContainerListResponse container)
        => Guid.TryParse(Label(container, DockerProviderConstants.InstanceLabelKey), out var id) ? id : null;

    private static string TrimLeadingSlash(string name)
        => name.StartsWith('/') ? name[1..] : name;
}

/// <summary>A workload container group with no row describing it — either this installation's, or one
/// carrying a DIFFERENT installation label.</summary>
public sealed record WorkloadOrphan
{
    /// <summary>The <c>io.reeve.workload-instance</c> label value — the grouping and purge key.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>Container names, without docker's leading slash. Convenience for the operator; never identity.</summary>
    public required IReadOnlyList<string> ContainerNames { get; init; }

    /// <summary>Container ids — what the purge actually removes.</summary>
    public required IReadOnlyList<string> ContainerIds { get; init; }

    /// <summary>The plugin type the workload belonged to, when the label is present.</summary>
    public string? GroupId { get; init; }

    /// <summary>The manifest workload id (e.g. <c>simulator</c>), when the label is present.</summary>
    public string? WorkloadId { get; init; }

    /// <summary>The <c>io.reeve.installation</c> label the container carries, when it is NOT this installation's; null for an ordinary orphan. See <see cref="IsForeign"/>.</summary>
    public string? ForeignInstallationId { get; init; }

    /// <summary>True when the container was created by a different installation id. Two situations produce this
    /// and only the operator can tell them apart: a genuinely co-tenanted daemon, or OUR OWN fleet after an identity change (a restore that missed <c>core.runtime_settings</c>) — reported rather than omitted so either case stays visible and nothing is purged implicitly.</summary>
    public bool IsForeign => ForeignInstallationId is not null;
}

/// <summary>Raised when a purge targets a container group belonging to a DIFFERENT installation and the caller
/// has not acknowledged that — reporting is safe, deleting without an explicit decision is not.</summary>
public sealed class WorkloadOrphanForeignInstallationException(string message) : Exception(message);

/// <summary>Raised when the provider cannot be inspected, so "no orphans" would be a claim rather than a fact.</summary>
public sealed class WorkloadOrphanProviderUnreachableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal interface IDockerSocketUriReader
{
    Task<string?> GetDockerSocketUriAsync(CancellationToken cancellationToken = default);
}
