using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core;
using Reeve.Workloads.DeploymentFixtures;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadParameterMaskPolicyTests
{
    [Fact]
    public void MaskingThenAcceptingTheFormBackUnchanged_ReturnsTheStoredValues()
    {
        var stored = Stored();
        var unmasked = WorkloadParameterMaskPolicy.UnmaskedNames(Workload);

        var shown = WorkloadParameterMaskPolicy.Mask(stored, unmasked);
        var retained = WorkloadParameterMaskPolicy.RetainMaskedValues(stored, shown);

        retained.Should().BeEquivalentTo(stored);
    }

    [Fact]
    public void APasswordIsMasked_AndAnOrdinaryParameterIsNot()
    {
        var shown = WorkloadParameterMaskPolicy.Mask(Stored(), WorkloadParameterMaskPolicy.UnmaskedNames(Workload));

        shown["dbPassword"].Should().Be(WorkloadParameterMaskPolicy.MaskSentinel);
        shown["region"].Should().Be("eu-west-1");
    }

    [Fact]
    public void ANullWorkload_MasksEverything()
    {
        var shown = WorkloadParameterMaskPolicy.Mask(Stored(), WorkloadParameterMaskPolicy.UnmaskedNames(null));

        shown.Values.Should().AllBe(WorkloadParameterMaskPolicy.MaskSentinel);
    }

    [Fact]
    public void ASubmittedNewPassword_OverwritesTheStoredOne()
    {
        var retained = WorkloadParameterMaskPolicy.RetainMaskedValues(
            Stored(), new Dictionary<string, string?> { ["dbPassword"] = "new-secret" });

        retained["dbPassword"].Should().Be("new-secret");
    }

    [Fact]
    public void TheSentinelWithNothingStored_IsDroppedRatherThanPersistedLiterally()
    {
        var retained = WorkloadParameterMaskPolicy.RetainMaskedValues(
            new Dictionary<string, string?>(),
            new Dictionary<string, string?> { ["dbPassword"] = WorkloadParameterMaskPolicy.MaskSentinel });

        retained.Should().NotContainKey("dbPassword");
    }

    [Fact]
    public void ABlankStoredValue_IsNotMaskedIntoLookingLikeASetSecret()
    {
        var shown = WorkloadParameterMaskPolicy.Mask(
            new Dictionary<string, string?> { ["dbPassword"] = string.Empty },
            WorkloadParameterMaskPolicy.UnmaskedNames(Workload));

        shown["dbPassword"].Should().BeEmpty();
    }

    [Fact]
    public void AReferenceIsReturnedVerbatimInsteadOfMasked()
    {
        var shown = WorkloadParameterMaskPolicy.Mask(
            new Dictionary<string, string?> { ["dbPassword"] = "ref:secrets:app#password" },
            WorkloadParameterMaskPolicy.UnmaskedNames(Workload));

        shown["dbPassword"].Should().Be("ref:secrets:app#password");
    }

    [Fact]
    public void The_user_namespace_switch_rides_unmasked()
        => WorkloadParameterMaskPolicy.UnmaskedNames(Workload).Should().Contain("security.userNamespace");

    private static readonly WorkloadManifest Workload = DeploymentManifestBuilder.Workload(parameters:
    [
        DeploymentManifestBuilder.Param("dbPassword", required: true, type: WorkloadParameterType.Password),
        DeploymentManifestBuilder.Param("region", required: false, type: WorkloadParameterType.Text),
    ]);

    private static Dictionary<string, string?> Stored() => new(StringComparer.Ordinal)
    {
        ["dbPassword"] = "hunter2",
        ["region"] = "eu-west-1",
    };
}
