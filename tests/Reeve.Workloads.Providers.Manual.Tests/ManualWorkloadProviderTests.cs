using Reeve.Workloads.Domain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Providers.Manual;
using Xunit;

namespace Reeve.Workloads.Providers.Manual.Tests;

public sealed class ManualWorkloadProviderTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static WorkloadDesiredSpec Spec(string specHash = "abc123def456") => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "com.reeve.matter",
        WorkloadId = "matter",
        InstanceId = InstanceId,
        Inst8 = "11111111",
        Provider = ManualProvider.Id,
        ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
        SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
        Labels = new Dictionary<string, string> { ["reeve.io/workload-instance"] = InstanceId.ToString() },
        Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
        Env = new List<ResolvedEnvVar>
        {
            new() { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "reeve-wl-com-reeve-matter-matter-11111111" },
        },
        HostRequirements = new List<HostRequirement>(),
        SpecHash = specHash,
    };

    private static WorkloadRef Ref() => new()
    {
        InstanceId = InstanceId,
        GroupId = "com.reeve.matter",
        WorkloadId = "matter",
        Inst8 = "11111111",
        Provider = ManualProvider.Id,
    };

    private static ManualWorkloadProvider Build(IWorkloadExternalLiveness? liveness = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(liveness ?? new FakeLiveness());
        return new(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ManualWorkloadProvider>.Instance);
    }

    [Fact]
    public void Reports_manual_provider()
    {
        var provider = Build();

        provider.Descriptor.Id.Should().Be(ManualProvider.Id);
    }

    [Fact]
    public async Task Apply_is_a_success_handoff_not_a_deploy()
    {
        var provider = Build();

        var result = await provider.ApplyAsync(Spec(), progress: null);

        result.Succeeded.Should().BeTrue();
        result.Reason.Should().Be(WorkloadApplyReason.Applied);
        result.AppliedSpecHash.Should().Be("abc123def456");
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Apply_is_idempotent_across_repeated_calls()
    {
        var provider = Build();

        var first = await provider.ApplyAsync(Spec(), progress: null);
        var second = await provider.ApplyAsync(Spec(), progress: null);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.AppliedSpecHash.Should().Be(first.AppliedSpecHash);
    }

    [Fact]
    public async Task Status_when_live_is_ready_mapping_to_externally_managed()
    {
        var liveness = new FakeLiveness(live: true);
        var provider = Build(liveness);

        var status = await provider.GetStatusAsync(Ref());

        status.Phase.Should().Be(WorkloadProviderPhase.Ready);
        status.ProviderReady.Should().BeTrue();
        status.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Status_when_not_live_is_pending_awaiting_operator_deploy()
    {
        var liveness = new FakeLiveness(live: false);
        var provider = Build(liveness);

        var status = await provider.GetStatusAsync(Ref());

        status.Phase.Should().Be(WorkloadProviderPhase.Pending);
        status.ProviderReady.Should().BeFalse();
        status.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Status_extracts_the_instance_id_from_the_ref()
    {
        var liveness = new FakeLiveness(live: true);
        var provider = Build(liveness);

        await provider.GetStatusAsync(Ref());

        liveness.LastQueriedInstanceId.Should().Be(InstanceId);
    }

    [Fact]
    public async Task Status_on_a_liveness_fault_is_unknown_never_pending_and_never_throws()
    {
        var liveness = new ThrowingLiveness(new InvalidOperationException("liveness store unreadable"));
        var provider = Build(liveness);

        var act = async () => await provider.GetStatusAsync(Ref());

        var result = await act.Should().NotThrowAsync();
        result.Subject.Phase.Should().Be(WorkloadProviderPhase.Unknown);
        result.Subject.ProviderReady.Should().BeFalse();
        result.Subject.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Status_propagates_caller_cancellation_and_does_not_map_it_to_unknown()
    {
        var liveness = new ThrowingLiveness(new OperationCanceledException());
        var provider = Build(liveness);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await provider.GetStatusAsync(Ref(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Preflight_checks_no_gateway_url_and_blocks_nothing()
    {
        var result = await Build().PreflightAsync(Spec());

        result.Checks.Should().BeEmpty();
        result.HasBlockingError.Should().BeFalse();
    }

    [Fact]
    public async Task Teardown_suspend_resume_are_clean_no_ops()
    {
        var provider = Build();

        var teardown = async () => await provider.TeardownAsync(Ref(), retainVolumes: true);
        var suspend = async () => await provider.SuspendAsync(Ref());
        var resume = async () => await provider.ResumeAsync(Ref());

        await teardown.Should().NotThrowAsync();
        await suspend.Should().NotThrowAsync();
        await resume.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Recent_logs_are_empty_with_manual_mode_guidance()
    {
        var provider = Build();

        var logs = await provider.GetRecentLogsAsync(Ref(), tailLines: 100, previousContainer: false);

        logs.Lines.Should().ContainSingle();
        logs.Lines[0].Text.Should().ContainAny("docker logs", "kubectl logs");
    }

    [Fact]
    public async Task A_live_tail_shows_the_guidance_line_once()
    {
        var provider = Build();

        var first = WorkloadLogTail.LinesAfter(
            (await provider.GetRecentLogsAsync(Ref(), tailLines: 201, previousContainer: false)).Lines, after: null, maxLines: 200);
        var next = WorkloadLogTail.LinesAfter(
            (await provider.GetRecentLogsAsync(Ref(), tailLines: 201, previousContainer: false)).Lines, first.Lines[^1], maxLines: 200);

        first.Lines.Should().ContainSingle().Which.AtUtc.Should().BeNull();
        next.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Declared_host_requirements_are_echoed_as_a_note_that_does_not_block()
    {
        var provider = Build();
        var spec = Spec() with
        {
            HostRequirements = new List<HostRequirement>
            {
                new() { Kind = HostRequirementKind.DbusService, Name = "bluetoothd" },
                new() { Kind = HostRequirementKind.KernelModule, Name = "tun" },
            },
        };

        var result = await provider.PreflightAsync(spec);

        var note = result.Checks.Should().ContainSingle(c => c.Id == "host-requirements").Subject;
        note.Passed.Should().BeTrue();
        note.Message.Should().Contain("bluetoothd").And.Contain("tun");
        result.HasBlockingError.Should().BeFalse();
        result.AllPassed.Should().BeTrue();
    }

    [Fact]
    public async Task An_unnamed_host_requirement_is_named_by_its_kind()
    {
        var provider = Build();
        var spec = Spec() with
        {
            HostRequirements = new List<HostRequirement> { new() { Kind = HostRequirementKind.Multicast, Name = null } },
        };

        var result = await provider.PreflightAsync(spec);

        result.Checks.Should().ContainSingle(c => c.Id == "host-requirements")
            .Which.Message.Should().Contain(nameof(HostRequirementKind.Multicast));
    }

    [Fact]
    public async Task Repeated_prerequisite_names_are_listed_once()
    {
        var provider = Build();
        var spec = Spec() with
        {
            HostRequirements = new List<HostRequirement>
            {
                new() { Kind = HostRequirementKind.KernelModule, Name = "tun" },
                new() { Kind = HostRequirementKind.UdevSerial, Name = "tun" },
            },
        };

        var result = await provider.PreflightAsync(spec);

        var message = result.Checks.Single(c => c.Id == "host-requirements").Message;
        CountOf(message, "tun").Should().Be(1);
    }

    [Fact]
    public async Task Declared_platforms_are_echoed_as_a_note_that_does_not_block()
    {
        var provider = Build();
        var spec = Spec() with { Platforms = new[] { "linux/amd64", "linux/arm64" } };

        var result = await provider.PreflightAsync(spec);

        var note = result.Checks.Should().ContainSingle(c => c.Id == "platforms").Subject;
        note.Passed.Should().BeTrue();
        note.Message.Should().Contain("linux/amd64").And.Contain("linux/arm64");
        result.HasBlockingError.Should().BeFalse();
        result.AllPassed.Should().BeTrue();
    }

    [Fact]
    public async Task A_spec_declaring_neither_gets_neither_note()
    {
        var provider = Build();

        var result = await provider.PreflightAsync(Spec());

        result.Checks.Should().NotContain(c => c.Id == "host-requirements");
        result.Checks.Should().NotContain(c => c.Id == "platforms");
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }


    private sealed class FakeLiveness(bool live = false) : IWorkloadExternalLiveness
    {
        public Guid? LastQueriedInstanceId { get; private set; }

        public ValueTask<bool> IsLiveAsync(Guid instanceId, CancellationToken cancellationToken = default)
        {
            LastQueriedInstanceId = instanceId;
            return ValueTask.FromResult(live);
        }
    }

    private sealed class ThrowingLiveness(Exception toThrow) : IWorkloadExternalLiveness
    {
        public ValueTask<bool> IsLiveAsync(Guid instanceId, CancellationToken cancellationToken = default)
            => throw toThrow;
    }
}
