using System;
using System.Collections.Generic;
using FluentAssertions;
using static Reeve.Workloads.Core.Tests.Rendering.ArtifactSpecFactory;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Core.Rendering;
using Reeve.Workloads.Providers.Docker.Rendering;
using Reeve.Workloads.Providers.Kubernetes.Rendering;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Rendering;

public sealed class WorkloadArtifactRendererRegistryTests
{
    private static readonly WorkloadArtifactRenderOptions Options =
        new() { ExternalSecretMode = true };

    private static WorkloadArtifactRendererRegistry FullRegistry() => new(new IWorkloadArtifactRenderer[]
    {
        new ComposeArtifactRenderer(),
        new KubernetesArtifactRenderer(),
        new HelmValuesArtifactRenderer(),
        new EnvArtifactRenderer(),
    });

    [Fact]
    public void Render_dispatches_to_the_renderer_for_the_requested_format()
    {
        var registry = FullRegistry();

        registry.Render(WorkloadArtifactFormatId.Parse("env"), Spec(), Options).Format.Should().Be(WorkloadArtifactFormatId.Parse("env"));
        registry.Render(WorkloadArtifactFormatId.Parse("compose"), Spec(), Options).Format.Should().Be(WorkloadArtifactFormatId.Parse("compose"));
        registry.Render(WorkloadArtifactFormatId.Parse("helm-values"), Spec(), Options).Format.Should().Be(WorkloadArtifactFormatId.Parse("helm-values"));
        registry.Render(WorkloadArtifactFormatId.Parse("kubernetes"), Spec(), Options).Format.Should().Be(WorkloadArtifactFormatId.Parse("kubernetes"));
    }

    [Fact]
    public void Supported_formats_reflects_the_registered_renderers()
    {
        FullRegistry().SupportedFormats.Should().BeEquivalentTo(new[]
        {
            WorkloadArtifactFormatId.Parse("compose"),
            WorkloadArtifactFormatId.Parse("kubernetes"),
            WorkloadArtifactFormatId.Parse("helm-values"),
            WorkloadArtifactFormatId.Parse("env"),
        });
    }

    [Fact]
    public void An_unknown_format_throws_a_clear_exception()
    {
        var registry = new WorkloadArtifactRendererRegistry(new IWorkloadArtifactRenderer[] { new EnvArtifactRenderer() });

        var act = () => registry.Render(WorkloadArtifactFormatId.Parse("kubernetes"), Spec(), Options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'kubernetes'*");
    }

    [Fact]
    public void Self_contained_formats_are_the_declared_ones_in_id_order()
    {
        var registry = new WorkloadArtifactRendererRegistry(new IWorkloadArtifactRenderer[]
        {
            new KubernetesArtifactRenderer(),
            new EnvArtifactRenderer(),
            new HelmValuesArtifactRenderer(),
            new ComposeArtifactRenderer(),
        });

        registry.SelfContainedFormats.Should().Equal(
            WorkloadArtifactFormatId.Parse("compose"),
            WorkloadArtifactFormatId.Parse("kubernetes"));
    }

    [Fact]
    public void A_duplicate_format_registration_throws_at_construction()
    {
        var act = () => new WorkloadArtifactRendererRegistry(new IWorkloadArtifactRenderer[]
        {
            new EnvArtifactRenderer(),
            new EnvArtifactRenderer(),
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Env*");
    }

    [Fact]
    public void A_null_renderer_collection_throws()
    {
        var act = () => new WorkloadArtifactRendererRegistry(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
