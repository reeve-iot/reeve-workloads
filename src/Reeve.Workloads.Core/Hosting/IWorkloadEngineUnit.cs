namespace Reeve.Workloads.Core;

internal interface IWorkloadEngineUnit
{
    string Key { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
