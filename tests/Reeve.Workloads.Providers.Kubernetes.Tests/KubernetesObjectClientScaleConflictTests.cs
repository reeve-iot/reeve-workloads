using System;
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

public sealed class KubernetesObjectClientScaleConflictTests
{
    private const string Namespace = "reeve";
    private const string DeploymentName = "reeve-wl-matter-11111111";

    private static KubernetesObjectClient NewClient() =>
        new(NullLogger<KubernetesObjectClient>.Instance);

    private static readonly IReadOnlyDictionary<string, string> Selector =
        new Dictionary<string, string> { ["reeve-iot.dev/workload-instance"] = "11111111" };

    private static HttpOperationResponse<T> Ok<T>(T body) =>
        new() { Body = body, Response = new HttpResponseMessage(HttpStatusCode.OK) };

    private static (IKubernetes Client, ScaleRecorder Recorder) ClientWithDeploymentAt(int currentReplicas)
    {
        var recorder = new ScaleRecorder();

        var deploymentList = new V1DeploymentList
        {
            Items = new List<V1Deployment>
            {
                new()
                {
                    Metadata = new V1ObjectMeta { Name = DeploymentName },
                    Spec = new V1DeploymentSpec { Replicas = currentReplicas },
                },
            },
        };

        var appsV1 = Substitute.For<IAppsV1Operations>();
        appsV1
            .ListNamespacedDeploymentWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok(deploymentList)));

        appsV1
            .PatchNamespacedDeploymentWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                recorder.ObjectPatch = ci.ArgAt<V1Patch>(0);
                recorder.ObjectPatchCount++;
                return Task.FromResult(Ok(new V1Deployment()));
            });

        appsV1
            .PatchNamespacedDeploymentScaleWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                recorder.ScaleSubresourcePatchCount++;
                return Task.FromResult(Ok(new V1Scale()));
            });

        var client = Substitute.For<IKubernetes>();
        client.AppsV1.Returns(appsV1);
        return (client, recorder);
    }

    [Fact]
    public async Task Scale_to_zero_when_already_at_zero_issues_no_patch()
    {
        var (client, recorder) = ClientWithDeploymentAt(currentReplicas: 0);

        await NewClient().ScaleDeploymentAsync(client, Namespace, Selector, replicas: 0, CancellationToken.None);

        recorder.ObjectPatchCount.Should().Be(0,
            "the deployment is already at 0 replicas — no redundant patch on a permanently-suspended instance");
        recorder.ScaleSubresourcePatchCount.Should().Be(0);
    }

    [Fact]
    public async Task Scale_to_zero_when_currently_running_patches_the_deployment_object_not_the_scale_subresource()
    {
        var (client, recorder) = ClientWithDeploymentAt(currentReplicas: 1);

        await NewClient().ScaleDeploymentAsync(client, Namespace, Selector, replicas: 0, CancellationToken.None);

        recorder.ScaleSubresourcePatchCount.Should().Be(0,
            "the RBAC-broken scale subresource must never be used (chart grants only `deployments patch`)");
        recorder.ObjectPatchCount.Should().Be(1,
            "a running deployment being suspended must merge-patch spec.replicas on the Deployment object itself");

        var body = (string)recorder.ObjectPatch!.Content;
        body.Should().Contain("\"replicas\":0", "the object merge-patch sets spec.replicas to the target count");
        body.Should().Contain("\"spec\"");
        recorder.ObjectPatch!.Type.Should().Be(V1Patch.PatchType.MergePatch,
            "a merge-patch of {\"spec\":{\"replicas\":N}} uses the granted `deployments patch` verb");
    }

    [Fact]
    public async Task Resume_patches_the_deployment_object_replicas_back_up()
    {
        var (client, recorder) = ClientWithDeploymentAt(currentReplicas: 0);

        await NewClient().ScaleDeploymentAsync(client, Namespace, Selector, replicas: 1, CancellationToken.None);

        recorder.ScaleSubresourcePatchCount.Should().Be(0);
        recorder.ObjectPatchCount.Should().Be(1);
        var body = (string)recorder.ObjectPatch!.Content;
        body.Should().Contain("\"replicas\":1");
    }

    private static IKubernetes ClientThatConflictsWith(string statusBody)
    {
        var ex = new HttpOperationException("conflict")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Conflict), statusBody),
        };

        var appsV1 = Substitute.For<IAppsV1Operations>();
        appsV1
            .PatchNamespacedDeploymentWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<V1Deployment>>>(_ => throw ex);

        var client = Substitute.For<IKubernetes>();
        client.AppsV1.Returns(appsV1);
        return client;
    }

    [Fact]
    public async Task Ssa_409_parses_the_manager_from_the_plural_conflicts_form()
    {
        var body = """
            {"kind":"Status","apiVersion":"v1","status":"Failure","message":"Apply failed with 2 conflicts: conflicts with \"kubectl-client-side-apply\" using apps/v1:\n- .spec.replicas\n- .spec.template.spec.containers[name=\"matter\"].image","reason":"Conflict","code":409}
            """;
        var client = ClientThatConflictsWith(body);

        var outcome = await NewClient().ServerSideApplyAsync(
            client, Namespace, KubernetesObjectKind.Deployment, DeploymentName, "{}", "reeve-workloads", force: false, CancellationToken.None);

        outcome.IsConflict.Should().BeTrue();
        outcome.ConflictingFieldManager.Should().Be("kubectl-client-side-apply",
            "the plural `conflicts with \"mgr\"` form must be parsed just like the singular form");
    }

    [Fact]
    public async Task Ssa_409_still_parses_the_manager_from_the_singular_conflict_form()
    {
        var body = """
            {"kind":"Status","apiVersion":"v1","status":"Failure","message":"Apply failed with 1 conflict: conflict with \"helm\" using apps/v1","reason":"Conflict","code":409}
            """;
        var client = ClientThatConflictsWith(body);

        var outcome = await NewClient().ServerSideApplyAsync(
            client, Namespace, KubernetesObjectKind.Deployment, DeploymentName, "{}", "reeve-workloads", force: false, CancellationToken.None);

        outcome.IsConflict.Should().BeTrue();
        outcome.ConflictingFieldManager.Should().Be("helm");
    }

    private sealed class ScaleRecorder
    {
        public int ObjectPatchCount { get; set; }
        public V1Patch? ObjectPatch { get; set; }
        public int ScaleSubresourcePatchCount { get; set; }
    }
}
