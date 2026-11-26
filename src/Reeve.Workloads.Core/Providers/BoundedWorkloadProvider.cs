using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Telemetry;

namespace Reeve.Workloads.Core.Providers;

internal sealed class BoundedWorkloadProvider : IWorkloadProvider
{
    private readonly IWorkloadProvider inner;
    private readonly WorkloadProviderTimeoutOptions options;
    private readonly ILogger logger;

    public BoundedWorkloadProvider(IWorkloadProvider inner, WorkloadProviderTimeoutOptions options, ILogger logger)
    {
        this.inner = inner;
        this.options = options;
        this.logger = logger;
    }

    public WorkloadProviderDescriptor Descriptor => inner.Descriptor;

    public Task<WorkloadApplyResult> ApplyAsync(
        WorkloadDesiredSpec spec,
        IProgress<WorkloadApplyProgress>? progress,
        CancellationToken cancellationToken = default,
        bool forceConflicts = false)
        => WithDeadlineAsync(
            nameof(ApplyAsync), options.MutationTimeout, cancellationToken,
            token => inner.ApplyAsync(spec, progress, token, forceConflicts),
            elapsed => WorkloadApplyResult.Failed(
                WorkloadApplyReason.ProviderUnreachable,
                $"The provider did not answer the apply within {elapsed}."));

    public async Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
    {
        var finished = await WithDeadlineAsync(
            nameof(TeardownAsync), options.MutationTimeout, cancellationToken,
            async token =>
            {
                await inner.TeardownAsync(reference, retainVolumes, token).ConfigureAwait(false);
                return true;
            },
            _ => false).ConfigureAwait(false);
        if (!finished)
        {
            throw new TimeoutException(
                $"The provider did not finish tearing down instance {reference.InstanceId} within {options.MutationTimeout}.");
        }
    }

    public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        => WithDeadlineAsync(
            nameof(SuspendAsync), options.CallTimeout, cancellationToken,
            token => inner.SuspendAsync(reference, token));

    public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        => WithDeadlineAsync(
            nameof(ResumeAsync), options.CallTimeout, cancellationToken,
            token => inner.ResumeAsync(reference, token));

    public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        => WithDeadlineAsync(
            nameof(GetStatusAsync), options.CallTimeout, cancellationToken,
            token => inner.GetStatusAsync(reference, token),
            elapsed => new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Unknown,
                ProviderReady = false,
                Message = $"The provider did not answer within {elapsed}.",
            });

    public Task<IReadOnlyDictionary<WorkloadRef, WorkloadStatus>> GetStatusesAsync(
        IReadOnlyList<WorkloadRef> references,
        CancellationToken cancellationToken = default)
        => WithDeadlineAsync(
            nameof(GetStatusesAsync), options.CallTimeout, cancellationToken,
            token => inner.GetStatusesAsync(references, token),
            elapsed => Unknown(references, $"The provider did not answer within {elapsed}."));

    private static IReadOnlyDictionary<WorkloadRef, WorkloadStatus> Unknown(
        IReadOnlyList<WorkloadRef> references, string message)
    {
        var statuses = new Dictionary<WorkloadRef, WorkloadStatus>(references.Count);
        foreach (var reference in references)
        {
            statuses[reference] = new WorkloadStatus
            {
                Phase = WorkloadProviderPhase.Unknown,
                ProviderReady = false,
                Message = message,
            };
        }

        return statuses;
    }

    public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
        => WithDeadlineAsync(
            nameof(PreflightAsync), options.CallTimeout, cancellationToken,
            token => inner.PreflightAsync(spec, token),
            elapsed => WorkloadPreflightResult.Ok(
            [
                PreflightCheck.Fail(
                    "provider-responsive",
                    PreflightSeverity.Error,
                    $"The provider did not answer the preflight within {elapsed}."),
            ]));

    public Task<WorkloadLogs> GetRecentLogsAsync(
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        CancellationToken cancellationToken = default)
        => WithDeadlineAsync(
            nameof(GetRecentLogsAsync), options.CallTimeout, cancellationToken,
            token => inner.GetRecentLogsAsync(reference, tailLines, previousContainer, token),
            _ => WorkloadLogs.Empty());

    public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
        IReadOnlyList<string> attachments,
        CancellationToken cancellationToken = default)
        => WithDeadlineAsync(
            nameof(GetAttachmentExistenceAsync), options.CallTimeout, cancellationToken,
            token => inner.GetAttachmentExistenceAsync(attachments, token),
            elapsed => WorkloadAttachmentExistence.Unreadable(
                $"The provider did not answer within {elapsed}."));

    private async Task<TResult> WithDeadlineAsync<TResult>(
        string operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<TResult>> call,
        Func<TimeSpan, TResult> onTimeout)
    {
        using var measure = Measure(operation);
        try
        {
            if (!options.Enabled)
            {
                return await call(cancellationToken).ConfigureAwait(false);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                return await call(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Expired(deadline, cancellationToken))
            {
                measure.Outcome = "timeout";
                LogTimeout(operation, timeout);
                return onTimeout(timeout);
            }
        }
        catch (OperationCanceledException)
        {
            measure.Outcome = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            measure.Failed(ex);
            throw;
        }
    }

    private async Task WithDeadlineAsync(
        string operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> call)
    {
        using var measure = Measure(operation);
        try
        {
            if (!options.Enabled)
            {
                await call(cancellationToken).ConfigureAwait(false);
                return;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                await call(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Expired(deadline, cancellationToken))
            {
                measure.Outcome = "timeout";
                LogTimeout(operation, timeout);
            }
        }
        catch (OperationCanceledException)
        {
            measure.Outcome = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            measure.Failed(ex);
            throw;
        }
    }

    private static bool Expired(CancellationTokenSource deadline, CancellationToken callerToken)
        => deadline.IsCancellationRequested && !callerToken.IsCancellationRequested;

    private Call Measure(string operation) => new(inner.Descriptor.Id, operation);

    private sealed class Call : IDisposable
    {
        private readonly WorkloadProviderId provider;
        private readonly string operation;
        private readonly long started;
        private readonly Activity? span;

        public Call(WorkloadProviderId provider, string operation)
        {
            this.provider = provider;
            this.operation = operation;
            started = Stopwatch.GetTimestamp();
            span = WorkloadsTelemetry.Source.StartActivity($"{WorkloadsTelemetry.ProviderSpan}.{operation}");
            span?.SetTag("workload.provider", provider.Value);
        }

        public string Outcome { private get; set; } = "ok";

        public void Failed(Exception ex)
        {
            Outcome = "error";
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }

        public void Dispose()
        {
            span?.SetTag("workload.outcome", Outcome);
            span?.Dispose();
            WorkloadsMetrics.ProviderCalled(provider, operation, Outcome, Stopwatch.GetElapsedTime(started));
        }
    }

    private void LogTimeout(string operation, TimeSpan timeout)
        => logger.LogWarning(
            "Provider {Provider} did not answer {Operation} within {Timeout}; treating it as unreachable for this pass.",
            inner.Descriptor.Id, operation, timeout);
}
