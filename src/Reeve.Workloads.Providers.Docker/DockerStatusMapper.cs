using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerStatusMapper : IDockerStatusMapper
{
    public async Task<WorkloadStatus> GetStatusAsync(
        IDockerClient client,
        WorkloadRef reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        IList<ContainerListResponse>? listed;
        try
        {
            listed = await client.Containers
                .ListContainersAsync(
                    new ContainersListParameters
                    {
                        All = true,
                        Filters = InstanceLabelFilter(reference.InstanceId),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unknown();
        }

        return await ResolveAsync(client, listed, reference.InstanceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
        IDockerClient client,
        IReadOnlyList<WorkloadRef> references,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(references);

        IList<ContainerListResponse>? listed;
        try
        {
            listed = await client.Containers
                .ListContainersAsync(
                    new ContainersListParameters { All = true, Filters = AnyInstanceLabelFilter() },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var unreachable = new Dictionary<WorkloadRef, WorkloadStatus>(references.Count);
            foreach (var reference in references)
            {
                unreachable[reference] = Unknown();
            }

            return unreachable;
        }

        var statuses = new Dictionary<WorkloadRef, WorkloadStatus>(references.Count);
        foreach (var reference in references)
        {
            statuses[reference] = await ResolveAsync(client, listed, reference.InstanceId, cancellationToken).ConfigureAwait(false);
        }

        return statuses;
    }

    private static async Task<WorkloadStatus> ResolveAsync(
        IDockerClient client,
        IList<ContainerListResponse>? listed,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var mine = (listed ?? new List<ContainerListResponse>())
                .Where(c => HasInstanceLabel(c, instanceId))
                .OrderByDescending(c => c.Created)
                .ToList();

            if (mine.Count == 0)
            {
                return WorkloadStatus.NotFound("No docker container matches the instance label selector.");
            }

            var chosen = mine.FirstOrDefault(IsRunningListState) ?? mine[0];
            var observedImage = string.IsNullOrWhiteSpace(chosen.Image) ? null : chosen.Image;

            ContainerInspectResponse inspected;
            try
            {
                inspected = await client.Containers
                    .InspectContainerAsync(chosen.ID, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return WorkloadStatus.NotFound("The docker container was removed between the list and the status read.");
            }

            return Map(inspected, observedImage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unknown();
        }
    }

    internal static WorkloadStatus Map(ContainerInspectResponse inspected, string? observedImage)
    {
        ArgumentNullException.ThrowIfNull(inspected);

        var state = inspected.State;
        var restartCount = ClampRestartCount(inspected.RestartCount);
        var status = state?.Status;

        var restarting = state is { Restarting: true }
            || string.Equals(status, "restarting", StringComparison.OrdinalIgnoreCase);

        if (state is { Running: true } && !restarting)
        {
            var health = state.Health?.Status;
            if (IsHealthNotReady(health))
            {
                return new WorkloadStatus
                {
                    Phase = WorkloadProviderPhase.Running,
                    ProviderReady = false,
                    RestartCount = restartCount,
                    Message = string.Format(CultureInfo.InvariantCulture, "Container running; healthcheck {0}.", health),
                    ObservedImage = observedImage,
                };
            }

            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Ready,
                ProviderReady = true,
                RestartCount = restartCount,
                Message = HasHealthcheck(health) ? "Container running and healthy." : "Container running.",
                ObservedImage = observedImage,
            };
        }

        if (string.Equals(status, "created", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Pending,
                ProviderReady = false,
                RestartCount = restartCount,
                Message = "Container created but not started yet.",
                ObservedImage = observedImage,
            };
        }

        if (state is { Paused: true } || string.Equals(status, "paused", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Running,
                ProviderReady = false,
                RestartCount = restartCount,
                Message = "Container paused.",
                ObservedImage = observedImage,
            };
        }

        return new WorkloadStatus
        {
            Phase = WorkloadProviderPhase.Failed,
            ProviderReady = false,
            RestartCount = restartCount,
            Message = BuildFailedMessage(state, restartCount),
            ObservedImage = observedImage,
        };
    }

    private static WorkloadStatus Unknown() => new()
    {
        Phase = WorkloadProviderPhase.Unknown,
        ProviderReady = false,
        Message = "Docker daemon unreachable — workload status is unknown.",
    };

    private static bool IsHealthNotReady(string? health) =>
        string.Equals(health, "starting", StringComparison.OrdinalIgnoreCase)
        || string.Equals(health, "unhealthy", StringComparison.OrdinalIgnoreCase);

    private static bool HasHealthcheck(string? health) =>
        !string.IsNullOrEmpty(health) && !string.Equals(health, "none", StringComparison.OrdinalIgnoreCase);

    private static string BuildFailedMessage(State? state, int restartCount)
    {
        var statusText = string.IsNullOrEmpty(state?.Status) ? "stopped" : state!.Status;
        var message = string.Format(CultureInfo.InvariantCulture, "Container {0} (exit code {1})", statusText, state?.ExitCode ?? 0);

        if (restartCount > 0)
        {
            message = string.Format(CultureInfo.InvariantCulture, "{0}, restarts: {1}", message, restartCount);
        }

        return message + ".";
    }

    private static int ClampRestartCount(long value) =>
        value <= 0 ? 0 : value >= int.MaxValue ? int.MaxValue : (int)value;

    private static IDictionary<string, IDictionary<string, bool>> InstanceLabelFilter(Guid instanceId) =>
        new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
        {
            ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [$"{DockerProviderConstants.InstanceLabelKey}={instanceId:D}"] = true,
            },
        };

    private static IDictionary<string, IDictionary<string, bool>> AnyInstanceLabelFilter() =>
        new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
        {
            ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [DockerProviderConstants.InstanceLabelKey] = true,
            },
        };

    private static bool HasInstanceLabel(ContainerListResponse container, Guid instanceId) =>
        container.Labels is not null
        && container.Labels.TryGetValue(DockerProviderConstants.InstanceLabelKey, out var value)
        && string.Equals(value, instanceId.ToString("D"), StringComparison.Ordinal);

    private static bool IsRunningListState(ContainerListResponse container) =>
        string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase);
}
