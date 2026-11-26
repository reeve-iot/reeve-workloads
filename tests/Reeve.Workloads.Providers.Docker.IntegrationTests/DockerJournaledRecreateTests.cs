using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
public sealed class DockerJournaledRecreateTests : IAsyncLifetime
{
    private const string GroupId = "com.reeve.recreate-test";
    private const string WorkloadId = "rc";

    private const string BusyboxRepository = "busybox";
    private const string BusyboxTag = "1.36";
    private const string BusyboxImage = BusyboxRepository + ":" + BusyboxTag;

    private const string OldSpecHash = "oldhash0011223344";
    private const string NewSpecHash = "newhash5566778899";

    private const string MiddleSpecHash = "midhash2233445566";

    private readonly DockerDaemonFixture _docker;
    private readonly DockerContainerManager _containers = new();
    private readonly DockerJournaledRecreate _sut;

    private readonly List<Guid> _instanceIds = new();

    public DockerJournaledRecreateTests(DockerDaemonFixture docker)
    {
        _docker = docker;
        _sut = new DockerJournaledRecreate(_containers);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task A_secret_only_change_reaches_the_running_container_when_the_restart_nonce_moves()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = NewInstance();
        var reference = ReferenceFor(instanceId);
        var rollback = new RecordingRollbackCoordinator(_containers);
        var (initialSpec, containerName) = BuildSpec(instanceId, OldSpecHash);
        initialSpec = initialSpec with
        {
            Env = [new ResolvedEnvVar { Name = "DB_PASSWORD", Kind = ResolvedEnvKind.SecretRef, SecretRef = "db-password" }],
            SecretMaterial = new Dictionary<string, string> { ["DB_PASSWORD"] = "old" },
        };
        var initialParameters = _containers.BuildContainerParameters(
            BuildContext(initialSpec, containerName, "rwt_secret_nonce_old", "bridge"));
        initialParameters.Cmd = new[] { "sleep", "3600" };
        var initial = await _containers.CreateAndStartAsync(client, initialParameters, CancellationToken.None);
        initial.Succeeded.Should().BeTrue(initial.Message);

        var current = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        current.Should().NotBeNull();
        var targetSpec = initialSpec with
        {
            RestartNonce = 1,
            SecretMaterial = new Dictionary<string, string> { ["DB_PASSWORD"] = "new" },
        };
        var desired = _containers.BuildContainerParameters(
            BuildContext(targetSpec, containerName, "rwt_secret_nonce_new", "bridge"));
        desired.Cmd = new[] { "sleep", "3600" };

        var result = await _sut.RecreateAsync(client, targetSpec, current!, desired, rollback, CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Message);
        var afterwards = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        afterwards.Should().NotBeNull();
        var inspected = await client.Containers.InspectContainerAsync(afterwards!.Id, CancellationToken.None);
        inspected.Config!.Env.Should().Contain("DB_PASSWORD=new");
        (await ListInstanceContainersAsync(client, instanceId)).Should().ContainSingle();
    }

    public async Task DisposeAsync()
    {
        if (_docker.Client is not { } client)
        {
            return;
        }

        foreach (var instanceId in _instanceIds.Distinct())
        {
            List<ContainerListResponse> mine;
            try
            {
                var listed = await client.Containers.ListContainersAsync(
                    new ContainersListParameters
                    {
                        All = true,
                        Filters = new Dictionary<string, IDictionary<string, bool>>
                        {
                            ["label"] = new Dictionary<string, bool>
                            {
                                [$"{DockerProviderConstants.InstanceLabelKey}={instanceId:D}"] = true,
                            },
                        },
                    },
                    CancellationToken.None);
                mine = listed.ToList();
            }
            catch (DockerApiException)
            {
                continue;
            }

            foreach (var id in mine.Select(c => c.ID))
            {
                try
                {
                    await client.Containers.RemoveContainerAsync(
                        id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
                }
                catch (DockerApiException)
                {
                }
            }
        }
    }

    [SkippableFact]
    public async Task Recreate_happy_swap_new_runs_old_removed_result_Applied()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = NewInstance();
        var reference = ReferenceFor(instanceId);
        var rollback = new RecordingRollbackCoordinator(_containers);

        var old = await CreateBlockingContainerAsync(client, instanceId, OldSpecHash);
        old.Succeeded.Should().BeTrue(old.Message);
        old.Container!.IsRunning.Should().BeTrue();

        var current = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        current.Should().NotBeNull();
        current!.SpecHash.Should().Be(OldSpecHash);

        var (newSpec, desired) = BuildDesired(instanceId, NewSpecHash, blocking: true);

        var result = await _sut.RecreateAsync(client, newSpec, current, desired, rollback, CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Reason.Should().Be(WorkloadApplyReason.Applied);
        result.AppliedSpecHash.Should().Be(NewSpecHash);
        rollback.Invocations.Should().Be(0, "a clean swap never rolls back");

        var afterwards = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        afterwards.Should().NotBeNull();
        afterwards!.SpecHash.Should().Be(NewSpecHash);
        afterwards.IsRunning.Should().BeTrue();
        (await ContainerExistsAsync(client, old.Container.Id)).Should().BeFalse("the old container was removed after the swap");
    }

    [SkippableFact]
    public async Task Recreate_start_failure_rolls_back_old_kept_new_cleaned_result_Failed()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = NewInstance();
        var reference = ReferenceFor(instanceId);
        var rollback = new RecordingRollbackCoordinator(_containers);

        var old = await CreateBlockingContainerAsync(client, instanceId, OldSpecHash);
        old.Succeeded.Should().BeTrue(old.Message);
        var oldId = old.Container!.Id;

        var current = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        current.Should().NotBeNull();

        var (newSpec, desired) = BuildDesired(instanceId, NewSpecHash, blocking: true);
        desired.Cmd = new[] { "/nonexistent-binary-zzz-9c1e" };

        var result = await _sut.RecreateAsync(client, newSpec, current, desired, rollback, CancellationToken.None);

        result.Succeeded.Should().BeFalse("the new container could not start");
        result.Reason.Should().NotBe(WorkloadApplyReason.Applied);
        rollback.Invocations.Should().Be(1, "a failed StartedNew rolls back through the coordinator");
        rollback.LastPrevious.Should().NotBeNull("the renamed old is passed as the rollback previous");
        DockerRecoveryDecisionTable.IsRenamed(rollback.LastPrevious!).Should().BeTrue(
            "the previous handed to rollback is the renamed old");

        (await ContainerExistsAsync(client, oldId)).Should().BeTrue("the previous container is kept on rollback");
        (await IsRunningAsync(client, oldId)).Should().BeTrue("the coordinator restarted the previous");
        (await ContainerExistsAsync(client, rollback.LastFailedCandidate!.Id))
            .Should().BeFalse("the failed candidate is cleaned up");

        var survivor = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        survivor.Should().NotBeNull();
        survivor!.Id.Should().Be(oldId);
        survivor.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Table_old_only_returns_RunFullSwap()
    {
        var observed = new[] { Ref("id-old", Canonical(), OldSpecHash, running: true) };

        var decision = DockerRecoveryDecisionTable.Decide(observed, NewSpecHash);

        decision.Action.Should().Be(DockerRecoveryAction.RunFullSwap);
        decision.NewContainer.Should().BeNull();
        decision.PreviousContainer!.Id.Should().Be("id-old");
        decision.ObstructingContainers.Should().BeEmpty("a single old is the previous, nothing to reconcile");
    }

    [Fact]
    public void Table_old_renamed_no_new_returns_ResumeCreateNew()
    {
        var renamed = DockerRecoveryDecisionTable.BuildRenamedName(Canonical(), OldSpecHash);
        var observed = new[] { Ref("id-old", renamed, OldSpecHash, running: false) };

        var decision = DockerRecoveryDecisionTable.Decide(observed, NewSpecHash);

        decision.Action.Should().Be(DockerRecoveryAction.ResumeCreateNew);
        decision.NewContainer.Should().BeNull();
        decision.PreviousContainer!.Id.Should().Be("id-old");
        DockerRecoveryDecisionTable.IsRenamed(decision.PreviousContainer!).Should().BeTrue();
        decision.ObstructingContainers.Should().BeEmpty("only the renamed old exists, nothing else to reconcile");
    }

    [Fact]
    public void Table_both_exist_new_running_returns_FinishRemovePrevious()
    {
        var renamed = DockerRecoveryDecisionTable.BuildRenamedName(Canonical(), OldSpecHash);
        var observed = new[]
        {
            Ref("id-new", Canonical(), NewSpecHash, running: true),
            Ref("id-old", renamed, OldSpecHash, running: false),
        };

        var decision = DockerRecoveryDecisionTable.Decide(observed, NewSpecHash);

        decision.Action.Should().Be(DockerRecoveryAction.FinishRemovePrevious);
        decision.NewContainer!.Id.Should().Be("id-new");
        decision.PreviousContainer!.Id.Should().Be("id-old");
        decision.ObstructingContainers.Should().BeEmpty("one running new + one renamed previous, nothing else to reconcile");
    }

    [Fact]
    public void Table_both_exist_new_not_running_returns_ResumeStartNew()
    {
        var renamed = DockerRecoveryDecisionTable.BuildRenamedName(Canonical(), OldSpecHash);
        var observed = new[]
        {
            Ref("id-new", Canonical(), NewSpecHash, running: false),
            Ref("id-old", renamed, OldSpecHash, running: false),
        };

        var decision = DockerRecoveryDecisionTable.Decide(observed, NewSpecHash);

        decision.Action.Should().Be(DockerRecoveryAction.ResumeStartNew);
        decision.NewContainer!.Id.Should().Be("id-new");
        decision.PreviousContainer!.Id.Should().Be("id-old");
        decision.ObstructingContainers.Should().BeEmpty("one created new + one renamed previous, nothing else to reconcile");
    }

    [Fact]
    public void Table_second_old_canonical_running_surfaces_obstruction_ResumeCreateNew()
    {
        var renamed = DockerRecoveryDecisionTable.BuildRenamedName(Canonical(), OldSpecHash);
        var observed = new[]
        {
            Ref("id-canonical-mid", Canonical(), MiddleSpecHash, running: true),
            Ref("id-renamed-old", renamed, OldSpecHash, running: false),
        };

        var decision = DockerRecoveryDecisionTable.Decide(observed, NewSpecHash);

        decision.Action.Should().Be(DockerRecoveryAction.ResumeCreateNew);
        decision.NewContainer.Should().BeNull();
        decision.PreviousContainer!.Id.Should().Be("id-renamed-old", "the retained rollback target is the renamed old, kept out of the canonical name");
        DockerRecoveryDecisionTable.IsRenamed(decision.PreviousContainer!).Should().BeTrue();
        decision.ObstructingContainers.Select(c => c.Id).Should()
            .ContainSingle().Which.Should().Be("id-canonical-mid",
                "the still-running canonical-named old must be reconciled so the target can reclaim the canonical name");
    }

    [Fact]
    public void Table_second_old_canonical_running_and_new_running_surfaces_obstruction_FinishRemovePrevious()
    {
        var renamed = DockerRecoveryDecisionTable.BuildRenamedName(Canonical(), OldSpecHash);
        var observed = new[]
        {
            Ref("id-new", Canonical(), NewSpecHash, running: true),
            Ref("id-renamed-old", renamed, OldSpecHash, running: false),
            Ref("id-stray-running", "reeve-recreate-test-rc-stray", MiddleSpecHash, running: true),
        };

        var decision = DockerRecoveryDecisionTable.Decide(observed, NewSpecHash);

        decision.Action.Should().Be(DockerRecoveryAction.FinishRemovePrevious);
        decision.NewContainer!.Id.Should().Be("id-new");
        decision.PreviousContainer!.Id.Should().Be("id-renamed-old");
        decision.ObstructingContainers.Select(c => c.Id).Should()
            .ContainSingle().Which.Should().Be("id-stray-running");
    }

    [Fact]
    public void Table_new_only_running_returns_Converged()
    {
        var observed = new[] { Ref("id-new", Canonical(), NewSpecHash, running: true) };

        var decision = DockerRecoveryDecisionTable.Decide(observed, NewSpecHash);

        decision.Action.Should().Be(DockerRecoveryAction.Converged);
        decision.NewContainer!.Id.Should().Be("id-new");
        decision.PreviousContainer.Should().BeNull();
        decision.ObstructingContainers.Should().BeEmpty("only the running new exists, nothing to reconcile");
    }

    [Fact]
    public void Table_no_containers_returns_CreateFresh()
    {
        var decision = DockerRecoveryDecisionTable.Decide(Array.Empty<DockerContainerRef>(), NewSpecHash);

        decision.Action.Should().Be(DockerRecoveryAction.CreateFresh);
        decision.NewContainer.Should().BeNull();
        decision.PreviousContainer.Should().BeNull();
        decision.ObstructingContainers.Should().BeEmpty("nothing carries the instance label");
    }

    [Fact]
    public void Table_same_hash_reapply_is_idempotently_Converged()
    {
        var observed = new[] { Ref("id", Canonical(), NewSpecHash, running: true) };

        DockerRecoveryDecisionTable.Decide(observed, NewSpecHash).Action.Should().Be(DockerRecoveryAction.Converged);
    }

    [Fact]
    public void BuildRenamedName_is_deterministic_and_marker_recognised()
    {
        var canonical = Canonical();

        var a = DockerRecoveryDecisionTable.BuildRenamedName(canonical, OldSpecHash);
        var b = DockerRecoveryDecisionTable.BuildRenamedName(canonical, OldSpecHash);

        a.Should().Be(b, "the rename target is deterministic in the canonical name + old spec-hash");
        a.Should().StartWith(canonical + DockerRecoveryDecisionTable.RenamedSuffixMarker);
        a.Should().EndWith(OldSpecHash[..8]);
        DockerRecoveryDecisionTable.IsRenamed(Ref("id", a, OldSpecHash, running: false)).Should().BeTrue();
        DockerRecoveryDecisionTable.IsRenamed(Ref("id", canonical, OldSpecHash, running: false)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Recreate_recovers_old_renamed_no_new_and_converges_to_Applied()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = NewInstance();
        var reference = ReferenceFor(instanceId);
        var rollback = new RecordingRollbackCoordinator(_containers);

        var old = await CreateBlockingContainerAsync(client, instanceId, OldSpecHash);
        old.Succeeded.Should().BeTrue(old.Message);
        var oldId = old.Container!.Id;

        (await _containers.StopContainerAsync(client, oldId, CancellationToken.None)).Succeeded.Should().BeTrue();
        var renamedName = DockerRecoveryDecisionTable.BuildRenamedName(CanonicalFor(instanceId), OldSpecHash);
        await client.Containers.RenameContainerAsync(oldId, new ContainerRenameParameters { NewName = renamedName }, CancellationToken.None);

        var current = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        current.Should().NotBeNull();
        DockerRecoveryDecisionTable.IsRenamed(current!).Should().BeTrue("the current resolves to the renamed old");

        var (newSpec, desired) = BuildDesired(instanceId, NewSpecHash, blocking: true);

        var result = await _sut.RecreateAsync(client, newSpec, current, desired, rollback, CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Reason.Should().Be(WorkloadApplyReason.Applied);
        rollback.Invocations.Should().Be(0);

        var afterwards = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        afterwards!.SpecHash.Should().Be(NewSpecHash);
        afterwards.IsRunning.Should().BeTrue();
        (await ContainerExistsAsync(client, oldId)).Should().BeFalse("the renamed old was removed at RemovedPrev");
    }

    [SkippableFact]
    public async Task Recreate_after_rollback_running_prev_converges_to_single_live_new_no_port_clash()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = NewInstance();
        var reference = ReferenceFor(instanceId);
        var rollback = new RecordingRollbackCoordinator(_containers);

        DockerContainerMutationResult? old = null;
        var hostPort = 0;
        for (var attempt = 0; attempt < 6 && old is not { Succeeded: true }; attempt++)
        {
            if (old?.Container is not null)
            {
                await client.Containers.RemoveContainerAsync(
                    old.Container.Id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }

            hostPort = Random.Shared.Next(23000, 39000);
            old = await CreateBlockingContainerAsync(client, instanceId, OldSpecHash, hostPort: hostPort);
        }

        old.Should().NotBeNull();
        old!.Succeeded.Should().BeTrue(old.Message);
        var oldId = old.Container!.Id;

        var renamedName = DockerRecoveryDecisionTable.BuildRenamedName(CanonicalFor(instanceId), OldSpecHash);
        await client.Containers.RenameContainerAsync(
            oldId, new ContainerRenameParameters { NewName = renamedName }, CancellationToken.None);

        var current = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        current.Should().NotBeNull();
        current!.Id.Should().Be(oldId);
        current.IsRunning.Should().BeTrue("the restored previous is left RUNNING under the -prev- rename");
        DockerRecoveryDecisionTable.IsRenamed(current).Should().BeTrue();

        var (newSpec, desired) = BuildDesired(instanceId, NewSpecHash, blocking: true, hostPort: hostPort);

        var result = await _sut.RecreateAsync(client, newSpec, current, desired, rollback, CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Reason.Should().Be(WorkloadApplyReason.Applied);
        result.AppliedSpecHash.Should().Be(NewSpecHash);

        var afterwards = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        afterwards.Should().NotBeNull();
        afterwards!.SpecHash.Should().Be(NewSpecHash);
        afterwards.IsRunning.Should().BeTrue();
        afterwards.Id.Should().NotBe(oldId, "the new is a fresh container, not the still-running previous");
        (await ContainerExistsAsync(client, oldId)).Should().BeFalse("the previous is stopped + removed after the swap");

        var instanceContainers = await ListInstanceContainersAsync(client, instanceId);
        var running = instanceContainers
            .Where(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))
            .ToList();
        running.Should().HaveCount(1, "the recovery must converge to a single live container");
        running[0].ID.Should().Be(afterwards.Id);
        (running[0].Ports ?? new List<PortSummary>()).Any(p => p.PublicPort == hostPort)
            .Should().BeTrue("the surviving new container publishes the workload host port");
        instanceContainers.Count(c => (c.Ports ?? new List<PortSummary>()).Any(p => p.PublicPort == hostPort))
            .Should().Be(1, "no two containers publish the same host port");
    }

    [SkippableFact]
    public async Task Recreate_resume_start_new_running_prev_converges_to_single_live_new_no_port_clash()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = NewInstance();
        var reference = ReferenceFor(instanceId);
        var rollback = new RecordingRollbackCoordinator(_containers);

        DockerContainerMutationResult? old = null;
        var hostPort = 0;
        for (var attempt = 0; attempt < 6 && old is not { Succeeded: true }; attempt++)
        {
            if (old?.Container is not null)
            {
                await client.Containers.RemoveContainerAsync(
                    old.Container.Id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }

            hostPort = Random.Shared.Next(23000, 39000);
            old = await CreateBlockingContainerAsync(client, instanceId, OldSpecHash, hostPort: hostPort);
        }

        old.Should().NotBeNull();
        old!.Succeeded.Should().BeTrue(old.Message);
        var oldId = old.Container!.Id;

        var renamedName = DockerRecoveryDecisionTable.BuildRenamedName(CanonicalFor(instanceId), OldSpecHash);
        await client.Containers.RenameContainerAsync(
            oldId, new ContainerRenameParameters { NewName = renamedName }, CancellationToken.None);

        var (newSpec, desired) = BuildDesired(instanceId, NewSpecHash, blocking: true, hostPort: hostPort);
        var createdNew = await client.Containers.CreateContainerAsync(desired, CancellationToken.None);
        var newId = createdNew.ID;

        var observedBefore = (await ListInstanceContainersAsync(client, instanceId)).Select(ToObservedRef).ToList();
        DockerRecoveryDecisionTable.Decide(observedBefore, NewSpecHash).Action
            .Should().Be(DockerRecoveryAction.ResumeStartNew, "the setup reproduces the untested ResumeStartNew window");

        var current = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        current.Should().NotBeNull();

        var result = await _sut.RecreateAsync(client, newSpec, current!, desired, rollback, CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Reason.Should().Be(WorkloadApplyReason.Applied);
        result.AppliedSpecHash.Should().Be(NewSpecHash);

        var afterwards = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        afterwards.Should().NotBeNull();
        afterwards!.SpecHash.Should().Be(NewSpecHash);
        afterwards.IsRunning.Should().BeTrue();
        afterwards.Id.Should().Be(newId, "the pre-created new container is started in place, not recreated");
        (await ContainerExistsAsync(client, oldId)).Should().BeFalse("the previous is stopped + removed after the swap");

        var instanceContainers = await ListInstanceContainersAsync(client, instanceId);
        var running = instanceContainers
            .Where(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))
            .ToList();
        running.Should().HaveCount(1, "the recovery must converge to a single live container");
        running[0].ID.Should().Be(newId);
        (running[0].Ports ?? new List<PortSummary>()).Any(p => p.PublicPort == hostPort)
            .Should().BeTrue("the surviving new container publishes the workload host port");
        instanceContainers.Count(c => (c.Ports ?? new List<PortSummary>()).Any(p => p.PublicPort == hostPort))
            .Should().Be(1, "no two containers publish the same host port");
    }

    [SkippableFact]
    public async Task Recreate_second_canonical_old_running_converges_to_single_live_target_no_wedge()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = NewInstance();
        var reference = ReferenceFor(instanceId);
        var rollback = new RecordingRollbackCoordinator(_containers);
        var renamedName = DockerRecoveryDecisionTable.BuildRenamedName(CanonicalFor(instanceId), OldSpecHash);

        DockerContainerMutationResult? middle = null;
        string? renamedOldId = null;
        var hostPort = 0;
        for (var attempt = 0; attempt < 6 && middle is not { Succeeded: true }; attempt++)
        {
            if (middle?.Container is not null)
            {
                await client.Containers.RemoveContainerAsync(
                    middle.Container.Id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }

            if (renamedOldId is not null)
            {
                await client.Containers.RemoveContainerAsync(
                    renamedOldId, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
                renamedOldId = null;
            }

            hostPort = Random.Shared.Next(23000, 39000);

            var oldA = await CreateBlockingContainerAsync(client, instanceId, OldSpecHash, hostPort: hostPort);
            if (!oldA.Succeeded)
            {
                if (oldA.Container is not null)
                {
                    await client.Containers.RemoveContainerAsync(
                        oldA.Container.Id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
                }

                continue;
            }

            (await _containers.StopContainerAsync(client, oldA.Container!.Id, CancellationToken.None)).Succeeded.Should().BeTrue();
            await client.Containers.RenameContainerAsync(
                oldA.Container.Id, new ContainerRenameParameters { NewName = renamedName }, CancellationToken.None);
            renamedOldId = oldA.Container.Id;

            middle = await CreateBlockingContainerAsync(client, instanceId, MiddleSpecHash, hostPort: hostPort);
        }

        middle.Should().NotBeNull();
        middle!.Succeeded.Should().BeTrue(middle.Message);
        var middleId = middle.Container!.Id;
        renamedOldId.Should().NotBeNull();

        var observedBefore = (await ListInstanceContainersAsync(client, instanceId)).Select(ToObservedRef).ToList();
        var decisionBefore = DockerRecoveryDecisionTable.Decide(observedBefore, NewSpecHash);
        decisionBefore.Action.Should().Be(DockerRecoveryAction.ResumeCreateNew, "the setup reproduces the second-old wedge window");
        decisionBefore.PreviousContainer!.Id.Should().Be(renamedOldId, "the renamed old is the retained rollback previous");
        decisionBefore.ObstructingContainers.Select(c => c.Id).Should().Contain(middleId,
            "the still-running canonical old must be reconciled, not ignored");

        var current = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        current.Should().NotBeNull();

        var (newSpec, desired) = BuildDesired(instanceId, NewSpecHash, blocking: true, hostPort: hostPort);

        var result = await _sut.RecreateAsync(client, newSpec, current!, desired, rollback, CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Reason.Should().Be(WorkloadApplyReason.Applied);
        result.AppliedSpecHash.Should().Be(NewSpecHash);

        var afterwards = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        afterwards.Should().NotBeNull();
        afterwards!.SpecHash.Should().Be(NewSpecHash, "the live container is the TARGET, not the stale middle version");
        afterwards.IsRunning.Should().BeTrue();
        afterwards.Id.Should().NotBe(middleId, "the target is a fresh container, not the stale B");
        (await ContainerExistsAsync(client, middleId)).Should().BeFalse("the stale canonical old (B) is stopped + removed, not left running");
        (await ContainerExistsAsync(client, renamedOldId!)).Should().BeFalse("the renamed old (A) is removed at RemovedPrev");

        var instanceContainers = await ListInstanceContainersAsync(client, instanceId);
        var running = instanceContainers
            .Where(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))
            .ToList();
        running.Should().HaveCount(1, "the recovery must converge to a single live container");
        running[0].ID.Should().Be(afterwards.Id);
        (running[0].Ports ?? new List<PortSummary>()).Any(p => p.PublicPort == hostPort)
            .Should().BeTrue("the surviving target publishes the workload host port");
        instanceContainers.Count(c => (c.Ports ?? new List<PortSummary>()).Any(p => p.PublicPort == hostPort))
            .Should().Be(1, "no stale non-target still holds the host port");
    }

    [SkippableFact]
    public async Task Recreate_second_canonical_old_reconcile_retains_shared_network_and_old_volume()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = NewInstance();
        var reference = ReferenceFor(instanceId);
        var rollback = new RecordingRollbackCoordinator(_containers);
        var renamedName = DockerRecoveryDecisionTable.BuildRenamedName(CanonicalFor(instanceId), OldSpecHash);

        var networkName = await CreateHarnessNetworkNameAsync(client);
        var volumeName = await _docker.CreateLabeledVolumeAsync();

        var oldA = await CreateBlockingContainerAsync(client, instanceId, OldSpecHash, networkName);
        oldA.Succeeded.Should().BeTrue(oldA.Message);
        (await _containers.StopContainerAsync(client, oldA.Container!.Id, CancellationToken.None)).Succeeded.Should().BeTrue();
        await client.Containers.RenameContainerAsync(
            oldA.Container.Id, new ContainerRenameParameters { NewName = renamedName }, CancellationToken.None);
        var renamedOldId = oldA.Container.Id;

        var middle = await CreateBlockingContainerAsync(
            client, instanceId, MiddleSpecHash, networkName, volumeBind: $"{volumeName}:/data");
        middle.Succeeded.Should().BeTrue(middle.Message);
        var middleId = middle.Container!.Id;

        var current = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        current.Should().NotBeNull();

        var (newSpec, desired) = BuildDesired(instanceId, NewSpecHash, blocking: true, networkName: networkName);

        var result = await _sut.RecreateAsync(client, newSpec, current!, desired, rollback, CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Reason.Should().Be(WorkloadApplyReason.Applied);

        (await ContainerExistsAsync(client, middleId)).Should().BeFalse("the stale canonical old is removed");
        (await ContainerExistsAsync(client, renamedOldId)).Should().BeFalse("the renamed old is removed at RemovedPrev");
        var afterwards = await _containers.FindContainerAsync(client, reference, CancellationToken.None);
        afterwards!.SpecHash.Should().Be(NewSpecHash);
        afterwards.IsRunning.Should().BeTrue();

        var networks = await _docker.ListLabeledNetworksAsync();
        networks.Any(n => string.Equals(n.Name, networkName, StringComparison.Ordinal))
            .Should().BeTrue("the shared network is never removed by the reconcile");

        var volumes = await _docker.ListLabeledVolumesAsync();
        volumes.Any(v => string.Equals(v.Name, volumeName, StringComparison.Ordinal))
            .Should().BeTrue("the force-removed old's named volume is retained (no RemoveVolumes)");
    }

    [SkippableFact]
    public async Task Failed_swap_retains_the_shared_network_and_the_old_volume()
    {
        _docker.EnsureAvailable();
        var client = _docker.Client!;
        await EnsureBusyboxAsync(client);

        var instanceId = NewInstance();
        var reference = ReferenceFor(instanceId);
        var rollback = new RecordingRollbackCoordinator(_containers);

        var networkName = await CreateHarnessNetworkNameAsync(client);
        var volumeName = await _docker.CreateLabeledVolumeAsync();

        var old = await CreateBlockingContainerAsync(client, instanceId, OldSpecHash, networkName, volumeBind: $"{volumeName}:/data");
        old.Succeeded.Should().BeTrue(old.Message);
        var oldId = old.Container!.Id;

        var current = await _containers.FindContainerAsync(client, reference, CancellationToken.None);

        var (newSpec, desired) = BuildDesired(instanceId, NewSpecHash, blocking: true, networkName: networkName);
        desired.Cmd = new[] { "/nonexistent-binary-zzz-d00d" };

        var result = await _sut.RecreateAsync(client, newSpec, current!, desired, rollback, CancellationToken.None);
        result.Succeeded.Should().BeFalse();

        var networks = await _docker.ListLabeledNetworksAsync();
        networks.Any(n => string.Equals(n.Name, networkName, StringComparison.Ordinal))
            .Should().BeTrue("the shared network survives a failed swap");

        var volumes = await _docker.ListLabeledVolumesAsync();
        volumes.Any(v => string.Equals(v.Name, volumeName, StringComparison.Ordinal))
            .Should().BeTrue("the old container's named volume is retained across a failed swap");

        (await ContainerExistsAsync(client, oldId)).Should().BeTrue();
        (await IsRunningAsync(client, oldId)).Should().BeTrue();
    }

    private sealed class RecordingRollbackCoordinator : IDockerRollbackCoordinator
    {
        private readonly IDockerContainerManager _containers;

        public RecordingRollbackCoordinator(IDockerContainerManager containers) => _containers = containers;

        public int Invocations { get; private set; }

        public DockerContainerRef? LastFailedCandidate { get; private set; }

        public DockerContainerRef? LastPrevious { get; private set; }

        public async Task<DockerRollbackResult> RollbackAsync(
            IDockerClient client,
            WorkloadDesiredSpec spec,
            DockerContainerRef failedCandidate,
            DockerContainerRef? previousContainer,
            CancellationToken cancellationToken)
        {
            Invocations++;
            LastFailedCandidate = failedCandidate;
            LastPrevious = previousContainer;

            await _containers.RemoveContainerAsync(client, failedCandidate.Id, cancellationToken);

            var restored = false;
            if (previousContainer is not null)
            {
                var start = await _containers.StartContainerAsync(client, previousContainer.Id, cancellationToken);
                restored = start.Succeeded;
            }

            return new DockerRollbackResult { PreviousRestored = restored, Message = "test rollback" };
        }
    }

    private Guid NewInstance()
    {
        var id = Guid.NewGuid();
        _instanceIds.Add(id);
        return id;
    }

    private static string Inst8(Guid instanceId) => instanceId.ToString("N")[..8];

    private static string CanonicalFor(Guid instanceId) =>
        DockerResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
        {
            GroupId = GroupId,
            WorkloadId = WorkloadId,
            Inst8 = Inst8(instanceId),
        });

    private static string Canonical() => "reeve-recreate-test-rc-abcd1234";

    private static WorkloadRef ReferenceFor(Guid instanceId) => new()
    {
        InstanceId = instanceId,
        GroupId = GroupId,
        WorkloadId = WorkloadId,
        Inst8 = Inst8(instanceId),
        Provider = DockerProvider.Id,
    };

    private static DockerContainerRef Ref(string id, string name, string? specHash, bool running) => new()
    {
        Id = id,
        Name = name,
        SpecHash = specHash,
        IsRunning = running,
    };

    private static DockerContainerRef ToObservedRef(ContainerListResponse container) => new()
    {
        Id = container.ID,
        Name = (container.Names?.FirstOrDefault() ?? string.Empty).TrimStart('/'),
        SpecHash = container.Labels is not null
            && container.Labels.TryGetValue(DockerProviderConstants.SpecHashLabelKey, out var hash)
                ? hash
                : null,
        IsRunning = string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase),
    };

    private static (WorkloadDesiredSpec Spec, string ContainerName) BuildSpec(
        Guid instanceId, string specHash, ResolvedNetwork? network = null)
    {
        var inst8 = Inst8(instanceId);
        var containerName = DockerResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        });

        var spec = new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.NewGuid(),
            GroupId = GroupId,
            WorkloadId = WorkloadId,
            InstanceId = instanceId,
            Inst8 = inst8,
            Provider = DockerProvider.Id,
            ResourceName = containerName,
            SecretName = containerName,
            Labels = new Dictionary<string, string>(),
            Image = BusyboxImage,
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Network = network,
            SpecHash = specHash,
        };

        return (spec, containerName);
    }

    private static DockerContainerRenderContext BuildContext(
        WorkloadDesiredSpec spec, string containerName, string token, string networkName)
        => new()
        {
            Spec = spec,
            ContainerName = containerName,
            Credentials = new Dictionary<string, string> { ["workload-token"] = token },
            SharedNetworkName = networkName,
            Privilege = new DockerPrivilegeRenderer().RenderPrivileges(spec),
            Resource = new DockerResourceRenderer().RenderResources(spec),
            NetworkHostConfig = new DockerNetworkManager().RenderNetworking(spec, networkName).HostConfig,
        };

    private (WorkloadDesiredSpec Spec, CreateContainerParameters Desired) BuildDesired(
        Guid instanceId, string specHash, bool blocking, string networkName = "bridge", int? hostPort = null)
    {
        var (spec, containerName) = BuildSpec(instanceId, specHash);
        var desired = _containers.BuildContainerParameters(
            BuildContext(spec, containerName, "rwt_recreate_" + specHash, networkName));
        if (blocking)
        {
            desired.Cmd = new[] { "sleep", "3600" };
        }

        if (hostPort is int port)
        {
            PublishHostPort(desired, port);
        }

        return (spec, desired);
    }

    private async Task<DockerContainerMutationResult> CreateBlockingContainerAsync(
        IDockerClient client, Guid instanceId, string specHash, string networkName = "bridge",
        string? volumeBind = null, int? hostPort = null)
    {
        var (spec, containerName) = BuildSpec(instanceId, specHash);
        var parameters = _containers.BuildContainerParameters(
            BuildContext(spec, containerName, "rwt_recreate_old_" + specHash, networkName));
        parameters.Cmd = new[] { "sleep", "3600" };

        if (volumeBind is not null)
        {
            parameters.HostConfig ??= new HostConfig();
            parameters.HostConfig.Binds = new List<string> { volumeBind };
        }

        if (hostPort is int port)
        {
            PublishHostPort(parameters, port);
        }

        return await _containers.CreateAndStartAsync(client, parameters, CancellationToken.None);
    }

    private static void PublishHostPort(CreateContainerParameters parameters, int hostPort, int containerPort = 8080)
    {
        var portKey = $"{containerPort}/tcp";
        parameters.ExposedPorts ??= new Dictionary<string, EmptyStruct>(StringComparer.Ordinal);
        parameters.ExposedPorts[portKey] = default;

        parameters.HostConfig ??= new HostConfig();
        parameters.HostConfig.PortBindings ??= new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal);
        parameters.HostConfig.PortBindings[portKey] = new List<PortBinding>
        {
            new() { HostPort = hostPort.ToString(CultureInfo.InvariantCulture) },
        };
    }

    private static async Task<IReadOnlyList<ContainerListResponse>> ListInstanceContainersAsync(
        IDockerClient client, Guid instanceId)
    {
        var listed = await client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"{DockerProviderConstants.InstanceLabelKey}={instanceId:D}"] = true,
                    },
                },
            },
            CancellationToken.None);
        return listed.ToList();
    }

    private async Task<string> CreateHarnessNetworkNameAsync(IDockerClient client)
    {
        var id = await _docker.CreateLabeledNetworkAsync();
        var networks = await _docker.ListLabeledNetworksAsync();
        var match = networks.FirstOrDefault(n => string.Equals(n.ID, id, StringComparison.Ordinal));
        match.Should().NotBeNull("the harness network was just created");
        return match!.Name;
    }

    private static async Task<bool> ContainerExistsAsync(IDockerClient client, string id)
    {
        try
        {
            await client.Containers.InspectContainerAsync(id, CancellationToken.None);
            return true;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<bool> IsRunningAsync(IDockerClient client, string id)
    {
        var inspected = await client.Containers.InspectContainerAsync(id, CancellationToken.None);
        return inspected.State?.Running == true;
    }

    private static async Task EnsureBusyboxAsync(IDockerClient client)
    {
        try
        {
            await client.Images.InspectImageAsync(BusyboxImage, CancellationToken.None);
            return;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = BusyboxRepository, Tag = BusyboxTag },
            authConfig: null,
            progress: new Progress<JSONMessage>(),
            CancellationToken.None);
    }
}
