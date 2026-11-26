using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerJournaledRecreate : IDockerJournaledRecreate
{
    private readonly IDockerContainerManager _containers;

    private static readonly TimeSpan GatePollInterval = TimeSpan.FromMilliseconds(250);
    private const int MaxGatePolls = 40;

    public DockerJournaledRecreate(IDockerContainerManager containers)
    {
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
    }

    public async Task<WorkloadApplyResult> RecreateAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        DockerContainerRef currentContainer,
        CreateContainerParameters desiredContainer,
        IDockerRollbackCoordinator rollbackCoordinator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(currentContainer);
        ArgumentNullException.ThrowIfNull(desiredContainer);
        ArgumentNullException.ThrowIfNull(rollbackCoordinator);

        var targetSpecHash = LabelValue(desiredContainer.Labels, DockerProviderConstants.SpecHashLabelKey) ?? spec.SpecHash;
        var targetRestartNonce = spec.RestartNonce.ToString(CultureInfo.InvariantCulture);
        var targetUnhashedEnvDigest = LabelValue(desiredContainer.Labels, DockerProviderConstants.UnhashedEnvDigestLabelKey);
        var targetCredentialDigest = LabelValue(desiredContainer.Labels, DockerProviderConstants.CredentialDigestLabelKey);
        var canonicalName = string.IsNullOrWhiteSpace(desiredContainer.Name) ? currentContainer.Name : desiredContainer.Name;

        try
        {
            var observed = await ObserveInstanceContainersAsync(client, spec.InstanceId, cancellationToken).ConfigureAwait(false);
            var decision = DockerRecoveryDecisionTable.Decide(
                observed, targetSpecHash, targetRestartNonce, targetUnhashedEnvDigest, targetCredentialDigest);

            var obstruction = await ReconcileObstructingContainersAsync(client, decision.ObstructingContainers, canonicalName, cancellationToken).ConfigureAwait(false);
            if (obstruction is not null)
            {
                return obstruction;
            }

            return decision.Action switch
            {
                DockerRecoveryAction.Converged =>
                    WorkloadApplyResult.Applied(spec.SpecHash, decision.Rationale),

                DockerRecoveryAction.FinishRemovePrevious =>
                    await FinishRemovePreviousAsync(client, spec, decision.PreviousContainer!, cancellationToken).ConfigureAwait(false),

                DockerRecoveryAction.RunFullSwap =>
                    await RunFullSwapAsync(client, spec, desiredContainer, decision.PreviousContainer!, canonicalName, rollbackCoordinator, cancellationToken).ConfigureAwait(false),

                DockerRecoveryAction.ResumeCreateNew =>
                    await CreateStartGateAsync(client, spec, desiredContainer, decision.PreviousContainer, rollbackCoordinator, cancellationToken).ConfigureAwait(false),

                DockerRecoveryAction.ResumeStartNew =>
                    await StartGateExistingAsync(client, spec, decision.NewContainer!, decision.PreviousContainer, rollbackCoordinator, cancellationToken).ConfigureAwait(false),

                DockerRecoveryAction.CreateFresh =>
                    await CreateStartGateAsync(client, spec, desiredContainer, previous: null, rollbackCoordinator, cancellationToken).ConfigureAwait(false),

                _ => WorkloadApplyResult.Failed(WorkloadApplyReason.Error, "unrecognized docker recovery action"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapApplyFailure(Classify(ex), "the docker daemon call failed during journaled recreate: " + ex.Message);
        }
    }

    private async Task<WorkloadApplyResult> RunFullSwapAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        CreateContainerParameters desiredContainer,
        DockerContainerRef old,
        string canonicalName,
        IDockerRollbackCoordinator rollbackCoordinator,
        CancellationToken cancellationToken)
    {
        var stopped = await _containers.StopContainerAsync(client, old.Id, cancellationToken).ConfigureAwait(false);
        if (!stopped.Succeeded)
        {
            return MapApplyFailure(stopped.FailureKind, stopped.Message);
        }

        var renamedName = DockerRecoveryDecisionTable.BuildRenamedName(canonicalName, old.SpecHash);
        var renamed = await TryRenameAsync(client, old.Id, renamedName, cancellationToken).ConfigureAwait(false);
        if (!renamed.Succeeded)
        {
            await RestartPreviousAsync(client, old, cancellationToken).ConfigureAwait(false);
            return MapApplyFailure(renamed.FailureKind, renamed.Message);
        }

        var renamedOld = old with { Name = renamedName, IsRunning = false };
        return await CreateStartGateAsync(client, spec, desiredContainer, renamedOld, rollbackCoordinator, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkloadApplyResult> CreateStartGateAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        CreateContainerParameters desiredContainer,
        DockerContainerRef? previous,
        IDockerRollbackCoordinator rollbackCoordinator,
        CancellationToken cancellationToken)
    {
        var (quiescedPrevious, quiesceFailure) = await QuiescePreviousIfRunningAsync(client, previous, cancellationToken).ConfigureAwait(false);
        if (quiesceFailure is not null)
        {
            return quiesceFailure;
        }

        previous = quiescedPrevious;

        var created = await DockerAttachmentSequence
            .CreateConnectAndStartAsync(
                _containers,
                client,
                desiredContainer,
                DockerAttachmentSequence.DesiredAttachments(spec),
                cancellationToken)
            .ConfigureAwait(false);
        if (!created.Succeeded)
        {
            if (created.Container is not null)
            {
                var rollback = await BestEffortRollbackAsync(rollbackCoordinator, client, spec, created.Container, previous, cancellationToken).ConfigureAwait(false);
                return MapApplyFailure(created.FailureKind, WithRollback(created.Message, rollback));
            }

            if (previous is not null)
            {
                await RestartPreviousAsync(client, previous, cancellationToken).ConfigureAwait(false);
            }

            return MapApplyFailure(created.FailureKind, created.Message);
        }

        return await GateThenRemovePreviousAsync(client, spec, created.Container!, previous, rollbackCoordinator, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkloadApplyResult> StartGateExistingAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        DockerContainerRef newContainer,
        DockerContainerRef? previous,
        IDockerRollbackCoordinator rollbackCoordinator,
        CancellationToken cancellationToken)
    {
        if (!newContainer.IsRunning)
        {
            var (quiescedPrevious, quiesceFailure) = await QuiescePreviousIfRunningAsync(client, previous, cancellationToken).ConfigureAwait(false);
            if (quiesceFailure is not null)
            {
                return quiesceFailure;
            }

            previous = quiescedPrevious;

            var reconciled = await DockerAttachmentSequence
                .ReconcileAttachmentsAsync(
                    _containers,
                    client,
                    newContainer.Id,
                    DockerAttachmentSequence.DesiredAttachments(spec),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!reconciled.Succeeded)
            {
                var rollback = await BestEffortRollbackAsync(rollbackCoordinator, client, spec, newContainer, previous, cancellationToken).ConfigureAwait(false);
                return MapApplyFailure(reconciled.FailureKind, WithRollback(reconciled.Message, rollback));
            }

            var started = await _containers.StartContainerAsync(client, newContainer.Id, cancellationToken).ConfigureAwait(false);
            if (!started.Succeeded)
            {
                var rollback = await BestEffortRollbackAsync(rollbackCoordinator, client, spec, newContainer, previous, cancellationToken).ConfigureAwait(false);
                return MapApplyFailure(started.FailureKind, WithRollback(started.Message, rollback));
            }
        }

        return await GateThenRemovePreviousAsync(client, spec, newContainer, previous, rollbackCoordinator, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(DockerContainerRef? Previous, WorkloadApplyResult? Failure)> QuiescePreviousIfRunningAsync(
        IDockerClient client,
        DockerContainerRef? previous,
        CancellationToken cancellationToken)
    {
        if (previous is not { IsRunning: true })
        {
            return (previous, null);
        }

        var stopped = await _containers.StopContainerAsync(client, previous.Id, cancellationToken).ConfigureAwait(false);
        if (!stopped.Succeeded)
        {
            return (previous, MapApplyFailure(stopped.FailureKind, stopped.Message));
        }

        return (previous with { IsRunning = false }, null);
    }

    private async Task<WorkloadApplyResult?> ReconcileObstructingContainersAsync(
        IDockerClient client,
        IReadOnlyList<DockerContainerRef> obstructing,
        string canonicalName,
        CancellationToken cancellationToken)
    {
        foreach (var stale in obstructing)
        {
            var blocksConvergence = stale.IsRunning
                || string.Equals(stale.Name, canonicalName, StringComparison.Ordinal);

            var removed = await _containers.RemoveContainerAsync(client, stale.Id, cancellationToken).ConfigureAwait(false);
            if (!removed.Succeeded && blocksConvergence)
            {
                return MapApplyFailure(removed.FailureKind, removed.Message);
            }
        }

        return null;
    }

    private async Task<WorkloadApplyResult> GateThenRemovePreviousAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        DockerContainerRef candidate,
        DockerContainerRef? previous,
        IDockerRollbackCoordinator rollbackCoordinator,
        CancellationToken cancellationToken)
    {
        var gate = await HealthGateAsync(client, candidate.Id, cancellationToken).ConfigureAwait(false);
        if (!gate.Succeeded)
        {
            var rollback = await BestEffortRollbackAsync(rollbackCoordinator, client, spec, candidate, previous, cancellationToken).ConfigureAwait(false);
            return MapApplyFailure(gate.FailureKind, WithRollback(gate.Message, rollback));
        }

        if (previous is not null)
        {
            await _containers.RemoveContainerAsync(client, previous.Id, cancellationToken).ConfigureAwait(false);
        }

        return WorkloadApplyResult.Applied(spec.SpecHash);
    }

    private async Task<WorkloadApplyResult> FinishRemovePreviousAsync(
        IDockerClient client,
        WorkloadDesiredSpec spec,
        DockerContainerRef previous,
        CancellationToken cancellationToken)
    {
        await _containers.RemoveContainerAsync(client, previous.Id, cancellationToken).ConfigureAwait(false);
        return WorkloadApplyResult.Applied(spec.SpecHash);
    }

    private static async Task<DockerOperationResult> HealthGateAsync(
        IDockerClient client,
        string containerId,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < MaxGatePolls; i++)
        {
            await Task.Delay(GatePollInterval, cancellationToken).ConfigureAwait(false);

            ContainerInspectResponse inspected;
            try
            {
                inspected = await client.Containers.InspectContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return DockerOperationResult.Fail(DockerFailureKind.Rejected, "the new container vanished during the health gate");
            }

            var state = inspected.State;

            if (inspected.RestartCount > 0 || state?.Restarting == true)
            {
                return DockerOperationResult.Fail(DockerFailureKind.Rejected, "the new container is crash-looping during the health gate");
            }

            if (state?.Dead == true || string.Equals(state?.Status, "exited", StringComparison.OrdinalIgnoreCase))
            {
                return DockerOperationResult.Fail(DockerFailureKind.Rejected, "the new container exited during the health gate");
            }

            if (state?.Running == true)
            {
                return DockerOperationResult.Ok();
            }
        }

        return DockerOperationResult.Fail(DockerFailureKind.Error, "the new container did not reach running within the health-gate budget");
    }

    private static async Task<IReadOnlyList<DockerContainerRef>> ObserveInstanceContainersAsync(
        IDockerClient client,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var listed = await client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                {
                    ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                    {
                        [$"{DockerProviderConstants.InstanceLabelKey}={instanceId:D}"] = true,
                    },
                },
            },
            cancellationToken).ConfigureAwait(false);

        return (listed ?? new List<ContainerListResponse>())
            .Where(c => c.Labels is not null
                && c.Labels.TryGetValue(DockerProviderConstants.InstanceLabelKey, out var owner)
                && string.Equals(owner, instanceId.ToString("D"), StringComparison.Ordinal))
            .Select(ToRef)
            .ToList();
    }

    private async Task<DockerOperationResult> TryRenameAsync(
        IDockerClient client,
        string containerId,
        string newName,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.Containers
                .RenameContainerAsync(containerId, new ContainerRenameParameters { NewName = newName }, cancellationToken)
                .ConfigureAwait(false);
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

    private async Task RestartPreviousAsync(IDockerClient client, DockerContainerRef previous, CancellationToken cancellationToken)
    {
        await _containers.StartContainerAsync(client, previous.Id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DockerRollbackResult?> BestEffortRollbackAsync(
        IDockerRollbackCoordinator rollbackCoordinator,
        IDockerClient client,
        WorkloadDesiredSpec spec,
        DockerContainerRef failedCandidate,
        DockerContainerRef? previous,
        CancellationToken cancellationToken)
    {
        try
        {
            return await rollbackCoordinator.RollbackAsync(client, spec, failedCandidate, previous, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static string? WithRollback(string? failure, DockerRollbackResult? rollback)
    {
        if (string.IsNullOrEmpty(rollback?.Message))
        {
            return failure;
        }

        return string.IsNullOrEmpty(failure) ? rollback.Message : $"{failure}; {rollback.Message}";
    }

    private static DockerContainerRef ToRef(ContainerListResponse container) => new()
    {
        Id = container.ID,
        Name = NormalizeContainerName(container.Names),
        SpecHash = container.Labels is not null
            && container.Labels.TryGetValue(DockerProviderConstants.SpecHashLabelKey, out var hash)
                ? hash
                : null,
        RestartNonce = container.Labels is not null
            && container.Labels.TryGetValue(DockerProviderConstants.RestartNonceLabelKey, out var nonce)
                ? nonce
                : null,
        UnhashedEnvDigest = LabelValue(container.Labels, DockerProviderConstants.UnhashedEnvDigestLabelKey),
        CredentialDigest = LabelValue(container.Labels, DockerProviderConstants.CredentialDigestLabelKey),
        IsRunning = string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase),
    };

    private static string NormalizeContainerName(IList<string>? names)
    {
        var first = names?.FirstOrDefault();
        if (string.IsNullOrEmpty(first))
        {
            return string.Empty;
        }

        return first.StartsWith('/') ? first[1..] : first;
    }

    private static string? LabelValue(IDictionary<string, string>? labels, string key)
        => labels is not null && labels.TryGetValue(key, out var value) ? value : null;

    private static WorkloadApplyResult MapApplyFailure(DockerFailureKind kind, string? message) => kind switch
    {
        DockerFailureKind.Unreachable => WorkloadApplyResult.Failed(WorkloadApplyReason.ProviderUnreachable, message),
        DockerFailureKind.Rejected => WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected, message),
        _ => WorkloadApplyResult.Failed(WorkloadApplyReason.Error, message),
    };

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
