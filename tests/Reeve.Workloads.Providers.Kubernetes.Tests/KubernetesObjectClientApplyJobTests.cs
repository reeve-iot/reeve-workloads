using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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

public sealed class KubernetesObjectClientApplyJobTests
{
    private const string Namespace = "reeve";
    private const string JobName = "reeve-wl-abcd1234-node-a-run1";
    private const string FieldManager = "reeve-workloads";

    private static KubernetesObjectClient NewClient() =>
        new(NullLogger<KubernetesObjectClient>.Instance);

    private static HttpOperationResponse<T> Ok<T>(T body) =>
        new() { Body = body, Response = new HttpResponseMessage(HttpStatusCode.OK) };

    private sealed record PatchRecorder(V1Patch Patch, string Name, string Namespace, string? FieldManager, bool? Force);

    private static (IKubernetes Client, List<PatchRecorder> JobPatches) ClientRecordingJobPatch()
    {
        var recorded = new List<PatchRecorder>();

        var batch = Substitute.For<IBatchV1Operations>();
        batch
            .PatchNamespacedJobWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                recorded.Add(new PatchRecorder(
                    ci.ArgAt<V1Patch>(0),
                    ci.ArgAt<string>(1),
                    ci.ArgAt<string>(2),
                    ci.ArgAt<string>(4),
                    ci.ArgAt<bool?>(6)));
                return Task.FromResult(Ok(new V1Job()));
            });

        var client = Substitute.For<IKubernetes>();
        client.BatchV1.Returns(batch);
        return (client, recorded);
    }

    [Fact]
    public async Task ServerSideApply_of_a_Job_routes_to_BatchV1_PatchNamespacedJob_and_returns_Ok()
    {
        var (client, jobPatches) = ClientRecordingJobPatch();
        const string manifest = "{\"apiVersion\":\"batch/v1\",\"kind\":\"Job\",\"metadata\":{\"name\":\"" + JobName + "\"}}";

        var outcome = await NewClient().ServerSideApplyAsync(
            client, Namespace, KubernetesObjectKind.Job, JobName, manifest, FieldManager, force: false, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue("SSA of a Job kind must succeed via BatchV1, not return an error outcome");
        outcome.IsConflict.Should().BeFalse("a clean apply against a reachable API server is not a conflict");
        outcome.Unreachable.Should().BeFalse("a clean apply against a reachable API server is not unreachable");
        outcome.Error.Should().BeNull(
            "the Job case must exist in the ServerSideApplyAsync switch — a fall-through would set Error to \"unknown object kind Job\"");
        jobPatches.Should().ContainSingle("the Job apply must be routed to client.BatchV1.PatchNamespacedJobAsync");

        var patch = jobPatches.Single();
        patch.Name.Should().Be(JobName);
        patch.Namespace.Should().Be(Namespace, "the SSA must target the namespace the provider passed through");
        patch.FieldManager.Should().Be(FieldManager, "the SSA must carry the reeve-workloads field manager");
        patch.Patch.Type.Should().Be(V1Patch.PatchType.ApplyPatch, "server-side apply uses an apply-patch");
        ((string)patch.Patch.Content).Should().Be(manifest, "the full object JSON is the apply-patch body");
    }

    [Fact]
    public async Task ServerSideApply_of_a_Job_forwards_the_force_flag_for_conflict_takeover()
    {
        var (client, jobPatches) = ClientRecordingJobPatch();

        await NewClient().ServerSideApplyAsync(
            client, Namespace, KubernetesObjectKind.Job, JobName, "{}", FieldManager, force: true, CancellationToken.None);

        jobPatches.Should().ContainSingle();
        var patch = jobPatches.Single();
        patch.Force.Should().Be(true, "the force flag must be forwarded to PatchNamespacedJobAsync");
        patch.Patch.Type.Should().Be(V1Patch.PatchType.ApplyPatch, "an SSA is an ApplyPatch of the full Job body");
    }

    [Fact]
    public async Task ServerSideApply_of_a_Job_never_returns_the_unknown_object_kind_error()
    {
        var (client, _) = ClientRecordingJobPatch();

        var outcome = await NewClient().ServerSideApplyAsync(
            client, Namespace, KubernetesObjectKind.Job, JobName, "{}", FieldManager, force: false, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        (outcome.Error ?? string.Empty).Should().NotContain("unknown object kind");
    }
}
