using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesObjectClientDeleteJobsTests
{
    private const string Namespace = "reeve";
    private const string InstanceValue = "11111111";
    private const string ExpectedInstanceSelector = "reeve.io/workload-instance=11111111";

    private static readonly IReadOnlyDictionary<string, string> Selector =
        new Dictionary<string, string> { ["reeve.io/workload-instance"] = InstanceValue };

    private static KubernetesObjectClient NewClient() =>
        new(NullLogger<KubernetesObjectClient>.Instance);

    private static HttpOperationResponse<T> Response<T>(T body) =>
        new() { Body = body, Response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) };

    [Fact]
    public async Task DeleteWorkload_deletes_the_instances_Jobs_by_label_with_Background_propagation()
    {
        var harness = new Harness(seededJobs: new (string, string?)[] { ("reeve-wl-abcd1234-node1-run1", "run1") });

        await NewClient().DeleteWorkloadByLabelAsync(harness.Client, Namespace, Selector, retainPvc: false, CancellationToken.None);

        harness.JobDeletes.Should().ContainSingle(d => d.Name == "reeve-wl-abcd1234-node1-run1",
            "the batch/v1 Job matching the instance label must be deleted at teardown");
        harness.JobDeletes.Single().Propagation.Should().Be("Background",
            "the Job delete must reap its pods (zero pods after teardown) — NOT the default orphan propagation");
        harness.JobListSelector.Should().Be(ExpectedInstanceSelector,
            "the Job list must be scoped to the instance label selector");
    }

    [Fact]
    public async Task DeleteWorkload_deletes_every_object_type_including_Jobs_when_not_retaining()
    {
        var harness = new Harness(
            seededJobs: new (string, string?)[] { ("reeve-wl-abcd1234-node1-run1", "run1") },
            withDeployment: true, withDaemonSet: true, withService: true, withSecret: true, withPvc: true);

        await NewClient().DeleteWorkloadByLabelAsync(harness.Client, Namespace, Selector, retainPvc: false, CancellationToken.None);

        await harness.Apps.Received(1).DeleteNamespacedDeploymentWithHttpMessagesAsync(
            "dep", Namespace, Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<bool?>(),
            Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
        await harness.Apps.Received(1).DeleteNamespacedDaemonSetWithHttpMessagesAsync(
            "ds", Namespace, Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<bool?>(),
            Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
        await harness.Core.Received(1).DeleteNamespacedServiceWithHttpMessagesAsync(
            "svc", Namespace, Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<bool?>(),
            Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
        await harness.Core.Received(1).DeleteNamespacedSecretWithHttpMessagesAsync(
            "sec", Namespace, Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<bool?>(),
            Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
        await harness.Core.Received(1).DeleteNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
            "pvc", Namespace, Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<bool?>(),
            Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
        harness.JobDeletes.Should().ContainSingle(d => d.Name == "reeve-wl-abcd1234-node1-run1");
    }

    [Fact]
    public async Task DeleteWorkload_retains_PVC_but_still_deletes_Jobs_when_retaining()
    {
        var harness = new Harness(
            seededJobs: new (string, string?)[] { ("reeve-wl-abcd1234-node1-run1", "run1") },
            withPvc: true);

        await NewClient().DeleteWorkloadByLabelAsync(harness.Client, Namespace, Selector, retainPvc: true, CancellationToken.None);

        await harness.Core.DidNotReceive().DeleteNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(),
            Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
        harness.JobDeletes.Should().ContainSingle(d => d.Name == "reeve-wl-abcd1234-node1-run1",
            "Job retention is not gated by retainPvc — a scan instance's Jobs must always be reaped");
        harness.JobDeletes.Single().Propagation.Should().Be("Background");
    }

    [Fact]
    public async Task DeleteScanJobs_deletes_past_run_Jobs_with_Background_propagation()
    {
        var harness = new Harness(seededJobs: new (string, string?)[]
        {
            ("reeve-wl-abcd1234-node1-run1", "run1"),
            ("reeve-wl-abcd1234-node1-run2", "run2"),
        });

        await NewClient().DeleteScanJobsByLabelAsync(harness.Client, Namespace, Selector, exceptRunId: "run2", CancellationToken.None);

        harness.JobDeletes.Should().ContainSingle(d => d.Name == "reeve-wl-abcd1234-node1-run1",
            "only the PAST-run Job must be deleted");
        harness.JobDeletes.Single().Propagation.Should().Be("Background",
            "the past-run Job delete must reap its pods (Background propagation)");
        harness.JobListSelector.Should().Contain(
            $"{WorkloadResourceLabels.ScanRunLabelKey} notin (run2)",
            "the selector must exclude the current run via a set-based notin clause");
    }

    [Fact]
    public async Task DeleteScanJobs_does_not_delete_the_current_run_Jobs()
    {
        var harness = new Harness(seededJobs: new (string, string?)[]
        {
            ("reeve-wl-abcd1234-node1-run1", "run1"),
            ("reeve-wl-abcd1234-node1-run2", "run2"),
        });

        await NewClient().DeleteScanJobsByLabelAsync(harness.Client, Namespace, Selector, exceptRunId: "run2", CancellationToken.None);

        harness.JobDeletes.Should().NotContain(d => d.Name == "reeve-wl-abcd1234-node1-run2",
            "the current run's Job must NOT be deleted by the pre-run reconcile sweep");
    }

    [Fact]
    public async Task DeleteScanJobs_is_a_noop_when_no_Jobs_match()
    {
        var harness = new Harness(seededJobs: Array.Empty<(string, string?)>());

        await NewClient().DeleteScanJobsByLabelAsync(harness.Client, Namespace, Selector, exceptRunId: "run2", CancellationToken.None);

        harness.JobDeletes.Should().BeEmpty("an empty match is a no-op");
    }

    [Fact]
    public async Task DeleteScanJobs_with_null_exceptRunId_sweeps_all_instance_Jobs()
    {
        var harness = new Harness(seededJobs: new (string, string?)[]
        {
            ("reeve-wl-abcd1234-node1-run1", "run1"),
            ("reeve-wl-abcd1234-node1-run2", "run2"),
        });

        await NewClient().DeleteScanJobsByLabelAsync(harness.Client, Namespace, Selector, exceptRunId: null, CancellationToken.None);

        harness.JobDeletes.Select(d => d.Name).Should().BeEquivalentTo(
            new[] { "reeve-wl-abcd1234-node1-run1", "reeve-wl-abcd1234-node1-run2" });
        harness.JobDeletes.Should().OnlyContain(d => d.Propagation == "Background");
        harness.JobListSelector.Should().Be(ExpectedInstanceSelector, "a null exceptRunId adds no notin clause");
    }

    private sealed class Harness
    {
        public IKubernetes Client { get; }
        public IAppsV1Operations Apps { get; }
        public ICoreV1Operations Core { get; }
        public IBatchV1Operations Batch { get; }
        public List<(string Name, string? Propagation)> JobDeletes { get; } = new();
        public string? JobListSelector { get; private set; }

        public Harness(
            IEnumerable<(string Name, string? RunId)> seededJobs,
            bool withDeployment = false,
            bool withDaemonSet = false,
            bool withService = false,
            bool withSecret = false,
            bool withPvc = false)
        {
            var jobs = seededJobs.ToList();

            Apps = Substitute.For<IAppsV1Operations>();
            Core = Substitute.For<ICoreV1Operations>();
            Batch = Substitute.For<IBatchV1Operations>();

            Apps.ListNamespacedDeploymentWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1DeploymentList
                {
                    Items = withDeployment
                        ? new List<V1Deployment> { new() { Metadata = new V1ObjectMeta { Name = "dep" } } }
                        : new List<V1Deployment>(),
                })));

            Apps.ListNamespacedDaemonSetWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1DaemonSetList
                {
                    Items = withDaemonSet
                        ? new List<V1DaemonSet> { new() { Metadata = new V1ObjectMeta { Name = "ds" } } }
                        : new List<V1DaemonSet>(),
                })));

            Core.ListNamespacedServiceWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1ServiceList
                {
                    Items = withService
                        ? new List<V1Service> { new() { Metadata = new V1ObjectMeta { Name = "svc" } } }
                        : new List<V1Service>(),
                })));

            Core.ListNamespacedSecretWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1SecretList
                {
                    Items = withSecret
                        ? new List<V1Secret> { new() { Metadata = new V1ObjectMeta { Name = "sec" } } }
                        : new List<V1Secret>(),
                })));

            Core.ListNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1PersistentVolumeClaimList
                {
                    Items = withPvc
                        ? new List<V1PersistentVolumeClaim> { new() { Metadata = new V1ObjectMeta { Name = "pvc" } } }
                        : new List<V1PersistentVolumeClaim>(),
                })));

            Apps.DeleteNamespacedDeploymentWithHttpMessagesAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1Status())));
            Apps.DeleteNamespacedDaemonSetWithHttpMessagesAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1Status())));
            Core.DeleteNamespacedServiceWithHttpMessagesAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1Service())));
            Core.DeleteNamespacedSecretWithHttpMessagesAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1Status())));
            Core.DeleteNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Response(new V1PersistentVolumeClaim())));

            Batch.ListNamespacedJobWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var selector = ci.ArgAt<string>(4);
                    JobListSelector = selector;
                    var matched = jobs
                        .Where(j => SelectorMatchesRunId(selector, j.RunId))
                        .Select(j => MakeJob(j.Name, j.RunId))
                        .ToList();
                    return Task.FromResult(Response(new V1JobList { Items = matched }));
                });

            Batch.DeleteNamespacedJobWithHttpMessagesAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    JobDeletes.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(7)));
                    return Task.FromResult(Response(new V1Status()));
                });

            Client = Substitute.For<IKubernetes>();
            Client.AppsV1.Returns(Apps);
            Client.CoreV1.Returns(Core);
            Client.BatchV1.Returns(Batch);
        }

        private static V1Job MakeJob(string name, string? runId)
        {
            var labels = new Dictionary<string, string> { ["reeve.io/workload-instance"] = InstanceValue };
            if (runId is not null)
            {
                labels[WorkloadResourceLabels.ScanRunLabelKey] = runId;
            }

            return new V1Job { Metadata = new V1ObjectMeta { Name = name, Labels = labels } };
        }

        private static bool SelectorMatchesRunId(string? selector, string? runId)
        {
            if (string.IsNullOrEmpty(selector))
            {
                return true;
            }

            var match = Regex.Match(selector, WorkloadResourceLabels.ScanRunLabelKey + @"\s+notin\s+\(([^)]*)\)");
            if (!match.Success)
            {
                return true;
            }

            var excluded = match.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return runId is null || !excluded.Contains(runId);
        }
    }
}
