using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadRepairsUnit : IWorkloadEngineUnit
{
    public const string Key = "workload-repairs";

    private readonly WorkloadRepairsScanner scanner;
    private readonly TimeSpan interval;
    private readonly ILogger<WorkloadRepairsUnit> logger;

    private CancellationTokenSource? cts;
    private Task? loop;

    public WorkloadRepairsUnit(
        WorkloadRepairsScanner scanner,
        IOptions<WorkloadRepairsOptions> options,
        ILogger<WorkloadRepairsUnit> logger)
    {
        this.scanner = scanner;
        this.interval = options.Value.ScanCadence;
        this.logger = logger;
    }

    string IWorkloadEngineUnit.Key => Key;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (loop is not null)
        {
            return Task.CompletedTask;
        }

        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;
        loop = Task.Run(() => RunAsync(token), token);

        logger.LogInformation("Workload Repairs escalation scanner started (cadence {Interval}).", interval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        cts.Dispose();
        cts = null;
        loop = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        await ScanOnceAsync(token).ConfigureAwait(false);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await ScanOnceAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task ScanOnceAsync(CancellationToken token)
    {
        try
        {
            await scanner.ScanAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workload Repairs escalation pass failed.");
        }
    }
}
