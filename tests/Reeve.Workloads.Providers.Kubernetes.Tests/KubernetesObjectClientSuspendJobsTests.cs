using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesObjectClientSuspendJobsTests
{
    private const string Namespace = "reeve";
    private const string ExpectedSelector = "reeve-iot.dev/workload-instance=11111111";

    private static readonly IReadOnlyDictionary<string, string> Selector =
        new Dictionary<string, string> { ["reeve-iot.dev/workload-instance"] = "11111111" };

    private static KubernetesObjectClient NewClient() =>
        new(NullLogger<KubernetesObjectClient>.Instance);

    private static HttpOperationResponse<T> Response<T>(T body) =>
        new() { Body = body, Response = new HttpResponseMessage(HttpStatusCode.OK) };

    private static HttpOperationException HttpError(HttpStatusCode status) =>
        new("api error")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(status), "{}"),
        };

    [Fact]
    public async Task Suspend_patches_an_in_flight_Job_with_spec_suspend_true()
    {
        var harness = new Harness(new[] { Job("node1", active: 1) });

        await NewClient().SetScanJobsSuspendedAsync(harness.Client, Namespace, Selector, suspended: true, CancellationToken.None);

        harness.Patches.Should().ContainSingle(p => p.Name == "node1");
        SuspendValue(harness.Patches.Single().Patch).Should().BeTrue();
        harness.ListSelector.Should().Be(ExpectedSelector, "the Job list must be scoped to the instance label");
    }

    [Fact]
    public async Task Suspend_patches_every_in_flight_Job_not_just_the_first()
    {
        var harness = new Harness(new[]
        {
            Job("node1", active: 1),
            Job("node2", active: 1),
            Job("node3", active: 1),
        });

        await NewClient().SetScanJobsSuspendedAsync(harness.Client, Namespace, Selector, suspended: true, CancellationToken.None);

        harness.Patches.Select(p => p.Name).Should().BeEquivalentTo(
            new[] { "node1", "node2", "node3" },
            "every in-flight node's Job must be reached, not just the first one the list returns");
    }

    [Fact]
    public async Task Suspend_leaves_a_terminal_Job_alone()
    {
        var harness = new Harness(new[]
        {
            Job("succeeded", succeeded: 1),
            Job("failed", failed: 1),
            Job("inflight", active: 1),
        });

        await NewClient().SetScanJobsSuspendedAsync(harness.Client, Namespace, Selector, suspended: true, CancellationToken.None);

        harness.Patches.Select(p => p.Name).Should().Equal(new[] { "inflight" },
            "only the non-terminal Job may be patched");
    }

    [Fact]
    public async Task Suspend_does_not_re_patch_a_Job_already_held()
    {
        var harness = new Harness(new[] { Job("already", active: 0, suspend: true) });

        await NewClient().SetScanJobsSuspendedAsync(harness.Client, Namespace, Selector, suspended: true, CancellationToken.None);

        harness.Patches.Should().BeEmpty("a Job already at the wanted value must not be patched again");
    }

    [Fact]
    public async Task Resume_clears_the_flag_only_where_it_is_set()
    {
        var harness = new Harness(new[]
        {
            Job("held", active: 0, suspend: true),
            Job("never-held", active: 1, suspend: false),
        });

        await NewClient().SetScanJobsSuspendedAsync(harness.Client, Namespace, Selector, suspended: false, CancellationToken.None);

        harness.Patches.Select(p => p.Name).Should().Equal("held");
        SuspendValue(harness.Patches.Single().Patch).Should().BeFalse();
    }

    [Fact]
    public async Task Resume_does_not_touch_a_terminal_Job_that_still_carries_the_flag()
    {
        var harness = new Harness(new[] { Job("done", succeeded: 1, suspend: true) });

        await NewClient().SetScanJobsSuspendedAsync(harness.Client, Namespace, Selector, suspended: false, CancellationToken.None);

        harness.Patches.Should().BeEmpty("a run that finished while held stays finished");
    }

    [Fact]
    public async Task A_Job_swept_between_the_list_and_the_patch_is_tolerated()
    {
        var harness = new Harness(new[] { Job("vanishing", active: 1) }, patchThrows: HttpError(HttpStatusCode.NotFound));

        var act = async () => await NewClient().SetScanJobsSuspendedAsync(
            harness.Client, Namespace, Selector, suspended: true, CancellationToken.None);

        await act.Should().NotThrowAsync("a 404 on the patch means the Job is already gone");
    }

    [Fact]
    public async Task A_non_404_fault_on_the_patch_propagates()
    {
        var harness = new Harness(new[] { Job("forbidden", active: 1) }, patchThrows: HttpError(HttpStatusCode.Forbidden));

        var act = async () => await NewClient().SetScanJobsSuspendedAsync(
            harness.Client, Namespace, Selector, suspended: true, CancellationToken.None);

        await act.Should().ThrowAsync<HttpOperationException>();
    }

    [Fact]
    public async Task No_matching_Jobs_issues_no_request()
    {
        var harness = new Harness(Array.Empty<V1Job>());

        await NewClient().SetScanJobsSuspendedAsync(harness.Client, Namespace, Selector, suspended: true, CancellationToken.None);

        harness.Patches.Should().BeEmpty();
    }

    private static bool SuspendValue(V1Patch patch)
    {
        var json = patch.Content as string;
        json.Should().NotBeNull("the merge patch must carry a JSON string body");
        using var document = JsonDocument.Parse(json!);
        return document.RootElement.GetProperty("spec").GetProperty("suspend").GetBoolean();
    }

    private static V1Job Job(string name, int? active = null, int? succeeded = null, int? failed = null, bool? suspend = null)
        => new()
        {
            Metadata = new V1ObjectMeta { Name = name, Labels = new Dictionary<string, string> { ["reeve-iot.dev/workload-instance"] = "11111111" } },
            Spec = suspend is null ? null : new V1JobSpec { Suspend = suspend, Template = new V1PodTemplateSpec() },
            Status = new V1JobStatus { Active = active, Succeeded = succeeded, Failed = failed },
        };

    private sealed class Harness
    {
        public IKubernetes Client { get; }
        public List<(string Name, V1Patch Patch)> Patches { get; } = new();
        public string? ListSelector { get; private set; }

        public Harness(IEnumerable<V1Job> seededJobs, Exception? patchThrows = null)
        {
            var jobs = seededJobs.ToList();
            var batch = Substitute.For<IBatchV1Operations>();

            batch.ListNamespacedJobWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    ListSelector = ci.ArgAt<string>(4);
                    return Task.FromResult(Response(new V1JobList { Items = jobs }));
                });

            batch.PatchNamespacedJobWithHttpMessagesAsync(
                    Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    if (patchThrows is not null)
                    {
                        throw patchThrows;
                    }

                    Patches.Add((ci.ArgAt<string>(1), ci.ArgAt<V1Patch>(0)));
                    return Task.FromResult(Response(new V1Job()));
                });

            Client = Substitute.For<IKubernetes>();
            Client.BatchV1.Returns(batch);
        }
    }
}
