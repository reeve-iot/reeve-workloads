using FluentAssertions;
using k8s.Models;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesPvcRecreatePolicyTests
{
    private static V1PersistentVolumeClaim Desired(string? storageClass) => new()
    {
        Metadata = new V1ObjectMeta { Name = "reeve-wl-data" },
        Spec = new V1PersistentVolumeClaimSpec { StorageClassName = storageClass },
    };

    private static V1PersistentVolumeClaim Live(string? storageClass, string? phase, string? volumeName = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = "reeve-wl-data" },
        Spec = new V1PersistentVolumeClaimSpec { StorageClassName = storageClass, VolumeName = volumeName },
        Status = phase is null ? null : new V1PersistentVolumeClaimStatus { Phase = phase },
    };

    [Theory]
    [InlineData("old-class")]
    [InlineData(null)]
    [InlineData("")]
    public void An_unbound_claim_whose_class_differs_is_recreated(string? liveClass)
    {
        KubernetesPvcRecreatePolicy.Decide(Desired("local-path"), Live(liveClass, "Pending"))
            .Should().Be(KubernetesPvcAction.Recreate);
    }

    [Fact]
    public void An_unbound_claim_whose_class_matches_is_kept()
    {
        KubernetesPvcRecreatePolicy.Decide(Desired("local-path"), Live("local-path", "Pending"))
            .Should().Be(KubernetesPvcAction.Keep);
    }

    [Theory]
    [InlineData("Bound", "pv-1")]
    [InlineData("Bound", null)]
    [InlineData("Lost", "pv-1")]
    [InlineData("Pending", "pv-1")]
    public void A_claim_that_is_bound_or_names_a_volume_is_never_recreated(string phase, string? volumeName)
    {
        KubernetesPvcRecreatePolicy.Decide(Desired("local-path"), Live("old-class", phase, volumeName))
            .Should().Be(KubernetesPvcAction.Keep);
    }

    [Fact]
    public void A_claim_without_a_reported_phase_is_kept()
    {
        KubernetesPvcRecreatePolicy.Decide(Desired("local-path"), Live("old-class", phase: null))
            .Should().Be(KubernetesPvcAction.Keep);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_desired_class_means_the_cluster_default_and_keeps_the_claim(string? desiredClass)
    {
        KubernetesPvcRecreatePolicy.Decide(Desired(desiredClass), Live("old-class", "Pending"))
            .Should().Be(KubernetesPvcAction.Keep);
    }
}
