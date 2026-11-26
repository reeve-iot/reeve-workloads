using FluentAssertions;
using Reeve.Workloads.Manifest;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadPathPolicyTests
{
    [Theory]
    [InlineData("/dev/ttyUSB0")]
    [InlineData("/dev/serial/by-id/usb-Zooz_800")]
    [InlineData("/dev/net/tun")]
    public void IsAllowedDevicePath_accepts_paths_under_dev(string path) =>
        WorkloadPathPolicy.IsAllowedDevicePath(path).Should().BeTrue();

    [Theory]
    [InlineData("/dev")]
    [InlineData("/etc/passwd")]
    [InlineData("/dev/../etc/passwd")]
    [InlineData("/devious/x")]
    [InlineData("dev/ttyUSB0")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowedDevicePath_rejects_everything_else(string? path) =>
        WorkloadPathPolicy.IsAllowedDevicePath(path).Should().BeFalse();

    [Theory]
    [InlineData("/run/dbus")]
    [InlineData("/run/dbus/system_bus_socket")]
    [InlineData("/var/run/dbus")]
    public void IsAllowedDbusPath_accepts_run_and_var_run(string path) =>
        WorkloadPathPolicy.IsAllowedDbusPath(path).Should().BeTrue();

    [Theory]
    [InlineData("/etc/dbus")]
    [InlineData("/run")]
    [InlineData("/var/run")]
    [InlineData("/run/../etc")]
    [InlineData("/runx/dbus")]
    [InlineData("/run/docker.sock")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/run/containerd")]
    [InlineData("/run/user/1000")]
    public void IsAllowedDbusPath_rejects_widening(string path) =>
        WorkloadPathPolicy.IsAllowedDbusPath(path).Should().BeFalse();

    [Theory]
    [InlineData("/sys/bus/usb")]
    [InlineData("/sys/bus/usb/devices")]
    [InlineData("/proc/net")]
    [InlineData("/proc/sys/kernel")]
    [InlineData("/run/udev")]
    [InlineData("/run/dbus")]
    [InlineData("/run/dockerish")]
    [InlineData("/run/systemd/journal")]
    [InlineData("/var/run/foo")]
    public void IsAllowedHostInspectPath_accepts_paths_under_the_root_allowlist(string path) =>
        WorkloadPathPolicy.IsAllowedHostInspectPath(path).Should().BeTrue();

    [Theory]
    [InlineData("/")]
    [InlineData("/etc")]
    [InlineData("/etc/passwd")]
    [InlineData("/sys")]
    [InlineData("/proc")]
    [InlineData("/var/run")]
    [InlineData("/sys/../etc/passwd")]
    [InlineData("/system/x")]
    [InlineData("/home/user")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/run/docker.sock")]
    [InlineData("/run/docker/plugins")]
    [InlineData("/run/containerd/containerd.sock")]
    [InlineData("/var/run/crio/crio.sock")]
    [InlineData("/run/podman/podman.sock")]
    [InlineData("/run/k3s/containerd/containerd.sock")]
    [InlineData("/run/k0s")]
    [InlineData("/run/cri-dockerd.sock")]
    [InlineData("/run/dockershim.sock")]
    [InlineData("/run/user/1000/docker.sock")]
    [InlineData("/var/run/balena-engine.sock")]
    [InlineData("/var/run/balena.sock")]
    [InlineData("/run/balena-engine")]
    [InlineData("/run/libvirt/libvirt-sock")]
    [InlineData("/run/snapd.socket")]
    [InlineData("/run/snapd-snap.socket")]
    [InlineData("/run/systemd/private")]
    [InlineData("/run/systemd")]
    [InlineData("/proc/1/root")]
    [InlineData("/proc/1/root/etc")]
    [InlineData("/proc/1/cwd")]
    [InlineData("/proc/1/net")]
    [InlineData("/proc/42/task/42/root")]
    [InlineData("/proc/self/root")]
    [InlineData("/proc/thread-self/fd")]
    [InlineData("sys/bus")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowedHostInspectPath_rejects_host_root_etc_and_outside_allowlist(string? path) =>
        WorkloadPathPolicy.IsAllowedHostInspectPath(path).Should().BeFalse();

    [Theory]
    [InlineData("/dev/serial")]
    [InlineData("/dev/serial/")]
    [InlineData("/dev/serial/by-id")]
    [InlineData("/dev/serial/by-path/pci-0000")]
    public void IsAllowedHostInspectPath_accepts_dev_serial_and_below(string path) =>
        WorkloadPathPolicy.IsAllowedHostInspectPath(path).Should().BeTrue();

    [Theory]
    [InlineData("/dev")]
    [InlineData("/dev/mem")]
    [InlineData("/dev/kmem")]
    [InlineData("/dev/sda")]
    [InlineData("/dev/serialx")]
    [InlineData("/dev/serial2")]
    [InlineData("/dev/serial/../mem")]
    [InlineData("/dev/tty/../serial")]
    public void IsAllowedHostInspectPath_rejects_other_dev_paths(string path) =>
        WorkloadPathPolicy.IsAllowedHostInspectPath(path).Should().BeFalse();

    [Theory]
    [InlineData("/dev/dri")]
    [InlineData("/dev/dri/by-path")]
    public void Dri_tree_is_an_allowed_host_inspect_mount(string path) =>
        WorkloadPathPolicy.IsAllowedHostInspectPath(path).Should().BeTrue();

    [Theory]
    [InlineData("/dev")]
    [InlineData("/dev/mem")]
    [InlineData("/dev/drilling")]
    [InlineData("/dev/dri/../mem")]
    public void No_other_dev_path_is_an_allowed_host_inspect_mount(string path) =>
        WorkloadPathPolicy.IsAllowedHostInspectPath(path).Should().BeFalse();

    [Theory]
    [InlineData("state")]
    [InlineData("ble/bindkeys")]
    [InlineData("./state")]
    public void IsSafeRelativeSubdir_accepts_safe_relatives(string subdir) =>
        WorkloadPathPolicy.IsSafeRelativeSubdir(subdir).Should().BeTrue();

    [Theory]
    [InlineData("/state")]
    [InlineData("../escape")]
    [InlineData("a/../../etc")]
    [InlineData("a//b")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSafeRelativeSubdir_rejects_unsafe(string? subdir) =>
        WorkloadPathPolicy.IsSafeRelativeSubdir(subdir).Should().BeFalse();

    [Theory]
    [InlineData("64Mi")]
    [InlineData("512Mi")]
    [InlineData("1Gi")]
    [InlineData("100m")]
    [InlineData("0.5")]
    [InlineData("250M")]
    public void IsValidQuantity_accepts_k8s_quantities(string q) =>
        WorkloadPathPolicy.IsValidQuantity(q).Should().BeTrue();

    [Theory]
    [InlineData("64 Mi")]
    [InlineData("64MB")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValidQuantity_rejects_junk(string? q) =>
        WorkloadPathPolicy.IsValidQuantity(q).Should().BeFalse();

    [Theory]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("10.0.3")]
    public void IsNumericVersion_accepts_dotted_integers(string v) =>
        WorkloadPathPolicy.IsNumericVersion(v).Should().BeTrue();

    [Theory]
    [InlineData("1.0-beta")]
    [InlineData("1\n")]
    [InlineData("v1")]
    [InlineData("")]
    [InlineData(null)]
    public void IsNumericVersion_rejects_non_numeric(string? v) =>
        WorkloadPathPolicy.IsNumericVersion(v).Should().BeFalse();

    [Theory]
    [InlineData("1", "1", 0)]
    [InlineData("1", "2", -1)]
    [InlineData("2", "1", 1)]
    [InlineData("1.0", "1", 0)]
    [InlineData("1.2", "1.10", -1)]
    public void CompareNumericVersions_orders_numerically(string a, string b, int expectedSign) =>
        Math.Sign(WorkloadPathPolicy.CompareNumericVersions(a, b)).Should().Be(expectedSign);

    [Theory]
    [InlineData("linux/amd64")]
    [InlineData("linux/arm64")]
    [InlineData("linux/arm/v7")]
    public void IsValidPlatformToken_accepts_os_arch(string token) =>
        WorkloadPathPolicy.IsValidPlatformToken(token).Should().BeTrue();

    [Theory]
    [InlineData("amd64")]
    [InlineData("linux")]
    [InlineData("Linux/amd64")]
    [InlineData("linux/")]
    [InlineData("linux/amd64\n")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValidPlatformToken_rejects_malformed(string? token) =>
        WorkloadPathPolicy.IsValidPlatformToken(token).Should().BeFalse();

    [Fact]
    public void IsAllowedDevicePath_rejects_an_absurdly_long_path()
    {
        var path = "/dev/" + new string('a', 600);
        WorkloadPathPolicy.IsAllowedDevicePath(path).Should().BeFalse();
    }

    [Fact]
    public void IsAllowedDevicePath_rejects_too_many_wildcards()
    {
        WorkloadPathPolicy.IsAllowedDevicePath("/dev/*/*/*/*/*/*/*/*/*/x").Should().BeFalse();
    }

    [Fact]
    public void IsAllowedDbusPath_rejects_an_absurdly_long_path()
    {
        var path = "/run/" + new string('b', 600);
        WorkloadPathPolicy.IsAllowedDbusPath(path).Should().BeFalse();
    }

    [Fact]
    public void IsAllowedDbusPath_rejects_too_many_wildcards()
    {
        WorkloadPathPolicy.IsAllowedDbusPath("/run/*/*/*/*/*/*/*/*/*/x").Should().BeFalse();
    }

    [Fact]
    public void Path_caps_do_not_reject_ordinary_paths()
    {
        WorkloadPathPolicy.IsAllowedDevicePath("/dev/serial/by-id/usb-Zooz_800").Should().BeTrue();
    }
}
