using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;

namespace Acme.Workloads.Nomad;

public static class NomadProvider
{
    public static WorkloadProviderId Id { get; } = WorkloadProviderId.Parse("nomad");

    public static WorkloadProviderDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        DisplayName = "Nomad",
        LocationNoun = "Nomad cluster",
        PublishesHostPorts = true,
        SupportsCpuRequests = true,
        SchedulesAcrossNodes = true,
        RequiresRfc1123AttachmentNames = false,
    };
}

public sealed class NomadWorkloadProvider : IWorkloadProvider
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Job> jobs = new();

    private sealed class Job
    {
        public required WorkloadDesiredSpec Spec { get; set; }
        public int Submissions { get; set; }
        public bool Stopped { get; set; }
    }

    public WorkloadProviderDescriptor Descriptor => NomadProvider.Descriptor;

    public int SubmissionsFor(Guid instanceId)
    {
        lock (gate)
        {
            return jobs.TryGetValue(instanceId, out var job) ? job.Submissions : 0;
        }
    }

    public Task<WorkloadApplyResult> ApplyAsync(
        WorkloadDesiredSpec spec,
        IProgress<WorkloadApplyProgress>? progress,
        CancellationToken cancellationToken = default,
        bool forceConflicts = false)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (gate)
        {
            if (jobs.TryGetValue(spec.InstanceId, out var existing))
            {
                existing.Spec = spec;
                existing.Submissions++;
                existing.Stopped = false;
            }
            else
            {
                jobs[spec.InstanceId] = new Job { Spec = spec, Submissions = 1 };
            }
        }

        return Task.FromResult(WorkloadApplyResult.Applied(spec.SpecHash));
    }

    public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            jobs.Remove(reference.InstanceId);
        }

        return Task.CompletedTask;
    }

    public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        => SetStopped(reference.InstanceId, stopped: true);

    public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
        => SetStopped(reference.InstanceId, stopped: false);

    private Task SetStopped(Guid instanceId, bool stopped)
    {
        lock (gate)
        {
            if (jobs.TryGetValue(instanceId, out var job))
            {
                job.Stopped = stopped;
            }
        }

        return Task.CompletedTask;
    }

    public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(reference.InstanceId, out var job))
            {
                return Task.FromResult(WorkloadStatus.NotFound("no nomad job carries this instance label"));
            }

            return Task.FromResult(new WorkloadStatus
            {
                Phase = job.Stopped ? WorkloadProviderPhase.Pending : WorkloadProviderPhase.Ready,
                ProviderReady = !job.Stopped,
                ObservedImage = job.Spec.Image,
            });
        }
    }

    public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
        => Task.FromResult(WorkloadPreflightResult.Ok());

    public Task<WorkloadLogs> GetRecentLogsAsync(
        WorkloadRef reference,
        int tailLines,
        bool previousContainer,
        CancellationToken cancellationToken = default)
        => Task.FromResult(WorkloadLogs.Empty());

    public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
        IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
        => Task.FromResult(WorkloadAttachmentExistence.NotApplicable(
            "this provider does not look up live network objects"));
}
