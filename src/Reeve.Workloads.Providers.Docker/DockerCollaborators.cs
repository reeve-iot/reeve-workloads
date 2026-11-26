using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;


internal enum DockerFailureKind
{
    None = 0,

    Unreachable = 1,

    Rejected = 2,

    Error = 3,
}

internal sealed record DockerOperationResult
{
    public required bool Succeeded { get; init; }

    public DockerFailureKind FailureKind { get; init; } = DockerFailureKind.None;

    public string? Message { get; init; }

    public static DockerOperationResult Ok(string? message = null) => new() { Succeeded = true, Message = message };

    public static DockerOperationResult Fail(DockerFailureKind kind, string? message) =>
        new() { Succeeded = false, FailureKind = kind, Message = message };
}

internal sealed record DockerContainerRef
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? SpecHash { get; init; }

    public string? RestartNonce { get; init; }

    public string? UnhashedEnvDigest { get; init; }

    public string? CredentialDigest { get; init; }

    public bool IsRunning { get; init; }
}

internal sealed record DockerContainerMutationResult
{
    public required bool Succeeded { get; init; }

    public DockerFailureKind FailureKind { get; init; } = DockerFailureKind.None;

    public string? Message { get; init; }

    public DockerContainerRef? Container { get; init; }

    public static DockerContainerMutationResult Ok(DockerContainerRef container) =>
        new() { Succeeded = true, Container = container };

    public static DockerContainerMutationResult Fail(DockerFailureKind kind, string? message, DockerContainerRef? container = null) =>
        new() { Succeeded = false, FailureKind = kind, Message = message, Container = container };
}

internal sealed record DockerNetworkEnsureResult
{
    public required bool Succeeded { get; init; }

    public DockerFailureKind FailureKind { get; init; } = DockerFailureKind.None;

    public string? Message { get; init; }

    public string? NetworkName { get; init; }

    public static DockerNetworkEnsureResult Ok(string networkName) =>
        new() { Succeeded = true, NetworkName = networkName };

    public static DockerNetworkEnsureResult Fail(DockerFailureKind kind, string? message) =>
        new() { Succeeded = false, FailureKind = kind, Message = message };
}

internal sealed record DockerPrivilegeRendering
{
    public required HostConfig HostConfig { get; init; }

    public string? User { get; init; }
}

internal sealed record DockerResourceRendering
{
    public required HostConfig HostConfig { get; init; }

    public required IReadOnlyDictionary<string, string> MetricsLabels { get; init; }
}

internal sealed record DockerContainerRenderContext
{
    public required WorkloadDesiredSpec Spec { get; init; }

    public required string ContainerName { get; init; }

    public IReadOnlyDictionary<string, string> Credentials { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public required string SharedNetworkName { get; init; }

    public required DockerPrivilegeRendering Privilege { get; init; }

    public required DockerResourceRendering Resource { get; init; }

    public required HostConfig NetworkHostConfig { get; init; }
}

internal sealed record DockerRollbackResult
{
    public required bool PreviousRestored { get; init; }

    public WorkloadLogs? CapturedEvidence { get; init; }

    public string? Message { get; init; }
}


internal sealed record DockerPullProgress
{
    public int? Percent { get; init; }

    public required string Message { get; init; }
}

internal interface IDockerImagePuller
{
    Task<DockerOperationResult> PullAsync(
        IDockerClient client,
        string imageReference,
        DockerProviderSettings settings,
        IProgress<DockerPullProgress>? progress,
        CancellationToken cancellationToken);
}


internal sealed record DockerAttachableNetwork
{
    public required string Name { get; init; }
}

internal sealed record DockerNetworkingPlan
{
    public required HostConfig HostConfig { get; init; }

    public IReadOnlyList<string> Attachments { get; init; } = [];
}

internal sealed record DockerAttachableNetworkListResult
{
    public required bool Succeeded { get; init; }

    public DockerFailureKind FailureKind { get; init; } = DockerFailureKind.None;

    public string? Message { get; init; }

    public IReadOnlyList<DockerAttachableNetwork> Networks { get; init; } = [];

    public static DockerAttachableNetworkListResult Ok(IReadOnlyList<DockerAttachableNetwork> networks) =>
        new() { Succeeded = true, Networks = networks };

    public static DockerAttachableNetworkListResult Fail(DockerFailureKind kind, string? message) =>
        new() { Succeeded = false, FailureKind = kind, Message = message };
}

internal interface IDockerNetworkManager
{
    Task<DockerNetworkEnsureResult> EnsureSharedNetworkAsync(
        IDockerClient client,
        DockerProviderSettings settings,
        CancellationToken cancellationToken);

    Task<DockerAttachableNetworkListResult> ListAttachableNetworksAsync(
        IDockerClient client,
        DockerProviderSettings settings,
        CancellationToken cancellationToken);

    DockerNetworkingPlan RenderNetworking(WorkloadDesiredSpec spec, string sharedNetworkName);
}


internal interface IDockerVolumeManager
{
    Task<DockerOperationResult> EnsureVolumesAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        DockerProviderSettings settings,
        CancellationToken cancellationToken);

    Task<DockerOperationResult> RemoveVolumesAsync(
        IDockerClient client,
        WorkloadRef reference,
        CancellationToken cancellationToken);
}


internal interface IDockerContainerManager
{
    CreateContainerParameters BuildContainerParameters(DockerContainerRenderContext context);

    Task<DockerContainerRef?> FindContainerAsync(
        IDockerClient client,
        WorkloadRef reference,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DockerContainerRef>> FindContainersAsync(
        IDockerClient client,
        WorkloadRef reference,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> ReadHeldEnvAsync(
        IDockerClient client,
        DockerContainerRef container,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken);

    Task<DockerContainerMutationResult> CreateAndStartAsync(
        IDockerClient client,
        CreateContainerParameters parameters,
        CancellationToken cancellationToken);

    Task<DockerOperationResult> StartContainerAsync(
        IDockerClient client,
        string containerId,
        CancellationToken cancellationToken);

    Task<DockerOperationResult> StopContainerAsync(
        IDockerClient client,
        string containerId,
        CancellationToken cancellationToken);

    Task<DockerOperationResult> RemoveContainerAsync(
        IDockerClient client,
        string containerId,
        CancellationToken cancellationToken);
}


internal interface IDockerPrivilegeRenderer
{
    DockerPrivilegeRendering RenderPrivileges(WorkloadDesiredSpec spec);
}


internal interface IDockerResourceRenderer
{
    DockerResourceRendering RenderResources(WorkloadDesiredSpec spec);
}


internal interface IDockerDeploymentValidator
{
    IReadOnlyList<PreflightCheck> Validate(WorkloadDesiredSpec spec);
}


internal interface IDockerDaemonGate
{
    Task<IReadOnlyList<PreflightCheck>> EvaluateAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        CancellationToken cancellationToken);
}


internal interface IDockerJournaledRecreate
{
    Task<WorkloadApplyResult> RecreateAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        DockerContainerRef currentContainer,
        CreateContainerParameters desiredContainer,
        IDockerRollbackCoordinator rollbackCoordinator,
        CancellationToken cancellationToken);
}


internal interface IDockerLogReader
{
    Task<WorkloadLogs> ReadAsync(
        IDockerClient client,
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        CancellationToken cancellationToken);
}


internal interface IDockerRollbackCoordinator
{
    Task<DockerRollbackResult> RollbackAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        DockerContainerRef failedCandidate,
        DockerContainerRef? previousContainer,
        CancellationToken cancellationToken);
}


internal interface IDockerStatusMapper
{
    Task<WorkloadStatus> GetStatusAsync(
        IDockerClient client,
        WorkloadRef reference,
        CancellationToken cancellationToken);

    async Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
        IDockerClient client,
        IReadOnlyList<WorkloadRef> references,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);

        var statuses = new Dictionary<WorkloadRef, WorkloadStatus>(references.Count);
        foreach (var reference in references)
        {
            statuses[reference] = await GetStatusAsync(client, reference, cancellationToken).ConfigureAwait(false);
        }

        return statuses;
    }
}

