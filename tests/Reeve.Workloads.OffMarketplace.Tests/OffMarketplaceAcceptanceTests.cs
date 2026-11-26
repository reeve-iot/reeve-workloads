using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.DependencyInjection;
using Reeve.Workloads.Testing;
using Fixture = Reeve.Workloads.DeploymentFixtures.DeploymentManifestBuilder;

namespace Reeve.Workloads.OffMarketplace.Tests;

public sealed class OffMarketplaceAcceptanceTests
{
    private const string GroupId = "acme.stack";

    private const string OtherGroupId = "acme.other";

    [Fact]
    public async Task AHostWithNoMarketplace_OwesTheEngineNoPortAboutOne()
    {
        await using var host = new OffMarketplaceHost();

        host.Composition.FindMissingHostPorts().Select(m => m.Port.FullName).Should().BeEmpty();
    }

    [Fact]
    public async Task TheComposedPipelineConforms_AndItsPointOfNoReturnIsTheHostsReload()
    {
        await using var host = new OffMarketplaceHost();
        await using var scope = host.CreateScope();

        var pipeline = scope.ServiceProvider.GetRequiredService<Reeve.Pipeline.Pipeline<WorkloadUpdateStageContext>>();

        Reeve.Workloads.DeploymentFixtures.UpdatePipelineConformance.CoreStagesOutOfOrder(pipeline).Should().BeEmpty();
        Reeve.Workloads.DeploymentFixtures.UpdatePipelineConformance.IrreversibleStages(pipeline)
            .Should().Equal(host.Swaps.Id, WorkloadUpdateStages.CommitParams);
    }

    [Fact]
    public async Task Fact1_AGroupInstalls_AndItsWorkloadsApply()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("db"), Fixture.Workload("api")));
        var groupInstanceId = Guid.NewGuid();

        (await host.InstallAsync(groupInstanceId, GroupId, "1.0.0")).Outcome
            .Should().Be(WorkloadMaterializeOutcome.Materialized);

        await host.RunLeaderPassAsync();

        var instances = await host.InstancesOfAsync(groupInstanceId);
        instances.Select(i => i.WorkloadId).Should().BeEquivalentTo(["db", "api"]);
        instances.Should().AllSatisfy(i => host.Provider.ApplyCountFor(i.Id).Should().BePositive(
            "nothing but the host's own call stood between the install and the apply"));
        (await host.InstalledVersionOfAsync(GroupId)).Should().Be("1.0.0");
    }

    [Fact]
    public async Task Fact2_AnUpdateMovesTheWorkloadsAndTheRow_AndTheSwapSeesTheRowAtTheTarget()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("db", Fixture.Digest('a'))));
        host.Manifests.Publish(GroupId, "1.1.0", Fixture.Manifest(Fixture.Workload("db", Fixture.Digest('b'))));
        var groupInstanceId = Guid.NewGuid();
        (await host.InstallAsync(groupInstanceId, GroupId, "1.0.0")).Outcome
            .Should().Be(WorkloadMaterializeOutcome.Materialized);

        await host.RunLeaderPassAsync();

        var started = await host.UpdateAsync(groupInstanceId, "1.1.0");

        started.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started, started.RefusalMessage);
        var outcome = await host.DriveUpdateAsync(groupInstanceId, maxPasses: 20);

        outcome.Should().Be(WorkloadUpdateOutcome.Completed);
        (await host.InstalledVersionOfAsync(GroupId)).Should().Be("1.1.0");
        host.Swaps.Initiated.Should().NotBeEmpty().And.AllSatisfy(call =>
        {
            call.SourceKey.Should().Be(OffMarketplaceHost.SourceKey, "the host's key comes back to it unchanged");
            call.RowVersion.Should().Be("1.1.0", "the host is told to swap to a version the library already reads as installed");
        });
        var db = (await host.InstancesOfAsync(groupInstanceId)).Should().ContainSingle().Subject;
        host.Provider.LastAppliedSpec(db.Id)!.Image.Should().Contain(Fixture.Digest('b'));
    }

    [Fact]
    public async Task Fact3_AFailedHealthGateRollsBack_AndLeavesTheRowAtTheFromVersion()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("db", Fixture.Digest('a'))));
        host.Manifests.Publish(GroupId, "1.1.0", Fixture.Manifest(Fixture.Workload("db", Fixture.Digest('b'))));
        var groupInstanceId = Guid.NewGuid();
        await host.InstallAsync(groupInstanceId, GroupId, "1.0.0");
        await host.RunLeaderPassAsync();

        var started = await host.UpdateAsync(groupInstanceId, "1.1.0");
        started.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started, started.RefusalMessage);

        var outcome = await host.DriveUpdateAsync(groupInstanceId, maxPasses: 20, runtime: id =>
        {
            if (host.Provider.LastAppliedSpec(id)!.Image.Contains(Fixture.Digest('b'), StringComparison.Ordinal))
            {
                host.Provider.SetStatus(id, WorkloadProviderPhase.Failed, providerReady: false, restartCount: 5, message: "CrashLoopBackOff");
            }
            else
            {
                host.Provider.SimulateReady(id);
            }
        });

        outcome.Should().Be(WorkloadUpdateOutcome.RolledBack);
        (await host.InstalledVersionOfAsync(GroupId)).Should().Be("1.0.0", "the row moves only once the gate passed");
        host.Swaps.Initiated.Should().BeEmpty("a host is never told to swap to a version whose workloads failed");
        var db = (await host.InstancesOfAsync(groupInstanceId)).Should().ContainSingle().Subject;
        host.Provider.LastAppliedSpec(db.Id)!.Image.Should().Contain(Fixture.Digest('a'));
    }

    [Fact]
    public async Task A_cohort_of_two_instances_commits_only_after_both_gates_pass()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("db", Fixture.Digest('a'))));
        host.Manifests.Publish(GroupId, "1.1.0", Fixture.Manifest(Fixture.Workload("db", Fixture.Digest('b'))));
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids) await host.InstallAsync(id, GroupId, "1.0.0");
        await host.RunLeaderPassAsync();

        var started = await host.UpdateCohortAsync("1.1.0", ids);
        started.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started, started.RefusalMessage);
        var outcomes = await host.DriveCohortAsync(ids, maxPasses: 30);

        outcomes.Should().OnlyContain(outcome => outcome == WorkloadUpdateOutcome.Completed);
        (await host.InstalledVersionOfAsync(GroupId)).Should().Be("1.1.0");
    }

    [Fact]
    public async Task A_failed_second_cohort_gate_leaves_both_instances_and_group_at_from()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("db", Fixture.Digest('a'))));
        host.Manifests.Publish(GroupId, "1.1.0", Fixture.Manifest(Fixture.Workload("db", Fixture.Digest('b'))));
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids) await host.InstallAsync(id, GroupId, "1.0.0");
        await host.RunLeaderPassAsync();

        var started = await host.UpdateCohortAsync("1.1.0", ids);
        started.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.Started, started.RefusalMessage);
        var failedWorkloadId = (await host.InstancesOfAsync(ids[1])).Single().Id;
        var candidateSeen = false;
        var outcomes = await host.DriveCohortAsync(ids, maxPasses: 30, workloadInstanceId =>
        {
            var image = host.Provider.LastAppliedSpec(workloadInstanceId)?.Image;
            if (workloadInstanceId != failedWorkloadId)
            {
                host.Provider.SimulateReady(workloadInstanceId);
            }
            else if (image?.Contains(Fixture.Digest('b'), StringComparison.Ordinal) == true)
            {
                candidateSeen = true;
                host.Provider.SetStatus(workloadInstanceId, WorkloadProviderPhase.Failed,
                    providerReady: false, restartCount: 5, message: "CrashLoopBackOff");
            }
            else if (candidateSeen)
            {
                host.Provider.SimulateReady(workloadInstanceId);
            }
        });

        outcomes.Should().OnlyContain(outcome => outcome == WorkloadUpdateOutcome.RolledBack,
            "the failed gate rolls its member back and the veto rolls the other back; none completes");
        (await host.InstalledVersionOfAsync(GroupId)).Should().Be("1.0.0");
        foreach (var id in ids)
        {
            var instance = (await host.InstancesOfAsync(id)).Should().ContainSingle().Subject;
            host.Provider.LastAppliedSpec(instance.Id)!.Image.Should().Contain(Fixture.Digest('a'));
        }
    }

    [Fact]
    public async Task Fact5_AnUpdateWhoseRequirementsMovedSinceTheyWereJudgedWritesNothing()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("db", Fixture.Digest('a'))));
        host.Manifests.Publish(GroupId, "1.1.0", Fixture.Manifest(Fixture.Workload("db", Fixture.Digest('b'))));
        var groupInstanceId = Guid.NewGuid();
        await host.InstallAsync(groupInstanceId, GroupId, "1.0.0");
        await host.RunLeaderPassAsync();
        var judged = await host.JudgeUpdateAsync(groupInstanceId, "1.1.0");

        host.Manifests.Publish(GroupId, "1.1.0", Fixture.Manifest(
            Fixture.Workload("db", Fixture.Digest('b'), privileges: new WorkloadPrivileges { Privileged = true })));

        var result = await host.UpdateAsync(groupInstanceId, "1.1.0", judged);

        result.Outcome.Should().Be(WorkloadUpdateInitiationOutcome.PreconditionFailed);
        await using (var scope = host.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IWorkloadUpdateIntentRepository>()
                .GetLatestForInstanceAsync(groupInstanceId, CancellationToken.None)).Should().BeNull();
        }

        await host.RunLeaderPassAsync();
        (await host.InstalledVersionOfAsync(GroupId)).Should().Be("1.0.0");
        host.Swaps.Initiated.Should().BeEmpty();
        var db = (await host.InstancesOfAsync(groupInstanceId)).Should().ContainSingle().Subject;
        host.Provider.LastAppliedSpec(db.Id)!.Image.Should().Contain(Fixture.Digest('a'));
    }

    [Fact]
    public async Task Fact5_AParameterEditWhoseRequirementsMovedSinceTheyWereJudgedWritesNothing()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("modem",
            privileges: new WorkloadPrivileges { Devices = [new WorkloadDevice { Parameter = "serialPort" }] },
            parameters: [Fixture.Param("serialPort", required: true, "/dev/ttyACM0", WorkloadParameterType.DevicePath)])));
        var groupInstanceId = Guid.NewGuid();
        await host.InstallAsync(groupInstanceId, GroupId, "1.0.0");
        await host.RunLeaderPassAsync();
        var installed = (await host.InstancesOfAsync(groupInstanceId)).Should().ContainSingle().Subject;
        var modem = installed.Id;
        var applies = host.Provider.ApplyCountFor(modem);

        WorkloadParameterEditResult result;
        await using (var scope = host.CreateScope())
        {
            var judged = await scope.ServiceProvider.GetRequiredService<IWorkloadRequirementsService>()
                .ForParameterEditAsync(modem, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyACM1" });

            result = await scope.ServiceProvider.GetRequiredService<IWorkloadParameterEditService>().UpdateParametersAsync(
                modem,
                new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" },
                actorUserId: null,
                new WorkloadWritePrecondition { RequirementsFingerprint = judged.Read!.Fingerprint });
        }

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.PreconditionFailed, result.Error);
        var stored = (await host.InstancesOfAsync(groupInstanceId)).Should().ContainSingle().Subject;
        stored.GetResolvedParameters().Should().Equal(installed.GetResolvedParameters());
        stored.State.Should().Be(installed.State);
        await host.RunLeaderPassAsync();
        host.Provider.ApplyCountFor(modem).Should().Be(applies, "nothing was written, so nothing moved to apply");
    }

    [Fact]
    public async Task A_password_only_edit_reaches_the_runtime_with_the_new_secret()
    {
        await using var host = new OffMarketplaceHost();
        var workload = Fixture.Workload("db", parameters:
        [
            Fixture.Param("dbPassword", required: false, "old", WorkloadParameterType.Password),
        ]) with
        {
            Env = [new Reeve.Workloads.Abstractions.Contracts.Deployment.WorkloadEnv { Name = "DB_PASSWORD", From = "parameter:dbPassword" }],
        };
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(workload));
        var groupInstanceId = Guid.NewGuid();
        await host.InstallAsync(groupInstanceId, GroupId, "1.0.0");
        await host.RunLeaderPassAsync();

        var db = (await host.InstancesOfAsync(groupInstanceId)).Should().ContainSingle().Subject;
        var appliedBefore = host.Provider.LastAppliedSpec(db.Id)!;
        WorkloadParameterEditResult result;
        await using (var scope = host.CreateScope())
        {
            var values = new Dictionary<string, string?> { ["dbPassword"] = "new" };
            var judged = await scope.ServiceProvider.GetRequiredService<IWorkloadRequirementsService>()
                .ForParameterEditAsync(db.Id, values);
            result = await scope.ServiceProvider.GetRequiredService<IWorkloadParameterEditService>().UpdateParametersAsync(
                db.Id, values, actorUserId: null,
                new WorkloadWritePrecondition { RequirementsFingerprint = judged.Read!.Fingerprint });
        }

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success, result.Error);
        await host.RunLeaderPassAsync();

        var applied = host.Provider.LastAppliedSpec(db.Id)!;
        applied.SecretMaterial.Should().ContainKey("DB_PASSWORD").WhoseValue.Should().Be("new");
        applied.RestartNonce.Should().Be(appliedBefore.RestartNonce + 1);
    }

    [Fact]
    public async Task Fact1_AnInstallAtAnotherVersionThanTheRowRecordsWritesNothing()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("db")));
        host.Manifests.Publish(GroupId, "1.1.0", Fixture.Manifest(Fixture.Workload("db"), Fixture.Workload("api")));
        await host.InstallAsync(Guid.NewGuid(), GroupId, "1.0.0");
        var second = Guid.NewGuid();

        (await host.InstallAsync(second, GroupId, "1.1.0")).Outcome.Should().Be(WorkloadMaterializeOutcome.VersionMismatch,
            "moving a module's version is an update, not a second install");

        (await host.InstancesOfAsync(second)).Should().BeEmpty();
        (await host.InstalledVersionOfAsync(GroupId)).Should().Be("1.0.0");
    }

    [Fact]
    public async Task Fact4_SuspendStopsAndParksEveryWorkload_AndResumeAppliesThemAgain()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("db"), Fixture.Workload("api")));
        var groupInstanceId = Guid.NewGuid();
        await host.InstallAsync(groupInstanceId, GroupId, "1.0.0");
        await host.RunLeaderPassAsync();
        var ids = (await host.InstancesOfAsync(groupInstanceId)).Select(i => i.Id).ToList();
        ids.Should().HaveCount(2).And.AllSatisfy(id => host.Provider.ApplyCountFor(id).Should().Be(1));

        await using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkloadLifecycleService>().SuspendAsync(groupInstanceId);
        }

        await host.RunLeaderPassAsync();
        await host.RunLeaderPassAsync();

        (await host.InstancesOfAsync(groupInstanceId)).Should().AllSatisfy(i =>
        {
            i.State.Should().Be(WorkloadInstanceState.Suspended);
            i.StateReason.Should().Be(WorkloadStateReason.SuspendedByHost, "the reconciler leaves the host's hold as it is");
            host.Provider.IsSuspended(i.Id).Should().BeTrue();
            host.Provider.ApplyCountFor(i.Id).Should().Be(1, "a held workload is never applied");
        });

        await using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkloadLifecycleService>().ResumeAsync(groupInstanceId);
        }

        await host.RunLeaderPassAsync();

        ids.Should().AllSatisfy(id =>
        {
            host.Provider.IsSuspended(id).Should().BeFalse();
            host.Provider.ApplyCountFor(id).Should().Be(2, "resume re-applies, so the runtime gets a fresh credential");
        });
    }

    [Fact]
    public async Task Fact6_APinnedAttachmentThatDisappearsParksDrifted_ButAnEditedNetworkDoesNot()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0",
            Fixture.Manifest(Fixture.Workload("gateway", network: Fixture.Network(hostNetwork: true))));
        var groupInstanceId = Guid.NewGuid();
        string[] attachments = ["iot", "cam"];
        (await host.InstallAsync(groupInstanceId, GroupId, "1.0.0", suppressReconcileNudge: true)).Outcome
            .Should().Be(WorkloadMaterializeOutcome.Materialized);

        var gateway = (await host.InstancesOfAsync(groupInstanceId)).Should().ContainSingle().Subject.Id;
        await using (var scope = host.CreateScope())
        {
            var lifecycle = scope.ServiceProvider.GetRequiredService<IWorkloadLifecycleService>();
            await lifecycle.SetParametersAsync(gateway, new Dictionary<string, string?>
            {
                ["network.mode"] = "operatorNetwork",
                ["network.attachments"] = "iot,cam",
            });
            (await lifecycle.PinAttachmentAsync(gateway, Pin(attachments)))
                .Should().BeTrue();
        }

        await host.RunLeaderPassAsync();
        host.Provider.ApplyCountFor(gateway).Should().Be(1, "the network the host accepted is the one the provider observes");

        host.Provider.SetNetworkAttachmentMissing("cam");
        (await host.ReconcileAsync(gateway)).Reason.Should().Be(WorkloadReconcileReason.AttachmentConfigDrifted);
        host.Provider.ApplyCountFor(gateway).Should().Be(1);

        host.Provider.SetNetworkAttachmentMissing("cam", missing: false);
        (await host.ReconcileAsync(gateway)).Reason.Should().NotBe(WorkloadReconcileReason.AttachmentConfigDrifted,
            "the attachment exists again, and nothing else held the workload");
    }

    [Fact]
    public async Task Fact7_AHostHeldRowIsNeverAppliedUntilTheHostResumesIt()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("db")));
        var groupInstanceId = Guid.NewGuid();
        await host.InstallAsync(groupInstanceId, GroupId, "1.0.0", suppressReconcileNudge: true);
        var db = (await host.InstancesOfAsync(groupInstanceId)).Should().ContainSingle().Subject.Id;
        await host.StoreBelowTheEntityAsync(db, WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost);

        await host.RunLeaderPassAsync();
        await host.RunLeaderPassAsync();

        host.Provider.ApplyCountFor(db).Should().Be(0, "a row the host holds was never accepted by anyone");
        var held = (await host.InstancesOfAsync(groupInstanceId)).Should().ContainSingle().Subject;
        held.State.Should().Be(WorkloadInstanceState.Suspended);
        held.StateReason.Should().Be(WorkloadStateReason.SuspendedByHost);

        await using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkloadLifecycleService>().ResumeAsync(groupInstanceId);
        }

        await host.RunLeaderPassAsync();

        host.Provider.ApplyCountFor(db).Should().Be(1, "the host's resume is the acceptance the row was waiting for");
    }

    [Fact]
    public async Task Fact8_AnEngineNobodyRan_AppliesNothingAndStopsTheApplication()
    {
        await using var host = new OffMarketplaceHost();
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("db")));

        await host.StartApplicationAsync();

        var groupInstanceId = Guid.NewGuid();
        (await host.InstallAsync(groupInstanceId, GroupId, "1.0.0")).Outcome
            .Should().Be(WorkloadMaterializeOutcome.Materialized, "the write path never needed an engine, which is why this failure is quiet");

        await host.NudgeEngineAsync();

        var db = (await host.InstancesOfAsync(groupInstanceId)).Should().ContainSingle().Subject.Id;
        (await host.WaitForApplyAsync(db)).Should().Be(0,
            "nothing is running the reconciler, so every nudge is dropped and the workload stays a row in a table");
        host.ApplicationWasAskedToStop.Should().BeTrue(
            "a composition that wired the whole engine and never started it must fail its boot rather than look healthy");
    }

    [Fact]
    public async Task Fact9_AStartedEngine_AppliesWithoutBeingDriven_AndStopsWhenLeadershipIsLost()
    {
        var election = new RevocableLeaderElection();
        await using var host = new OffMarketplaceHost(election);
        host.Manifests.Publish(GroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("db")));
        host.Manifests.Publish(OtherGroupId, "1.0.0", Fixture.Manifest(Fixture.Workload("api")));

        await host.StartEngineAndApplicationAsync();

        var whileLeader = Guid.NewGuid();
        (await host.InstallAsync(whileLeader, GroupId, "1.0.0")).Outcome.Should().Be(WorkloadMaterializeOutcome.Materialized);
        await host.NudgeEngineAsync();
        var db = (await host.InstancesOfAsync(whileLeader)).Should().ContainSingle().Subject.Id;

        (await host.WaitForApplyAsync(db)).Should().BePositive(
            "the engine holds the leadership, so it answers the install itself — nothing drove it");

        election.Revoke();
        await election.AwaitResignedAsync(OffMarketplaceHost.EngineWaitBudget);

        var afterLoss = Guid.NewGuid();
        (await host.InstallAsync(afterLoss, OtherGroupId, "1.0.0")).Outcome.Should().Be(WorkloadMaterializeOutcome.Materialized);

        await host.NudgeEngineAsync();
        var api = (await host.InstancesOfAsync(afterLoss)).Should().ContainSingle().Subject.Id;

        (await host.WaitForApplyAsync(api)).Should().Be(0,
            "a replica that lost the leadership must stop writing to the fleet: whoever takes the lock next owns it");
        host.Provider.ApplyCountFor(db).Should().Be(1,
            "and must not go on re-applying what it had already applied while it was leader");
        host.ApplicationWasAskedToStop.Should().BeFalse(
            "losing an election is a normal day for a replica, not a reason to take the process down");
    }

    [Fact]
    public async Task Fact10_AHostWithNoElection_IsToldItIsRunningOnTheLibrarysOwn()
    {
        await using var onTheLibrarysOwn = new OffMarketplaceHost();

        onTheLibrarysOwn.Composition.FindHostPortsOnLibraryDefaults().Should().Contain(typeof(IWorkloadLeaderElection),
            "the single-replica election cannot see outside this process, so a host running two replicas over it "
            + "runs two engines on one fleet — and this audit is the only thing that says so before the fleet does");

        await using var withItsOwnElection = new OffMarketplaceHost(new RevocableLeaderElection());

        withItsOwnElection.Composition.FindHostPortsOnLibraryDefaults().Should().NotContain(typeof(IWorkloadLeaderElection),
            "a host that answered the port is not running without one, and must not be warned as if it were");
    }

    [Fact]
    public async Task Fact11_RunningTheEngineTwice_RunsOneEngine()
    {
        var election = new RevocableLeaderElection();
        await using var host = new OffMarketplaceHost(election);

        await host.StartEngineAndApplicationAsync();
        await host.RunEngineAsync();

        election.Acquisitions.Should().Be(1, "a second engine would contend for a leadership of its own");
        host.EngineUnit.Starts.Should().Be(1, "and would start every unit a second time underneath the first");

        host.Logs.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Text.Contains("RunReeveWorkloadsAsync was called more than once", StringComparison.Ordinal),
            "the library says so out loud, which is the only way that host learns of it");

        host.ApplicationWasAskedToStop.Should().BeFalse("something did run the engine, so the start guard has nothing to say");
    }

    private static WorkloadAttachmentPin Pin(string[] attachments) => new()
    {
        Mode = WorkloadNetworkMode.OperatorNetwork,
        AttachmentNames = attachments,
    };
}
