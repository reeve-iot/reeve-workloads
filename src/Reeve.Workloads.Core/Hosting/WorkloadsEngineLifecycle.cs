using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadsEngineLifecycle : IHostedService, IDisposable
{
    private readonly WorkloadsEngineStartSignal signal;
    private readonly WorkloadsEngineRunner runner;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<WorkloadsEngineLifecycle> logger;

    private CancellationTokenRegistration registration;

    public WorkloadsEngineLifecycle(
        WorkloadsEngineStartSignal signal,
        WorkloadsEngineRunner runner,
        IHostApplicationLifetime lifetime,
        ILogger<WorkloadsEngineLifecycle> logger)
    {
        this.signal = signal;
        this.runner = runner;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registration = lifetime.ApplicationStarted.Register(CheckSomethingRanTheEngine);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        registration.Dispose();
        registration = default;

        await runner.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => registration.Dispose();

    private void CheckSomethingRanTheEngine()
    {
        if (signal.WasStarted)
        {
            return;
        }

        logger.LogCritical(
            "The Reeve workloads engine was composed but never started: nothing called RunReeveWorkloadsAsync. "
            + "No workload will be reconciled, deployed, updated or repaired while this process runs, so it is "
            + "being stopped rather than left to look healthy. Call RunReeveWorkloadsAsync on the built service "
            + "provider during startup.");

        lifetime.StopApplication();
    }
}
