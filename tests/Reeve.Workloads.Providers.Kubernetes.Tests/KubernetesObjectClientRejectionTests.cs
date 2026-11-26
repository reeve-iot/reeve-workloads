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

public sealed class KubernetesObjectClientRejectionTests
{
    private const string Namespace = "reeve";
    private const string DeploymentName = "reeve-wl-camera-go2rtc-camera-35f35cf2";
    private const string FieldManager = "reeve-workloads";

    private const string InvalidNodeSelectorStatus =
        """
        {"kind":"Status","apiVersion":"v1","status":"Failure",
         "message":"Deployment.apps \"reeve-wl\" is invalid: spec.template.spec.nodeSelector: Invalid value: \"'true'\": a valid label must be an empty string or consist of alphanumeric characters",
         "reason":"Invalid","code":422}
        """;

    private static KubernetesObjectClient NewClient() =>
        new(NullLogger<KubernetesObjectClient>.Instance);

    private static IKubernetes ClientAnswering(HttpStatusCode status, string body)
    {
        var appsV1 = Substitute.For<IAppsV1Operations>();
        appsV1
            .PatchNamespacedDeploymentWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<V1Deployment>>>(_ => throw new HttpOperationException($"{(int)status}")
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(status), body),
            });

        var client = Substitute.For<IKubernetes>();
        client.AppsV1.Returns(appsV1);
        return client;
    }

    private static Task<KubernetesApplyOutcome> ApplyAgainst(IKubernetes client) =>
        NewClient().ServerSideApplyAsync(
            client, Namespace, KubernetesObjectKind.Deployment, DeploymentName,
            "{\"apiVersion\":\"apps/v1\",\"kind\":\"Deployment\"}", FieldManager, force: false, CancellationToken.None);

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task An_answered_4xx_is_a_rejection_not_a_retryable_error(HttpStatusCode status)
    {
        var outcome = await ApplyAgainst(ClientAnswering(status, InvalidNodeSelectorStatus));

        outcome.Succeeded.Should().BeFalse();
        outcome.Rejected.Should().BeTrue(
            "the API server answered {0} — it refused these bytes, and the engine must surface that instead of re-applying them forever",
            (int)status);
        outcome.Unreachable.Should().BeFalse("the API server answered, so it was plainly reachable");
        outcome.IsConflict.Should().BeFalse("only a 409 managedFields answer is a field-manager conflict");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task An_answer_about_the_caller_or_the_cluster_stays_retryable(HttpStatusCode status)
    {
        var outcome = await ApplyAgainst(ClientAnswering(status, "{}"));

        outcome.Succeeded.Should().BeFalse();
        outcome.Rejected.Should().BeFalse(
            "{0} does not mean the spec was refused — reporting SpecRejected would blame the workload for a rotated token, a namespace Argo has not created yet, or the server's own bad day",
            (int)status);
        outcome.Unreachable.Should().BeFalse("the API server answered");
        outcome.IsConflict.Should().BeFalse();
        outcome.Error.Should().NotBeNullOrWhiteSpace("the answer must still reach the operator");
    }

    [Fact]
    public async Task A_rejection_carries_the_api_servers_own_message_to_the_operator()
    {
        var outcome = await ApplyAgainst(
            ClientAnswering(HttpStatusCode.UnprocessableEntity, InvalidNodeSelectorStatus));

        outcome.Error.Should().NotBeNullOrWhiteSpace();
        outcome.Error.Should().Contain("nodeSelector", "the operator has to be told WHICH field the API server refused");
        outcome.Error.Should().Contain("'true'", "the offending value is the whole diagnosis");
    }

    [Fact]
    public async Task A_409_is_still_a_field_manager_conflict_and_not_a_rejection()
    {
        const string conflictBody =
            """{"kind":"Status","message":"Apply failed with 1 conflict: conflict with \"kubectl\"","reason":"Conflict","code":409}""";

        var outcome = await ApplyAgainst(ClientAnswering(HttpStatusCode.Conflict, conflictBody));

        outcome.IsConflict.Should().BeTrue();
        outcome.Rejected.Should().BeFalse("a conflict is taken over with force, not reported as a refused spec");
    }
}
