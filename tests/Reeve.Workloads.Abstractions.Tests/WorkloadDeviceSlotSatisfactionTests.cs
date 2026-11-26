using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadDeviceSlotSatisfactionTests
{
    [Fact]
    public void A_multi_device_slot_is_satisfied_by_any_indexed_key()
    {
        var workload = Manifest(maxCount: 4);
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["acc"] = null,
            ["acc[0]"] = null,
            ["acc[2]"] = "/dev/dri/renderD128",
        };

        WorkloadDeviceSlotSatisfaction.IsSatisfied(workload, "acc", resolved).Should().BeTrue();
    }

    [Fact]
    public void A_multi_device_slot_with_every_index_blank_is_unsatisfied_however_the_declared_key_reads()
    {
        var workload = Manifest(maxCount: 4);
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["acc"] = "/dev/dri/renderD128",
            ["acc[0]"] = "   ",
        };

        WorkloadDeviceSlotSatisfaction.IsSatisfied(workload, "acc", resolved).Should().BeFalse();
    }

    [Fact]
    public void An_index_at_or_beyond_maxCount_does_not_satisfy_the_slot()
    {
        var workload = Manifest(maxCount: 2);
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["acc[5]"] = "/dev/dri/renderD128",
        };

        WorkloadDeviceSlotSatisfaction.IsSatisfied(workload, "acc", resolved).Should().BeFalse();
    }

    [Fact]
    public void A_single_device_slot_is_satisfied_by_the_declared_key_alone()
    {
        var workload = Manifest(maxCount: 1);

        WorkloadDeviceSlotSatisfaction.IsSatisfied(
            workload, "acc",
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["acc"] = "/dev/ttyUSB0" })
            .Should().BeTrue();

        WorkloadDeviceSlotSatisfaction.IsSatisfied(
            workload, "acc",
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["acc[0]"] = "/dev/ttyUSB0" })
            .Should().BeFalse();
    }

    [Fact]
    public void A_parameter_backing_no_slot_falls_back_to_its_own_key()
    {
        var workload = Manifest(maxCount: 4);

        WorkloadDeviceSlotSatisfaction.IsSatisfied(
            workload, "other",
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["other"] = "x" }).Should().BeTrue();
        WorkloadDeviceSlotSatisfaction.IsSatisfied(
            workload, "other", new Dictionary<string, string?>(StringComparer.Ordinal)).Should().BeFalse();
    }

    private static WorkloadManifest Manifest(int maxCount) => new()
    {
        Id = "wl",
        DisplayName = "Slot gate",
        Image = new WorkloadImage { Ref = "registry.example/x", Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.Service,
        Privileges = new WorkloadPrivileges
        {
            Devices = [new WorkloadDevice { Parameter = "acc", MaxCount = maxCount }],
        },
        Parameters =
        [
            new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath, Required = true },
            new WorkloadParameter { Name = "other", Type = WorkloadParameterType.Text, Required = true },
        ],
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
    };
}
