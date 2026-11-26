using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadApplyProgressReporterTests
{
    private static WorkloadApplyProgress Progress(int? percent, string message) =>
        new() { Percent = percent, Message = message };

    [Fact]
    public async Task Reported_progress_is_persisted_via_the_delegate_and_drained()
    {
        var persisted = new List<WorkloadApplyProgress>();
        await using var reporter = new WorkloadApplyProgressReporter(
            (progress, _) =>
            {
                persisted.Add(progress);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            CancellationToken.None);

        ((IProgress<WorkloadApplyProgress>)reporter).Report(Progress(42, "Pulling image… 42% (12/26 MB)"));
        await reporter.DrainAsync();

        persisted.Should().ContainSingle();
        persisted[0].Percent.Should().Be(42);
        persisted[0].Message.Should().Be("Pulling image… 42% (12/26 MB)");
    }

    [Fact]
    public async Task A_persist_fault_is_swallowed_so_the_deploy_never_fails()
    {
        await using var reporter = new WorkloadApplyProgressReporter(
            (_, _) => throw new InvalidOperationException("db down"),
            NullLogger.Instance,
            CancellationToken.None);

        ((IProgress<WorkloadApplyProgress>)reporter).Report(Progress(1, "Pulling image… 1%"));

        await reporter.Invoking(r => r.DrainAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Drain_is_idempotent()
    {
        await using var reporter = new WorkloadApplyProgressReporter(
            (_, _) => Task.CompletedTask,
            NullLogger.Instance,
            CancellationToken.None);

        await reporter.DrainAsync();
        await reporter.Invoking(r => r.DrainAsync()).Should().NotThrowAsync();
    }
}
