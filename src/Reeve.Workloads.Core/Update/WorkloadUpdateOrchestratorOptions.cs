using Reeve.Workloads.Abstractions.Update;

namespace Reeve.Workloads.Core.Update;

/// <summary>Timing knobs for the <see cref="WorkloadUpdateOrchestrator"/>. A plain record rather than IOptions so
/// the pure orchestrator stays trivially fake-testable. Bound from <see cref="SectionName"/> unless the host registers
/// its own instance before composing.</summary>
public sealed record WorkloadUpdateOrchestratorOptions
{
    /// <summary>The configuration section the composition binds these values from.</summary>
    public const string SectionName = "Workloads:UpdateOrchestrator";

    /// <summary>The pause-excluded HealthGate timeout budget: running time after the workload started, with image
    /// pulls and paused spans (provider or host evidence unavailable) left out.</summary>
    public TimeSpan HealthGateTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The health-gate stage's absolute cap, measured from the stage's entry with pulls and paused spans
    /// included. It only bounds a gate whose clock never runs out; <see cref="HealthGateTimeout"/> decides inside it.
    /// A value below <see cref="HealthGateTimeout"/> is raised to it.</summary>
    public TimeSpan HealthGateMaximumWait { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The sidecar stage's budget, both ways: how long an apply may make no progress going forward, and how
    /// long the previous version may take to come back healthy on the way down. One stage, one budget.</summary>
    public TimeSpan RollbackTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest the health-gate stage may run: <see cref="HealthGateMaximumWait"/>, or
    /// <see cref="HealthGateTimeout"/> when that is longer. A host that bounds a whole update phase allows at least
    /// this much for the gate.</summary>
    public TimeSpan HealthGateStageBudget =>
        HealthGateMaximumWait > HealthGateTimeout ? HealthGateMaximumWait : HealthGateTimeout;
}
