using Reeve.Workloads.Abstractions.Providers;
using System;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesResourceRenderRefusalTests
{
    private static readonly KubernetesRenderContext Ctx = new() { Namespace = "reeve" };
    private static readonly KubernetesWorkloadRenderer Renderer = new();
    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string SpecHash = "abc123def456";

    [Fact]
    public void An_unrenderable_memory_limit_never_yields_a_kubernetes_container_without_that_limit()
    {
        Assert.Throws<WorkloadResourceRenderException>(
            () => Renderer.Render(Spec(new ResolvedResources { LimitsMemory = "64 MB" }), Ctx));
    }

    [Fact]
    public void An_unrenderable_cpu_limit_never_yields_a_kubernetes_container_without_that_limit()
    {
        Assert.Throws<WorkloadResourceRenderException>(
            () => Renderer.Render(Spec(new ResolvedResources { LimitsCpu = "garbage" }), Ctx));
    }

    [Fact]
    public void A_renderable_limit_reaches_the_container_intact()
    {
        var rendered = Renderer.Render(Spec(new ResolvedResources { LimitsMemory = "512Mi" }), Ctx);

        var container = rendered.Deployment!.Spec.Template.Spec.Containers[0];
        container.Resources.Limits["memory"].ToString().Should().Be("512Mi");
    }

    private static WorkloadDesiredSpec Spec(ResolvedResources resources) => new()
    {
        GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        GroupId = "com.reeve.matter",
        WorkloadId = "matter",
        InstanceId = InstanceId,
        Inst8 = "11111111",
        Provider = KubernetesProvider.Id,
        ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
        SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
        Labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                 {
                     InstanceId = InstanceId,
                     InstallationId = "install-1",
                     SpecHash = SpecHash,
                 }),
        Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
        Kind = WorkloadKind.SingleNode,
        Resources = resources,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        SpecHash = SpecHash,
    };
}
