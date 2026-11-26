using Reeve.Workloads.Abstractions.Providers;
using System;
using System.Collections.Generic;
using Docker.DotNet.Models;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerResourceMetricsTests
{
    private static readonly DockerResourceRenderer Renderer = new();

    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * 1024L * 1024L;
    private const long NanoPerCpu = 1_000_000_000L;

    private static WorkloadDesiredSpec Spec(
        ResolvedResources? resources = null,
        WorkloadObservability? observability = null)
    {
        const string specHash = "abc123def456";
        var instanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = instanceId,
                         InstallationId = "install-1",
                         SpecHash = specHash,
                     });

        return new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = instanceId,
            Inst8 = "11111111",
            Provider = DockerProvider.Id,
            ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
            SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
            Labels = labels,
            Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Kind = WorkloadKind.SingleNode,
            Resources = resources,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Observability = observability,
            SpecHash = specHash,
        };
    }

    private static WorkloadObservability Metrics(int port, string path) =>
        new() { Metrics = new WorkloadMetrics { Port = port, Path = path } };

    [Fact]
    public void Limits_memory_and_cpu_map_to_exact_Memory_and_NanoCPUs()
    {
        var spec = Spec(resources: new ResolvedResources
        {
            LimitsMemory = "512Mi",
            LimitsCpu = "1",
        });

        var rendering = Renderer.RenderResources(spec);

        rendering.HostConfig.Memory.Should().Be(512L * MiB, "512Mi is a hard memory cap == HostConfig.Memory");
        rendering.HostConfig.NanoCPUs.Should().Be(1 * NanoPerCpu, "1 cpu == 1e9 nanocpu");
    }

    [Fact]
    public void Requests_memory_maps_to_MemoryReservation()
    {
        var spec = Spec(resources: new ResolvedResources
        {
            RequestsMemory = "256Mi",
        });

        var rendering = Renderer.RenderResources(spec);

        rendering.HostConfig.MemoryReservation.Should().Be(256L * MiB, "requests.memory is the soft reservation");
        rendering.HostConfig.Memory.Should().Be(0, "no limits.memory ⇒ no hard cap");
        rendering.HostConfig.NanoCPUs.Should().Be(0);
    }

    [Fact]
    public void Gi_memory_and_millicpu_are_parsed_exactly()
    {
        var spec = Spec(resources: new ResolvedResources
        {
            LimitsMemory = "2Gi",
            RequestsMemory = "1Gi",
            LimitsCpu = "500m",
        });

        var rendering = Renderer.RenderResources(spec);

        rendering.HostConfig.Memory.Should().Be(2L * GiB);
        rendering.HostConfig.MemoryReservation.Should().Be(1L * GiB);
        rendering.HostConfig.NanoCPUs.Should().Be(500_000_000L, "500m == 0.5 cpu == 5e8 nanocpu");
    }

    [Theory]
    [InlineData("2", 2_000_000_000L)]
    [InlineData("1500m", 1_500_000_000L)]
    [InlineData("250m", 250_000_000L)]
    [InlineData("0.5", 500_000_000L)]
    public void Cpu_limits_convert_to_nanocpus(string limitsCpu, long expectedNanos)
    {
        var spec = Spec(resources: new ResolvedResources { LimitsCpu = limitsCpu });

        Renderer.RenderResources(spec).HostConfig.NanoCPUs.Should().Be(expectedNanos);
    }

    [Theory]
    [InlineData("9223372036854775")]
    [InlineData("1e15")]
    public void An_absurd_cpu_limit_is_refused_rather_than_wrapped_into_a_negative_quota(string limitsCpu)
    {
        var spec = Spec(resources: new ResolvedResources { LimitsCpu = limitsCpu });

        Assert.Throws<WorkloadResourceRenderException>(() => Renderer.RenderResources(spec));
    }

    [Theory]
    [InlineData("64M", 64_000_000L)]
    [InlineData("1G", 1_000_000_000L)]
    [InlineData("1Ki", 1024L)]
    public void Memory_limits_honour_the_k8s_quantity_suffix_table(string limitsMemory, long expectedBytes)
    {
        var spec = Spec(resources: new ResolvedResources { LimitsMemory = limitsMemory });

        Renderer.RenderResources(spec).HostConfig.Memory.Should().Be(expectedBytes);
    }

    [Fact]
    public void Requests_cpu_is_ignored_and_never_becomes_NanoCPUs()
    {
        var spec = Spec(resources: new ResolvedResources { RequestsCpu = "250m" });

        var rendering = Renderer.RenderResources(spec);

        rendering.HostConfig.NanoCPUs.Should().Be(0, "requests.cpu is documented-ignored for docker");
        rendering.HostConfig.Memory.Should().Be(0);
        rendering.HostConfig.MemoryReservation.Should().Be(0);
    }

    [Fact]
    public void Requests_cpu_does_not_perturb_the_limits_cpu_quota()
    {
        var spec = Spec(resources: new ResolvedResources { RequestsCpu = "250m", LimitsCpu = "1" });

        Renderer.RenderResources(spec).HostConfig.NanoCPUs.Should().Be(1 * NanoPerCpu);
    }

    [Fact]
    public void Metrics_produce_the_exact_prometheus_and_reeve_scrape_labels()
    {
        var spec = Spec(observability: Metrics(9090, "/metrics"));

        var labels = Renderer.RenderResources(spec).MetricsLabels;

        labels.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["prometheus.io/scrape"] = "true",
            ["prometheus.io/port"] = "9090",
            ["prometheus.io/path"] = "/metrics",
            [DockerProviderConstants.MetricsScrapeLabelKey] = "true",
            [DockerProviderConstants.MetricsPortLabelKey] = "9090",
            [DockerProviderConstants.MetricsPathLabelKey] = "/metrics",
        });
    }

    [Fact]
    public void Metrics_labels_mirror_the_k8s_scrape_annotation_keys_and_reeve_keys()
    {
        var spec = Spec(observability: Metrics(8081, "/actuator/prometheus"));

        var labels = Renderer.RenderResources(spec).MetricsLabels;

        labels["prometheus.io/port"].Should().Be("8081");
        labels["prometheus.io/path"].Should().Be("/actuator/prometheus");
        labels[DockerProviderConstants.MetricsPortLabelKey].Should().Be("8081");
        labels[DockerProviderConstants.MetricsPathLabelKey].Should().Be("/actuator/prometheus");
        labels[DockerProviderConstants.MetricsScrapeLabelKey].Should().Be("true");
    }

    [Fact]
    public void No_resources_leaves_every_HostConfig_field_unset()
    {
        var rendering = Renderer.RenderResources(Spec());

        rendering.HostConfig.Should().NotBeNull("the fragment is always returned (required, non-null)");
        rendering.HostConfig.Memory.Should().Be(0, "unset ⇒ docker 'unlimited'");
        rendering.HostConfig.MemoryReservation.Should().Be(0);
        rendering.HostConfig.NanoCPUs.Should().Be(0);
    }

    [Fact]
    public void No_metrics_yields_an_empty_label_dict()
    {
        Renderer.RenderResources(Spec()).MetricsLabels.Should().BeEmpty();
    }

    [Fact]
    public void Empty_resources_record_leaves_the_HostConfig_unset()
    {
        var spec = Spec(resources: new ResolvedResources());

        var rendering = Renderer.RenderResources(spec);

        rendering.HostConfig.Memory.Should().Be(0);
        rendering.HostConfig.MemoryReservation.Should().Be(0);
        rendering.HostConfig.NanoCPUs.Should().Be(0);
    }

    [Fact]
    public void An_unparseable_memory_limit_is_refused_rather_than_rendered_as_unlimited()
    {
        var spec = Spec(resources: new ResolvedResources { LimitsMemory = "64 MB" });

        var ex = Assert.Throws<WorkloadResourceRenderException>(() => Renderer.RenderResources(spec));

        ex.Message.Should().Contain("64 MB");
    }

    [Fact]
    public void An_unparseable_cpu_limit_is_refused_rather_than_rendered_as_unlimited()
    {
        var spec = Spec(resources: new ResolvedResources { LimitsCpu = "garbage" });

        var ex = Assert.Throws<WorkloadResourceRenderException>(() => Renderer.RenderResources(spec));

        ex.Message.Should().Contain("garbage");
    }

    [Theory]
    [InlineData("512Mi", 512L * 1024 * 1024)]
    [InlineData("1Gi", 1024L * 1024 * 1024)]
    [InlineData("4Mi", 4L * 1024 * 1024)]
    public void Every_renderable_memory_limit_reaches_docker_as_a_non_zero_cap(string quantity, long expectedBytes)
    {
        var rendering = Renderer.RenderResources(Spec(resources: new ResolvedResources { LimitsMemory = quantity }));

        rendering.HostConfig.Memory.Should().Be(expectedBytes);
    }

    [Theory]
    [InlineData("6Mi")]
    [InlineData("64Mi")]
    [InlineData("256M")]
    [InlineData("1.5Gi")]
    public void Anything_the_operator_gate_accepts_renders_to_a_real_cap_never_to_unlimited(string quantity)
    {
        ResourceQuantityPolicy.Validate(ResourceQuantityKind.Memory, quantity).Should().BeNull();

        var rendering = Renderer.RenderResources(Spec(resources: new ResolvedResources { LimitsMemory = quantity }));

        rendering.HostConfig.Memory.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Same_spec_renders_the_same_output_pure()
    {
        var spec = Spec(
            resources: new ResolvedResources { LimitsMemory = "512Mi", LimitsCpu = "500m", RequestsMemory = "256Mi" },
            observability: Metrics(9090, "/metrics"));

        var first = Renderer.RenderResources(spec);
        var second = Renderer.RenderResources(spec);

        first.HostConfig.Memory.Should().Be(second.HostConfig.Memory);
        first.HostConfig.MemoryReservation.Should().Be(second.HostConfig.MemoryReservation);
        first.HostConfig.NanoCPUs.Should().Be(second.HostConfig.NanoCPUs);
        first.MetricsLabels.Should().BeEquivalentTo(second.MetricsLabels);
    }
}
