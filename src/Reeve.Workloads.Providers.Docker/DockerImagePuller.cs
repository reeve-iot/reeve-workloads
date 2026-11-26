using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Providers.Docker;

internal sealed class DockerImagePuller : IDockerImagePuller
{
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromSeconds(1);

    public async Task<DockerOperationResult> PullAsync(
        IDockerClient client,
        string imageReference,
        DockerProviderSettings settings,
        IProgress<DockerPullProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(settings);

        DockerImageReference parsed;
        try
        {
            parsed = DockerRegistryAuth.ParseReference(imageReference);
        }
        catch (ArgumentException ex)
        {
            return DockerOperationResult.Fail(
                DockerFailureKind.Rejected,
                $"The workload image reference is invalid: {ex.Message}");
        }

        var resolution = DockerRegistryAuth.Resolve(imageReference, settings);
        if (resolution.Rejected)
        {
            return DockerOperationResult.Fail(DockerFailureKind.Rejected, resolution.RejectionMessage);
        }

        var parameters = new ImagesCreateParameters
        {
            FromImage = parsed.FromImage,
            Tag = parsed.Tag,
        };

        var capture = new PullProgressCapture(progress, ProgressThrottle);

        try
        {
            await client.Images
                .CreateImageAsync(parameters, resolution.Auth, capture, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DockerImageNotFoundException)
        {
            return NotFound(parsed);
        }
        catch (DockerApiException ex)
        {
            return ClassifyApiException(ex, parsed);
        }
        catch (Exception ex) when (IsConnectivityFailure(ex))
        {
            return Unreachable(parsed, Summarize(ex.Message));
        }

        if (capture.ErrorMessage is { Length: > 0 } streamedError)
        {
            var kind = ClassifyText(streamedError) ?? DockerFailureKind.Error;
            return DockerOperationResult.Fail(kind, ComposeFailureMessage(kind, parsed, streamedError));
        }

        return DockerOperationResult.Ok($"Pulled image '{parsed.Raw}'.");
    }

    private static DockerOperationResult ClassifyApiException(DockerApiException ex, DockerImageReference image)
    {
        var kind = ClassifyStatus(ex.StatusCode)
            ?? ClassifyText(ex.ResponseBody)
            ?? ClassifyText(ex.Message)
            ?? DockerFailureKind.Error;

        var detail = Summarize(FirstNonEmpty(ex.ResponseBody, ex.Message));
        return DockerOperationResult.Fail(kind, ComposeFailureMessage(kind, image, detail));
    }

    private static DockerFailureKind? ClassifyStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => DockerFailureKind.Rejected,
        HttpStatusCode.Unauthorized => DockerFailureKind.Rejected,
        HttpStatusCode.Forbidden => DockerFailureKind.Rejected,
        HttpStatusCode.BadRequest => DockerFailureKind.Rejected,
        HttpStatusCode.BadGateway => DockerFailureKind.Unreachable,
        HttpStatusCode.ServiceUnavailable => DockerFailureKind.Unreachable,
        HttpStatusCode.GatewayTimeout => DockerFailureKind.Unreachable,
        HttpStatusCode.RequestTimeout => DockerFailureKind.Unreachable,
        _ => null,
    };

    private static DockerFailureKind? ClassifyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.ToLowerInvariant();

        if (Contains(t, "not found", "manifest unknown", "manifest for", "no such image", "does not exist",
                "pull access denied", "denied", "unauthorized", "authentication required", "forbidden",
                "requires 'docker login'", "requested access to the resource is denied", "insufficient_scope",
                "repository name not known", "invalid reference"))
        {
            return DockerFailureKind.Rejected;
        }

        if (Contains(t, "timeout", "timed out", "connection refused", "connection reset", "no such host",
                "dial tcp", "network is unreachable", "temporary failure in name resolution", "i/o timeout",
                "no route to host", "tls handshake", "server misbehaving", "eof", "cannot connect to the docker daemon"))
        {
            return DockerFailureKind.Unreachable;
        }

        return null;
    }

    private static bool IsConnectivityFailure(Exception ex) => ex switch
    {
        HttpRequestException => true,
        SocketException => true,
        IOException => true,
        TimeoutException => true,
        OperationCanceledException => true,
        _ => ex.InnerException is not null && IsConnectivityFailure(ex.InnerException),
    };

    private static DockerOperationResult NotFound(DockerImageReference image) =>
        DockerOperationResult.Fail(
            DockerFailureKind.Rejected,
            $"Image '{image.Raw}' was not found in registry '{image.RegistryHost}'.");

    private static DockerOperationResult Unreachable(DockerImageReference image, string? detail) =>
        DockerOperationResult.Fail(
            DockerFailureKind.Unreachable,
            $"The docker daemon could not reach registry '{image.RegistryHost}' to pull '{image.Raw}'"
                + (string.IsNullOrEmpty(detail) ? "." : $": {detail}"));

    private static string ComposeFailureMessage(DockerFailureKind kind, DockerImageReference image, string? detail)
    {
        var reason = string.IsNullOrEmpty(detail) ? null : $": {detail}";
        return kind switch
        {
            DockerFailureKind.Rejected =>
                $"The registry rejected the pull of image '{image.Raw}' (host '{image.RegistryHost}'){reason}",
            DockerFailureKind.Unreachable =>
                $"The docker daemon could not reach registry '{image.RegistryHost}' to pull '{image.Raw}'{reason}",
            _ =>
                $"Pulling image '{image.Raw}' failed{reason}",
        };
    }

    private static bool Contains(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? Summarize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var single = text.Trim().ReplaceLineEndings(" ");
        const int cap = 300;
        return single.Length <= cap ? single : single[..cap] + "…";
    }

    private sealed class PullProgressCapture(IProgress<DockerPullProgress>? outward, TimeSpan throttle)
        : IProgress<JSONMessage>
    {
        private readonly IProgress<DockerPullProgress>? outward = outward;
        private readonly TimeSpan throttle = throttle;
        private readonly DockerPullProgressAggregator aggregator = new();
        private readonly System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        private TimeSpan lastReport = TimeSpan.FromDays(-1);

        public string? ErrorMessage { get; private set; }

        public void Report(JSONMessage value)
        {
            if (value is null)
            {
                return;
            }

            var message = value.Error?.Message;
            if (!string.IsNullOrWhiteSpace(message))
            {
                ErrorMessage = message;
                return;
            }

            if (outward is null)
            {
                return;
            }

            aggregator.Observe(value);

            var elapsed = stopwatch.Elapsed;
            if (elapsed - lastReport < throttle)
            {
                return;
            }

            lastReport = elapsed;
            outward.Report(aggregator.Snapshot());
        }
    }
}
