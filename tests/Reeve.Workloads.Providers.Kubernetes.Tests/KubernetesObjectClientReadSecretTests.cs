using System;
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

public sealed class KubernetesObjectClientReadSecretTests
{
    private const string Namespace = "reeve-workloads";
    private const string SecretName = "reeve-secret";
    private static readonly string[] Keys = ["A_TOKEN", "B_TOKEN"];

    private static KubernetesObjectClient NewClient() =>
        new(NullLogger<KubernetesObjectClient>.Instance);

    private static HttpOperationException HttpError(HttpStatusCode status) =>
        new("api error")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(status), "{}"),
        };

    private static IKubernetes ClientThatThrows(Exception toThrow)
    {
        var coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1
            .ReadNamespacedSecretWithHttpMessagesAsync(
                SecretName, Namespace, Arg.Any<bool?>(), Arg.Any<System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns<Task<HttpOperationResponse<V1Secret>>>(_ => throw toThrow);

        var client = Substitute.For<IKubernetes>();
        client.CoreV1.Returns(coreV1);
        return client;
    }

    [Fact]
    public async Task ReadSecretValues_returns_null_for_a_genuine_404()
    {
        var client = ClientThatThrows(HttpError(HttpStatusCode.NotFound));

        var value = await NewClient().ReadSecretValuesAsync(client, Namespace, SecretName, Keys, CancellationToken.None);

        value.Should().BeEmpty("a genuine 404 means the Secret does not exist yet — the provider mints a fresh credential");
    }

    [Fact]
    public async Task ReadSecretValues_reads_the_secret_once_and_returns_only_the_keys_it_holds()
    {
        var coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1
            .ReadNamespacedSecretWithHttpMessagesAsync(
                SecretName, Namespace, Arg.Any<bool?>(), Arg.Any<System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(new HttpOperationResponse<V1Secret>
            {
                Body = new V1Secret
                {
                    Data = new System.Collections.Generic.Dictionary<string, byte[]>
                    {
                        ["A_TOKEN"] = System.Text.Encoding.UTF8.GetBytes("a-value"),
                        ["OTHER"] = System.Text.Encoding.UTF8.GetBytes("not-asked"),
                    },
                },
            });
        var client = Substitute.For<IKubernetes>();
        client.CoreV1.Returns(coreV1);

        var values = await NewClient().ReadSecretValuesAsync(client, Namespace, SecretName, Keys, CancellationToken.None);

        values.Should().Equal(new System.Collections.Generic.Dictionary<string, string> { ["A_TOKEN"] = "a-value" });
        await coreV1.Received(1).ReadNamespacedSecretWithHttpMessagesAsync(
            SecretName, Namespace, Arg.Any<bool?>(), Arg.Any<System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadSecretValues_THROWS_on_a_transport_error_rather_than_reading_as_absent()
    {
        var client = ClientThatThrows(new HttpRequestException("connection refused"));

        var act = async () => await NewClient().ReadSecretValuesAsync(client, Namespace, SecretName, Keys, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>(
            "a transport error must surface (not read as absent) so a transient blip never mints a duplicate active token");
    }

    [Fact]
    public async Task ReadSecretValues_THROWS_on_a_non_404_http_error_rather_than_reading_as_absent()
    {
        var client = ClientThatThrows(HttpError(HttpStatusCode.InternalServerError));

        var act = async () => await NewClient().ReadSecretValuesAsync(client, Namespace, SecretName, Keys, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>(
            "a non-404 HTTP error must surface rather than read as absent (never gratuitously mint over a live Secret)");
    }

    [Fact]
    public async Task ReadSecretValues_still_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = ClientThatThrows(new OperationCanceledException(cts.Token));

        var act = async () => await NewClient().ReadSecretValuesAsync(client, Namespace, SecretName, Keys, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
