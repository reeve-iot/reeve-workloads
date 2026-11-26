using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Xunit;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadUpdateIntentSnapshotDiscardTests
{
    [Fact]
    public void ReachingATerminalPhase_DiscardsBothParameterSnapshots()
    {
        var intent = Staged();

        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.Completed, Now);

        intent.OperatorParametersDocument!.Entries.Should().BeEmpty();
        intent.PreviousParametersDocument!.Workloads.Should().BeEmpty(
            "it holds the resolved value of every parameter, and its only reader restores during RollingBack");
    }

    [Fact]
    public void TheRollbackBranchAlsoDiscardsThem_ButOnlyOnceItIsTerminal()
    {
        var intent = Staged();

        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.RollingBack, Now);

        intent.PreviousParametersDocument!.Workloads.Should().NotBeEmpty();

        intent.AdvanceTo(WorkloadUpdatePhase.RolledBack, Now);

        intent.PreviousParametersDocument!.Workloads.Should().BeEmpty();
    }

    [Fact]
    public void TheSpecSnapshotsSurvive_TheDiscardIsNarrowNotABlanketWipe()
    {
        var intent = Staged();

        intent.AdvanceTo(WorkloadUpdatePhase.ApplyingSidecar, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.HealthGate, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.SwappingModule, Now);
        intent.AdvanceTo(WorkloadUpdatePhase.Completed, Now);

        intent.TargetSpecsDocument.Should().NotBeNull();
        intent.PreviousSpecsDocument.Should().NotBeNull();
    }

    private static WorkloadUpdateIntent Staged()
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "1.0.0", "1.1.0", Now);
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                TargetSpecs = new Dictionary<string, WorkloadDesiredSpec> { ["gw"] = Spec() },
                PreviousSpecs = new Dictionary<string, WorkloadDesiredSpec> { ["gw"] = Spec() },
                PreviousParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>
                {
                    ["gw"] = new Dictionary<string, string?> { ["dbPassword"] = "hunter2" },
                },
                FromDigests = new Dictionary<string, string?> { ["gw"] = "sha256:a" },
                ToDigests = new Dictionary<string, string?> { ["gw"] = "sha256:b" },
                OperatorParameters = new Dictionary<string, string?> { ["dbPassword"] = "hunter2" },
            },
            Now);
        return intent;
    }

    private static WorkloadDesiredSpec Spec() => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "reeve",
        WorkloadId = "gw",
        InstanceId = Guid.Parse("12345678-0000-0000-0000-000000000000"),
        Inst8 = "12345678",
        Provider = StandInProviders.DockerId,
        ResourceName = "reeve-gw-1234abcd",
        SecretName = "reeve-gw-1234abcd-secret",
        Labels = new Dictionary<string, string>(),
        Image = "ghcr.io/reeve/gw@sha256:" + new string('a', 64),
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "0123abcd",
    };

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
}
