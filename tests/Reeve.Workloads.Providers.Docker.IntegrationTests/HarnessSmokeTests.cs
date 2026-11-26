using System.Text.RegularExpressions;
using FluentAssertions;

namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[Collection(DockerIntegrationCollection.Name)]
[Trait("lane", "smoke")]
public sealed class HarnessSmokeTests
{
    private readonly DockerDaemonFixture _docker;

    public HarnessSmokeTests(DockerDaemonFixture docker) => _docker = docker;

    [SkippableFact]
    [Trait("lane", "smoke")]
    public void Daemon_reports_a_plausible_version()
    {
        _docker.EnsureAvailable();

        _docker.Client.Should().NotBeNull("EnsureAvailable passed, so a live client must exist");
        _docker.ServerVersion.Should().NotBeNullOrWhiteSpace();
        Regex.IsMatch(_docker.ServerVersion, @"^\d+\.\d+")
            .Should().BeTrue($"'{_docker.ServerVersion}' should look like a docker version (MAJOR.MINOR...)");
        _docker.ApiVersion.Should().MatchRegex(@"^\d+\.\d+", "the daemon should report a numeric API version");
    }

    [SkippableFact]
    [Trait("lane", "smoke")]
    public async Task Labeled_network_and_volume_roundtrip_and_clean_up()
    {
        _docker.EnsureAvailable();

        var networkId = await _docker.CreateLabeledNetworkAsync("smoke");
        var volumeName = await _docker.CreateLabeledVolumeAsync("smoke");

        networkId.Should().NotBeNullOrWhiteSpace();
        volumeName.Should().NotBeNullOrWhiteSpace();

        (await _docker.ListLabeledNetworksAsync())
            .Select(n => n.ID).Should().Contain(networkId);
        (await _docker.ListLabeledVolumesAsync())
            .Select(v => v.Name).Should().Contain(volumeName);

        await _docker.RemoveNetworkAsync(networkId);
        await _docker.RemoveVolumeAsync(volumeName);

        (await _docker.ListLabeledNetworksAsync())
            .Select(n => n.ID).Should().NotContain(networkId);
        (await _docker.ListLabeledVolumesAsync())
            .Select(v => v.Name).Should().NotContain(volumeName);

        await _docker.RemoveNetworkAsync(networkId);
        await _docker.RemoveVolumeAsync(volumeName);
    }
}
