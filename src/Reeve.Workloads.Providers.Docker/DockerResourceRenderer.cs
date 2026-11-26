using System;
using System.Collections.Generic;
using System.Globalization;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerResourceRenderer : IDockerResourceRenderer
{
    private const string PrometheusScrapeKey = "prometheus.io/scrape";
    private const string PrometheusPortKey = "prometheus.io/port";
    private const string PrometheusPathKey = "prometheus.io/path";

    private const long NanoCpusPerCpu = 1_000_000_000L;

    public DockerResourceRendering RenderResources(WorkloadDesiredSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var hostConfig = new HostConfig();
        var resources = spec.Resources;
        if (resources is not null)
        {
            if (Require(ResourceQuantityKind.Memory, resources.LimitsMemory, out var limitMemoryBytes))
            {
                hostConfig.Memory = limitMemoryBytes;
            }

            if (Require(ResourceQuantityKind.Memory, resources.RequestsMemory, out var requestMemoryBytes))
            {
                hostConfig.MemoryReservation = requestMemoryBytes;
            }

            if (Require(ResourceQuantityKind.Cpu, resources.LimitsCpu, out var limitCpuMillis))
            {
                const long nanosPerMilli = NanoCpusPerCpu / 1000L;
                if (limitCpuMillis > long.MaxValue / nanosPerMilli)
                {
                    throw new WorkloadResourceRenderException(
                        $"The resolved CPU limit '{resources.LimitsCpu}' is too large to express as a docker CPU "
                        + "quota. Correct it on the workload's Resources panel.");
                }

                hostConfig.NanoCPUs = limitCpuMillis * nanosPerMilli;
            }
        }

        return new DockerResourceRendering
        {
            HostConfig = hostConfig,
            MetricsLabels = BuildMetricsLabels(spec),
        };
    }

    private static IReadOnlyDictionary<string, string> BuildMetricsLabels(WorkloadDesiredSpec spec)
    {
        var metrics = spec.Observability?.Metrics;
        if (metrics is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var port = metrics.Port.ToString(CultureInfo.InvariantCulture);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PrometheusScrapeKey] = "true",
            [PrometheusPortKey] = port,
            [PrometheusPathKey] = metrics.Path,

            [DockerProviderConstants.MetricsScrapeLabelKey] = "true",
            [DockerProviderConstants.MetricsPortLabelKey] = port,
            [DockerProviderConstants.MetricsPathLabelKey] = metrics.Path,
        };
    }

    private static bool Require(ResourceQuantityKind kind, string? value, out long units)
    {
        units = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!ResourceQuantityPolicy.TryParseForRender(kind, value, out units))
        {
            throw new WorkloadResourceRenderException(
                $"The resolved {kind} quantity '{value}' is not a valid Kubernetes quantity, so this workload cannot "
                + "be rendered. Correct it on the workload's Resources panel.");
        }

        return true;
    }
}
