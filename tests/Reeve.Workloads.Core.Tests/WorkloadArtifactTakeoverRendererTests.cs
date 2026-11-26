using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Rendering;
using Reeve.Workloads.Providers.Docker.Rendering;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Kubernetes.Rendering;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadArtifactTakeoverRendererTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly ScriptedArtifactService artifactService = new();
    private readonly RecordingAuditSink auditSink = new();

    private IWorkloadArtifactRenderer[] renderers =
    [
        new EnvArtifactRenderer(),
        new HelmValuesArtifactRenderer(),
        new KubernetesArtifactRenderer(),
        new ComposeArtifactRenderer(),
    ];

    private WorkloadArtifactTakeoverRenderer Build() => new(
        artifactService,
        new WorkloadArtifactRendererRegistry(renderers),
        auditSink,
        NullLogger<WorkloadArtifactTakeoverRenderer>.Instance);

    private static WorkloadInstance Instance()
        => WorkloadInstance.Create(InstanceId, GroupInstanceId, "com.reeve.matter", "gateway", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task The_takeover_never_reveals_and_so_never_rotates_the_running_token()
    {
        await Build().RenderTakeoverAsync(Instance());

        artifactService.Calls.Should().OnlyContain(c => c.RevealToken == false);
        artifactService.Calls.Should().OnlyContain(c => c.ActorUserId == null);
    }

    [Fact]
    public async Task Both_the_compose_and_kubernetes_artifacts_are_rendered()
    {
        await Build().RenderTakeoverAsync(Instance());

        artifactService.Calls.Select(c => c.Format).Should()
            .Equal(WorkloadArtifactFormatId.Parse("compose"), WorkloadArtifactFormatId.Parse("kubernetes"));
    }

    [Fact]
    public async Task A_third_party_self_contained_format_is_handed_over_too()
    {
        renderers = [.. renderers, new StubRenderer("acme.nomad-job", isSelfContained: true)];

        await Build().RenderTakeoverAsync(Instance());

        artifactService.Calls.Select(c => c.Format.Value).Should().Equal("acme.nomad-job", "compose", "kubernetes");
    }

    [Fact]
    public async Task A_takeover_with_no_self_contained_renderer_aborts_before_rendering_anything()
    {
        renderers = [new EnvArtifactRenderer(), new HelmValuesArtifactRenderer()];

        await FluentActions.Awaiting(() => Build().RenderTakeoverAsync(Instance()))
            .Should().ThrowAsync<WorkloadSwitchTakeoverRenderException>()
            .WithMessage($"*{InstanceId}*nothing to hand over*");

        artifactService.Calls.Should().BeEmpty();
        auditSink.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkloadArtifactRenderOutcome.NotFound)]
    [InlineData(WorkloadArtifactRenderOutcome.HostRefused)]
    [InlineData(WorkloadArtifactRenderOutcome.NoHandAppliedProvider)]
    public async Task A_non_success_outcome_aborts_the_switch_and_says_which(WorkloadArtifactRenderOutcome outcome)
    {
        artifactService.Outcome = outcome;

        var thrown = await FluentActions.Awaiting(() => Build().RenderTakeoverAsync(Instance()))
            .Should().ThrowAsync<WorkloadSwitchTakeoverRenderException>();

        thrown.Which.Message.Should().Contain(outcome.ToString()).And.Contain("scripted failure");
    }

    [Fact]
    public async Task A_host_refusal_aborts_the_takeover_with_the_hosts_reason()
    {
        artifactService.Outcome = WorkloadArtifactRenderOutcome.HostRefused;
        artifactService.Workloads = [];
        artifactService.Error = "no external gateway address is configured";

        var thrown = await FluentActions.Awaiting(() => Build().RenderTakeoverAsync(Instance()))
            .Should().ThrowAsync<WorkloadSwitchTakeoverRenderException>();

        thrown.Which.Message.Should().Contain("HostRefused").And.Contain("no external gateway address is configured");
        auditSink.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_successful_render_that_produced_no_artifacts_still_aborts()
    {
        artifactService.Workloads = [];
        artifactService.Outcome = WorkloadArtifactRenderOutcome.Success;

        await FluentActions.Awaiting(() => Build().RenderTakeoverAsync(Instance()))
            .Should().ThrowAsync<WorkloadSwitchTakeoverRenderException>()
            .WithMessage("*no artifacts*");
    }

    [Fact]
    public async Task One_faulted_workload_aborts_the_whole_takeover()
    {
        artifactService.Workloads =
        [
            Item("gateway"),
            new RenderedWorkloadArtifactItem { WorkloadId = "bridge", Artifact = null, Error = "pod patch is not valid yaml" },
        ];

        await FluentActions.Awaiting(() => Build().RenderTakeoverAsync(Instance()))
            .Should().ThrowAsync<WorkloadSwitchTakeoverRenderException>()
            .WithMessage("*bridge*");
    }

    [Fact]
    public async Task The_second_format_is_never_attempted_after_the_first_aborts()
    {
        artifactService.Outcome = WorkloadArtifactRenderOutcome.HostRefused;

        await FluentActions.Awaiting(() => Build().RenderTakeoverAsync(Instance()))
            .Should().ThrowAsync<WorkloadSwitchTakeoverRenderException>();

        artifactService.Calls.Should().ContainSingle()
            .Which.Format.Should().Be(WorkloadArtifactFormatId.Parse("compose"));
    }

    [Fact]
    public async Task An_aborted_render_leaves_no_rendered_audit_trail()
    {
        artifactService.Outcome = WorkloadArtifactRenderOutcome.HostRefused;

        await FluentActions.Awaiting(() => Build().RenderTakeoverAsync(Instance()))
            .Should().ThrowAsync<WorkloadSwitchTakeoverRenderException>();

        auditSink.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_completed_takeover_audits_each_format_and_records_that_nothing_was_revealed()
    {
        await Build().RenderTakeoverAsync(Instance());

        auditSink.Entries.Should().HaveCount(2);
        auditSink.Entries.Should().OnlyContain(e => e.Action == WorkloadArtifactTakeoverRenderer.TakeoverAuditAction);
        auditSink.Entries.Should().OnlyContain(e => e.EntityId == InstanceId);
        foreach (var entry in auditSink.Entries)
        {
            System.Text.Json.JsonSerializer.Serialize(entry.Metadata).Should().Contain("\"tokenRevealed\":false");
        }
    }

    [Fact]
    public async Task The_audit_spells_each_format_the_way_6x_did()
    {
        await Build().RenderTakeoverAsync(Instance());

        auditSink.Entries.Select(e => System.Text.Json.JsonSerializer.Serialize(e.Metadata)).Should().SatisfyRespectively(
            compose => compose.Should().Contain("\"format\":\"Compose\""),
            kubernetes => kubernetes.Should().Contain("\"format\":\"Kubernetes\""));
    }

    [Fact]
    public async Task A_failing_audit_sink_does_not_abort_a_complete_takeover()
    {
        auditSink.Fault = new InvalidOperationException("audit db down");

        await FluentActions.Awaiting(() => Build().RenderTakeoverAsync(Instance())).Should().NotThrowAsync();

        artifactService.Calls.Should().HaveCount(2, "both formats still render");
    }

    [Fact]
    public async Task A_null_instance_is_refused_before_anything_renders()
    {
        await FluentActions.Awaiting(() => Build().RenderTakeoverAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();

        artifactService.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_stops_the_takeover_between_formats()
    {
        using var cts = new CancellationTokenSource();
        artifactService.OnCall = () => cts.Cancel();

        await FluentActions.Awaiting(() => Build().RenderTakeoverAsync(Instance(), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        artifactService.Calls.Should().ContainSingle();
    }

    [Fact]
    public void Every_missing_dependency_is_refused_at_construction()
    {
        var log = NullLogger<WorkloadArtifactTakeoverRenderer>.Instance;
        var registry = new WorkloadArtifactRendererRegistry(renderers);
        var attempts = new Action[]
        {
            () => _ = new WorkloadArtifactTakeoverRenderer(null!, registry, auditSink, log),
            () => _ = new WorkloadArtifactTakeoverRenderer(artifactService, null!, auditSink, log),
            () => _ = new WorkloadArtifactTakeoverRenderer(artifactService, registry, null!, log),
            () => _ = new WorkloadArtifactTakeoverRenderer(artifactService, registry, auditSink, null!),
        };

        attempts.Should().AllSatisfy(attempt => attempt.Should().Throw<ArgumentNullException>());
    }

    private static RenderedWorkloadArtifactItem Item(string workloadId) => new()
    {
        WorkloadId = workloadId,
        Artifact = new RenderedWorkloadArtifact
        {
            Format = WorkloadArtifactFormatId.Parse("compose"),
            PrimaryContent = $"# {workloadId}",
        },
    };

    private sealed class StubRenderer(string format, bool isSelfContained) : IWorkloadArtifactRenderer
    {
        public WorkloadArtifactFormatId Format { get; } = WorkloadArtifactFormatId.Parse(format);

        public bool IsSelfContained => isSelfContained;

        public RenderedWorkloadArtifact Render(WorkloadDesiredSpec spec, WorkloadArtifactRenderOptions options)
            => throw new InvalidOperationException("the takeover renders through the artifact service, never directly");
    }

    private sealed record RenderCall(Guid InstanceId, WorkloadArtifactFormatId Format, bool RevealToken, Guid? ActorUserId);

    private sealed class ScriptedArtifactService : IWorkloadArtifactService
    {
        public List<RenderCall> Calls { get; } = [];

        public WorkloadArtifactRenderOutcome Outcome { get; set; } = WorkloadArtifactRenderOutcome.Success;

        public IReadOnlyList<RenderedWorkloadArtifactItem> Workloads { get; set; } = [Item("gateway")];

        public Action? OnCall { get; set; }

        public string Error { get; set; } = "scripted failure";

        public Task<WorkloadArtifactRenderResult> RenderAsync(
            Guid instanceId, WorkloadArtifactFormatId format, bool revealToken, Guid? actorUserId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new RenderCall(instanceId, format, revealToken, actorUserId));
            OnCall?.Invoke();
            return Task.FromResult(new WorkloadArtifactRenderResult
            {
                Outcome = Outcome,
                Format = format,
                Error = Outcome == WorkloadArtifactRenderOutcome.Success ? null : Error,
                Workloads = Workloads,
            });
        }
    }

    private sealed class RecordingAuditSink : IWorkloadAuditSink
    {
        public List<WorkloadAuditEntry> Entries { get; } = [];

        public Exception? Fault { get; set; }

        public Task WriteAsync(WorkloadAuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (Fault is not null)
            {
                return Task.FromException(Fault);
            }

            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
