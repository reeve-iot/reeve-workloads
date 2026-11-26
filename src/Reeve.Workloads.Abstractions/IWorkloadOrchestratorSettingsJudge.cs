using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions;

/// <summary>Judges a proposed settings document against live state: bound instances, retained volumes and the
/// document served now. It writes nothing; the host decides what to do with the verdict.</summary>
public interface IWorkloadOrchestratorSettingsJudge
{
    /// <summary>Whether <paramref name="proposed"/> may take effect, and when it may not, every rule it breaks.</summary>
    /// <param name="proposed">The plaintext document to judge. Run it through
    /// <see cref="IWorkloadOrchestratorSettingsRules.ResolveMasked"/> and
    /// <see cref="IWorkloadOrchestratorSettingsRules.Validate"/> first.</param>
    /// <param name="facts">What only the host knows, for the target rule.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <remarks>A proposal that changes the provider list carries its new <see cref="WorkloadOrchestratorSettings.ConfiguredProviders"/>;
    /// one that carries none keeps the served set. Reads the database through the instance and retained-volume repositories, so call it after the
    /// schema is migrated. In auto-detect mode it also probes the providers.</remarks>
    Task<WorkloadSettingsVerdict> JudgeAsync(
        WorkloadOrchestratorSettings proposed,
        WorkloadSettingsHostFacts facts,
        CancellationToken cancellationToken = default);
}

/// <summary>What the host knows about the deployment that the library cannot read from its own state.</summary>
public sealed record WorkloadSettingsHostFacts
{
    /// <summary>Providers the explicit target must not be, each with the reason the host gives: not configured by
    /// the other feature's active configuration, or unable to run on a clustered installation. A provider absent
    /// from the map is available.</summary>
    public IReadOnlyDictionary<WorkloadProviderId, string> UnavailableTargets { get; init; }
        = new Dictionary<WorkloadProviderId, string>();
}

/// <summary>The stable <see cref="WorkloadSettingsBlock.Code"/> values. A host maps them to its own response.</summary>
public static class WorkloadSettingsBlockCodes
{
    /// <summary>The effective provider would change while a non-terminating instance is bound to the provider
    /// currently in force. Instances bound to a third provider do not block, a document that keeps the effective
    /// provider never gets this block, and with no provider in force nothing blocks.</summary>
    public const string ProviderSwitchBlocked = "provider_switch_blocked";

    /// <summary>A provider entry that is in use would be dropped or relocated.</summary>
    public const string ProviderInUse = "provider_in_use";

    /// <summary>The target is one the host says it does not configure or cannot run.</summary>
    public const string TargetNotConfigured = "target_not_configured";
}

/// <summary>The judge's answer.</summary>
public sealed record WorkloadSettingsVerdict
{
    /// <summary>Every rule the document breaks; empty when it may take effect.</summary>
    public IReadOnlyList<WorkloadSettingsBlock> Blocks { get; init; } = [];

    /// <summary>True when nothing blocks the document.</summary>
    public bool IsApplicable => Blocks.Count == 0;

    /// <summary>The effective provider the proposed document resolves to: its explicit provider, else what
    /// auto-detect picks among its configured providers, or among the served document's when it carries none.
    /// Empty when none can be resolved. An empty id serialises as JSON null and does not deserialise back.</summary>
    public WorkloadProviderId RequestedProvider { get; init; }

    /// <summary>The effective provider of the document served now, the one the instances are expected to run on.
    /// Empty when the served document resolves to none (no compiled-in provider among the configured ones).
    /// An empty id serialises as JSON null and does not deserialise back.</summary>
    public WorkloadProviderId ActiveProvider { get; init; }
}

/// <summary>One rule a document breaks.</summary>
public sealed record WorkloadSettingsBlock
{
    /// <summary>A <see cref="WorkloadSettingsBlockCodes"/> value.</summary>
    public required string Code { get; init; }

    /// <summary>Operator-facing text naming the blocking instances and volumes.</summary>
    public required string Reason { get; init; }

    /// <summary>The provider the rule concerns.</summary>
    public required WorkloadProviderId Provider { get; init; }

    /// <summary>The instances that block, empty when none does.</summary>
    public IReadOnlyList<WorkloadProviderSwitchBlocker> Instances { get; init; } = [];

    /// <summary>The active retained volumes that block, empty when none does.</summary>
    public IReadOnlyList<WorkloadSettingsBlockingVolume> Volumes { get; init; } = [];
}

/// <summary>An active retained volume that holds a provider entry in place.</summary>
public sealed record WorkloadSettingsBlockingVolume
{
    /// <summary>The retained-volume record id.</summary>
    public required Guid Id { get; init; }

    /// <summary>The plugin type the volume was retained from.</summary>
    public required string GroupId { get; init; }

    /// <summary>The workload the volume belonged to.</summary>
    public required string WorkloadId { get; init; }

    /// <summary>The volume id within the workload.</summary>
    public required string VolumeId { get; init; }
}
