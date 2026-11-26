using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerVolumeManager : IDockerVolumeManager
{
    private readonly IDockerVolumeGroupOwnership _groupOwnership;

    public DockerVolumeManager()
        : this(new DockerVolumeGroupOwnership())
    {
    }

    public DockerVolumeManager(IDockerVolumeGroupOwnership groupOwnership)
    {
        _groupOwnership = groupOwnership ?? throw new ArgumentNullException(nameof(groupOwnership));
    }

    public async Task<DockerOperationResult> EnsureVolumesAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        DockerProviderSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Volumes.Count == 0)
        {
            return DockerOperationResult.Ok();
        }

        var labels = DockerProviderConstants.BuildResourceLabels(spec)
            .Where(kv => kv.Key != DockerProviderConstants.UnhashedEnvDigestLabelKey)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        try
        {
            foreach (var volume in spec.Volumes)
            {
                var name = volume.ResourceName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    return DockerOperationResult.Fail(
                        DockerFailureKind.Rejected,
                        $"volume '{volume.Id}' has no resolved resource name to materialize.");
                }

                var exists = await VolumeExistsAsync(client, name, cancellationToken).ConfigureAwait(false);

                if (volume.ExistingClaim)
                {
                    if (!exists)
                    {
                        return DockerOperationResult.Fail(
                            DockerFailureKind.Rejected,
                            $"volume '{name}' is a restore-adopted existing claim but does not exist on the daemon; " +
                            "refusing to create a fresh empty volume (fail-closed).");
                    }

                    continue;
                }

                if (!exists)
                {
                    await client.Volumes.CreateAsync(
                        new VolumesCreateParameters
                        {
                            Name = name,
                            Labels = new Dictionary<string, string>(labels, StringComparer.Ordinal),
                        },
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return await _groupOwnership.EnsureAsync(client, spec, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DockerOperationResult.Fail(Classify(ex), ex.Message);
        }
    }

    public async Task<DockerOperationResult> RemoveVolumesAsync(
        IDockerClient client,
        WorkloadRef reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var instanceId = reference.InstanceId.ToString("D");

        try
        {
            var listed = await client.Volumes.ListAsync(
                new VolumesListParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                    {
                        ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                        {
                            [$"{DockerProviderConstants.InstanceLabelKey}={instanceId}"] = true,
                        },
                    },
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var volume in listed.Volumes ?? new List<VolumeResponse>())
            {
                if (volume.Labels is null
                    || !volume.Labels.TryGetValue(DockerProviderConstants.InstanceLabelKey, out var owner)
                    || !string.Equals(owner, instanceId, StringComparison.Ordinal))
                {
                    continue;
                }

                await RemoveOneAsync(client, volume.Name, cancellationToken).ConfigureAwait(false);
            }

            return DockerOperationResult.Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DockerOperationResult.Fail(Classify(ex), ex.Message);
        }
    }

    private static async Task<bool> VolumeExistsAsync(IDockerClient client, string name, CancellationToken cancellationToken)
    {
        try
        {
            await client.Volumes.InspectAsync(name, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task RemoveOneAsync(IDockerClient client, string name, CancellationToken cancellationToken)
    {
        try
        {
            await client.Volumes.RemoveAsync(name, force: true, cancellationToken).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static DockerFailureKind Classify(Exception ex) => ex switch
    {
        DockerApiException api => api.StatusCode >= HttpStatusCode.InternalServerError
            ? DockerFailureKind.Error
            : DockerFailureKind.Rejected,
        HttpRequestException => DockerFailureKind.Unreachable,
        SocketException => DockerFailureKind.Unreachable,
        IOException => DockerFailureKind.Unreachable,
        TimeoutException => DockerFailureKind.Unreachable,
        OperationCanceledException => DockerFailureKind.Unreachable,
        _ => DockerFailureKind.Error,
    };
}
