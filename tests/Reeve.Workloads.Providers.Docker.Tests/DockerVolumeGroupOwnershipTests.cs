using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerVolumeGroupOwnershipTests
{
    private const string WorkloadImage = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static ResolvedVolume Volume(string id, string mountPath) => new()
    {
        Id = id,
        MountPath = mountPath,
        Size = "64Mi",
        Criticality = VolumeCriticality.Cache,
        ResourceName = $"reeve-volume-{id}",
    };

    private static WorkloadDesiredSpec Spec(int? fsGroup, params ResolvedVolume[] volumes) => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "com.reeve.matter",
        WorkloadId = "matter",
        InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Inst8 = "11111111",
        Provider = DockerProvider.Id,
        ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
        SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
        Labels = new Dictionary<string, string>(),
        Image = WorkloadImage,
        Kind = WorkloadKind.SingleNode,
        Privileges = fsGroup is null ? null : new ResolvedPrivileges { FsGroup = fsGroup },
        Volumes = volumes,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = "abc123def456",
    };

    private sealed class FakeDaemon
    {
        public IDockerClient Client { get; } = Substitute.For<IDockerClient>();

        public List<CreateContainerParameters> Created { get; } = [];

        public FakeDaemon(long exitCode = 0)
        {
            Client.Containers.CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Created.Add(call.Arg<CreateContainerParameters>());
                    return Task.FromResult(new CreateContainerResponse { ID = "helper-1" });
                });
            Client.Containers.WaitContainerAsync("helper-1", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ContainerWaitResponse { StatusCode = exitCode }));
        }

        public Task RemovedHelper() => Client.Containers.Received(1)
            .RemoveContainerAsync("helper-1", Arg.Is<ContainerRemoveParameters>(p => p.Force == true && p.RemoveVolumes != true), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_spec_with_no_volume_group_touches_nothing_on_the_daemon()
    {
        var daemon = new FakeDaemon();

        var result = await new DockerVolumeGroupOwnership().EnsureAsync(daemon.Client, Spec(null, Volume("data", "/data")), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        daemon.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task The_group_is_applied_by_a_confined_helper_from_the_workload_image_over_exactly_its_writable_volumes()
    {
        var daemon = new FakeDaemon();
        var spec = Spec(1000, Volume("data", "/data"), Volume("ota", "/ota-images"));

        var result = await new DockerVolumeGroupOwnership().EnsureAsync(daemon.Client, spec, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        var helper = daemon.Created.Should().ContainSingle().Which;

        helper.Image.Should().Be(WorkloadImage);
        helper.User.Should().Be("0");

        helper.Entrypoint.TakeLast(3).Should().Equal("1000", "/data", "/ota-images");
        helper.HostConfig.Mounts.Select(m => (m.Type, m.Source, m.Target)).Should().BeEquivalentTo(new[]
        {
            ("volume", "reeve-volume-data", "/data"),
            ("volume", "reeve-volume-ota", "/ota-images"),
        });

        helper.HostConfig.NetworkMode.Should().Be("none");
        helper.HostConfig.Privileged.Should().BeFalse();
        helper.HostConfig.ReadonlyRootfs.Should().BeTrue();
        helper.HostConfig.CapDrop.Should().Equal("ALL");
        helper.HostConfig.CapAdd.Should().BeEquivalentTo("CHOWN", "FOWNER", "DAC_OVERRIDE");
        helper.HostConfig.SecurityOpt.Should().Contain("no-new-privileges");

        helper.Labels.Should().NotContainKey(DockerProviderConstants.InstanceLabelKey);

        await daemon.Client.Containers.Received(1).StartContainerAsync("helper-1", Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>());
        await daemon.RemovedHelper();
    }

    [Fact]
    public async Task A_helper_that_fails_fails_the_apply_with_the_reason_and_is_still_removed()
    {
        var daemon = new FakeDaemon(exitCode: 127);

        var result = await new DockerVolumeGroupOwnership().EnsureAsync(daemon.Client, Spec(1000, Volume("data", "/data")), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(DockerFailureKind.Rejected);
        result.Message.Should().Contain("1000").And.Contain("127").And.Contain("sh, find, chgrp and chmod");
        await daemon.RemovedHelper();
    }

    [Fact]
    public async Task A_walk_that_fails_on_a_changing_volume_is_a_retryable_error_that_does_not_blame_the_image()
    {
        var daemon = new FakeDaemon(exitCode: 1);

        var result = await new DockerVolumeGroupOwnership().EnsureAsync(daemon.Client, Spec(1000, Volume("data", "/data")), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(DockerFailureKind.Error);
        result.Message.Should().Contain("next apply").And.NotContain("image");
        await daemon.RemovedHelper();
    }

    [Fact]
    public async Task A_daemon_that_cannot_create_the_helper_fails_the_apply_instead_of_starting_an_unwritable_workload()
    {
        var client = Substitute.For<IDockerClient>();
        client.Containers.CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>())
            .Returns<Task<CreateContainerResponse>>(_ => throw new DockerApiException(HttpStatusCode.NotFound, "No such image"));

        var result = await new DockerVolumeGroupOwnership().EnsureAsync(client, Spec(1000, Volume("data", "/data")), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(DockerFailureKind.Rejected);
        await client.Containers.DidNotReceiveWithAnyArgs().RemoveContainerAsync(default!, default!, default);
    }

    private static IDockerClient ClientWithVolumes(params string[] existing)
    {
        var client = Substitute.For<IDockerClient>();
        client.Volumes.InspectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            existing.Contains(call.Arg<string>())
                ? Task.FromResult(new VolumeResponse { Name = call.Arg<string>() })
                : throw new DockerApiException(HttpStatusCode.NotFound, "no such volume"));
        client.Volumes.CreateAsync(Arg.Any<VolumesCreateParameters>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new VolumeResponse { Name = call.Arg<VolumesCreateParameters>().Name }));
        return client;
    }

    [Fact]
    public async Task A_volume_that_predates_the_field_is_handed_over_on_the_next_apply()
    {
        var ownership = Substitute.For<IDockerVolumeGroupOwnership>();
        ownership.EnsureAsync(default!, default!, default).ReturnsForAnyArgs(DockerOperationResult.Ok());
        var client = ClientWithVolumes("reeve-volume-data");
        var spec = Spec(1000, Volume("data", "/data"));

        var result = await new DockerVolumeManager(ownership).EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        await client.Volumes.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
        await ownership.Received(1).EnsureAsync(client, spec, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_new_volume_and_a_restore_adopted_one_are_both_handed_over_after_they_exist()
    {
        var ownership = Substitute.For<IDockerVolumeGroupOwnership>();
        ownership.EnsureAsync(default!, default!, default).ReturnsForAnyArgs(DockerOperationResult.Ok());
        var client = ClientWithVolumes("reeve-volume-retained");
        var spec = Spec(1000, Volume("fresh", "/fresh"), Volume("retained", "/retained") with { ExistingClaim = true });

        var result = await new DockerVolumeManager(ownership).EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        await client.Volumes.Received(1).CreateAsync(Arg.Is<VolumesCreateParameters>(p => p.Name == "reeve-volume-fresh"), Arg.Any<CancellationToken>());
        await ownership.Received(1).EnsureAsync(client, spec, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_adopted_volume_fails_before_anything_is_handed_over()
    {
        var ownership = Substitute.For<IDockerVolumeGroupOwnership>();
        var client = ClientWithVolumes();
        var spec = Spec(1000, Volume("retained", "/retained") with { ExistingClaim = true });

        var result = await new DockerVolumeManager(ownership).EnsureVolumesAsync(client, spec, new DockerProviderSettings(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        await ownership.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_failed_hand_over_fails_the_volume_step()
    {
        var ownership = Substitute.For<IDockerVolumeGroupOwnership>();
        ownership.EnsureAsync(default!, default!, default)
            .ReturnsForAnyArgs(DockerOperationResult.Fail(DockerFailureKind.Rejected, "image has no chgrp"));
        var client = ClientWithVolumes("reeve-volume-data");

        var result = await new DockerVolumeManager(ownership)
            .EnsureVolumesAsync(client, Spec(1000, Volume("data", "/data")), new DockerProviderSettings(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("chgrp");
    }

    private static (string Gid, string DefaultGid)? ChgrpTarget()
    {
        var current = RunAsync("id", "-g").GetAwaiter().GetResult().Trim();
        if (RunAsync("id", "-u").GetAwaiter().GetResult().Trim() == "0")
        {
            return ("4242", current);
        }

        var other = RunAsync("id", "-G").GetAwaiter().GetResult().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(g => g != current);
        return other is null ? null : (other, current);
    }

    private static async Task<string> RunAsync(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);
        var err = await stderr;
        process.ExitCode.Should().Be(0, $"`{file} {string.Join(' ', args)}` failed: {err}");
        return await stdout;
    }

    [SkippableFact]
    public async Task The_script_hands_a_mismatched_volume_to_the_group_once_and_never_follows_a_planted_symlink()
    {
        var target = ChgrpTarget();
        Skip.If(target is null, "a single-group, non-root user cannot chgrp to anything else, so there is nothing to observe.");
        var (gid, defaultGid) = target!.Value;

        var root = Directory.CreateTempSubdirectory("volown-").FullName;
        try
        {
            var volume = Path.Combine(root, "volume");
            var sub = Path.Combine(volume, "sub");
            var outside = Path.Combine(root, "outside.txt");
            Directory.CreateDirectory(sub);
            await File.WriteAllTextAsync(Path.Combine(volume, "a.bin"), "a");
            await File.WriteAllTextAsync(Path.Combine(sub, "b.bin"), "b");
            await File.WriteAllTextAsync(outside, "outside");
            File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.CreateSymbolicLink(Path.Combine(volume, "link"), outside);
            foreach (var f in new[] { Path.Combine(volume, "a.bin"), Path.Combine(sub, "b.bin") })
            {
                File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await RunScriptAsync(gid, volume);

            (await Stat("%g", volume)).Should().Be(gid);
            (await Stat("%g", sub)).Should().Be(gid);
            (await Stat("%g", Path.Combine(sub, "b.bin"))).Should().Be(gid);
            File.GetUnixFileMode(volume).Should().HaveFlag(UnixFileMode.SetGroup)
                .And.HaveFlag(UnixFileMode.GroupRead).And.HaveFlag(UnixFileMode.GroupWrite).And.HaveFlag(UnixFileMode.GroupExecute);
            File.GetUnixFileMode(sub).Should().HaveFlag(UnixFileMode.SetGroup);
            File.GetUnixFileMode(Path.Combine(sub, "b.bin")).Should().HaveFlag(UnixFileMode.GroupWrite)
                .And.NotHaveFlag(UnixFileMode.SetGroup);

            (await Stat("%g", outside)).Should().Be(defaultGid);
            File.GetUnixFileMode(outside).Should().NotHaveFlag(UnixFileMode.GroupWrite);

            File.SetUnixFileMode(Path.Combine(sub, "b.bin"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await RunScriptAsync(gid, volume);
            File.GetUnixFileMode(Path.Combine(sub, "b.bin")).Should().NotHaveFlag(UnixFileMode.GroupWrite);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_script_leaves_no_file_with_group_write_but_no_group_read_behind()
    {
        var gid = (await RunAsync("id", "-g")).Trim();
        var root = Directory.CreateTempSubdirectory("volown-").FullName;
        try
        {
            var volume = Path.Combine(root, "volume");
            Directory.CreateDirectory(volume);
            var file = Path.Combine(volume, "f");
            await File.WriteAllTextAsync(file, "x");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);

            await RunScriptAsync(gid, volume);

            File.GetUnixFileMode(file).Should().HaveFlag(UnixFileMode.GroupRead).And.HaveFlag(UnixFileMode.GroupWrite);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_script_exits_127_when_the_image_lacks_a_tool_it_needs()
    {
        var empty = Directory.CreateTempSubdirectory("volown-").FullName;
        try
        {
            (await RunScriptForExitCodeAsync("4242", "/data", replacePath: empty)).Should().Be(127);
        }
        finally
        {
            Directory.Delete(empty);
        }
    }

    [Fact]
    public async Task The_script_fails_when_a_mount_path_cannot_be_handed_over()
    {
        var exit = await RunScriptForExitCodeAsync("4242", "/definitely/not/a/volume");

        exit.Should().NotBe(0);
    }

    [SkippableFact]
    public async Task A_run_that_fails_midway_leaves_the_volume_unmarked_so_the_next_run_finishes_it()
    {
        var target = ChgrpTarget();
        Skip.If(target is null, "a single-group, non-root user cannot chgrp to anything else, so there is nothing to observe.");
        var (gid, _) = target!.Value;

        var root = Directory.CreateTempSubdirectory("volown-").FullName;
        try
        {
            var volume = Path.Combine(root, "volume");
            Directory.CreateDirectory(Path.Combine(volume, "a", "b"));
            var files = Enumerable.Range(0, 20).Select(i => Path.Combine(volume, "a", "b", $"f{i}")).ToList();
            foreach (var f in files)
            {
                await File.WriteAllTextAsync(f, "x");
                File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            }

            var shim = Path.Combine(root, "shim");
            Directory.CreateDirectory(shim);
            var shimPath = Path.Combine(shim, "chmod");
            await File.WriteAllTextAsync(shimPath, "#!/bin/sh\nfor a in \"$@\"; do [ \"$a\" = \"g+rw\" ] && exit 1; done\nexec /bin/chmod \"$@\"\n");
            File.SetUnixFileMode(shimPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            (await RunScriptForExitCodeAsync(gid, volume, pathPrefix: shim)).Should().NotBe(0, "a failed pass is a failed run");
            File.GetUnixFileMode(volume).Should().NotHaveFlag(UnixFileMode.SetGroup, "the root is the done marker and must not be set by a run that did not finish");

            await RunScriptAsync(gid, volume);

            files.Should().OnlyContain(f => File.GetUnixFileMode(f).HasFlag(UnixFileMode.GroupWrite), "the retry must not skip the volume");
            File.GetUnixFileMode(volume).Should().HaveFlag(UnixFileMode.SetGroup);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_helper_that_does_not_finish_in_its_budget_is_a_failure_the_next_apply_retries_and_is_removed()
    {
        var daemon = new FakeDaemon();
        daemon.Client.Containers.WaitContainerAsync("helper-1", Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()).ContinueWith<ContainerWaitResponse>(_ => throw new OperationCanceledException()));

        var result = await new DockerVolumeGroupOwnership(TimeSpan.FromMilliseconds(50))
            .EnsureAsync(daemon.Client, Spec(1000, Volume("data", "/data")), CancellationToken.None);

        result.Succeeded.Should().BeFalse("a timeout must never read as a finished hand-over");
        result.FailureKind.Should().Be(DockerFailureKind.Error);
        result.Message.Should().Contain("next apply");
        await daemon.RemovedHelper();
    }

    [Fact]
    public async Task A_daemon_that_reports_an_error_for_the_helper_fails_even_with_exit_code_zero()
    {
        var daemon = new FakeDaemon();
        daemon.Client.Containers.WaitContainerAsync("helper-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ContainerWaitResponse { StatusCode = 0, Error = new WaitExitError { Message = "oom" } }));

        var result = await new DockerVolumeGroupOwnership().EnsureAsync(daemon.Client, Spec(1000, Volume("data", "/data")), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task A_helper_a_crash_left_behind_is_removed_before_the_next_one_runs_and_only_this_instances()
    {
        var daemon = new FakeDaemon();
        daemon.Client.Containers.ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<ContainerListResponse>>(new List<ContainerListResponse> { new() { ID = "stale-1" } }));
        var spec = Spec(1000, Volume("data", "/data"));

        var result = await new DockerVolumeGroupOwnership().EnsureAsync(daemon.Client, spec, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        await daemon.Client.Containers.Received(1).RemoveContainerAsync("stale-1", Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>());
        await daemon.Client.Containers.Received(1).ListContainersAsync(
            Arg.Is<ContainersListParameters>(p => p.All == true
                && p.Filters["label"].ContainsKey($"{DockerVolumeGroupOwnership.HelperInstanceLabelKey}={spec.InstanceId:D}")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_helper_runs_under_resource_limits_so_plugin_supplied_binaries_cannot_exhaust_the_host()
    {
        var daemon = new FakeDaemon();

        await new DockerVolumeGroupOwnership().EnsureAsync(daemon.Client, Spec(1000, Volume("data", "/data")), CancellationToken.None);

        var host = daemon.Created.Single().HostConfig;
        host.Memory.Should().BeGreaterThan(0);
        host.PidsLimit.Should().BeGreaterThan(0);
        host.NanoCPUs.Should().BeGreaterThan(0);
    }

    private static Task<string> Stat(string format, string path) => RunAsync("stat", "-c", format, path).ContinueWith(t => t.Result.Trim());

    private static async Task RunScriptAsync(string gid, string path)
        => (await RunScriptForExitCodeAsync(gid, path)).Should().Be(0);

    private static async Task<int> RunScriptForExitCodeAsync(string gid, string path, string? pathPrefix = null, string? replacePath = null)
    {
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (pathPrefix is not null)
        {
            psi.Environment["PATH"] = pathPrefix + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }

        if (replacePath is not null)
        {
            psi.Environment["PATH"] = replacePath;
        }

        foreach (var a in new[] { "-c", DockerVolumeGroupOwnership.Script, "reeve-volume-ownership", gid, path })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);
        await stdout;
        await stderr;
        return process.ExitCode;
    }
}
