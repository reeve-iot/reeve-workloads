using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Abstractions;

/// <summary>The bootstrap material only the host knows. The library composes <c>instanceId</c> and
/// <c>specHash</c>; every other <c>bootstrap:&lt;key&gt;</c> value, and any env the host forces, comes from here.</summary>
/// <remarks>Asked once per instance and per side, and the planner rebuilds every tick, so the host answers the
/// same request the same way — apart from secret material, which is outside the hash.
/// <para>A <c>WorkloadHostRefusedException</c> thrown while the library plans an update is definitive: it says
/// this instance will not be updated. For an update-cohort member still walking forward before the cohort's version
/// commit, and not past an irreversible stage, it vetoes the whole cohort, which rolls back. Anywhere else, and for
/// any other failure of the source, a planning failure holds the update and the next tick retries it.</para></remarks>
public interface IWorkloadHostBootstrapSource
{
    /// <summary>What this side of this instance should see beyond the library's own keys, or a refusal.</summary>
    Task<WorkloadHostBootstrapResult> GetAsync(
        WorkloadHostBootstrapRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Who the rendered spec is for. A host may answer them differently: what the library applies itself
/// and what an operator applies by hand can need different values for the same key.</summary>
public enum WorkloadBootstrapAudience
{
    /// <summary>A spec the library renders to apply, drift on or plan an update against.</summary>
    Runtime = 0,

    /// <summary>A spec rendered into artifacts an operator applies by hand.</summary>
    HandApplied = 1,
}

/// <summary>One side of one instance, as the library is about to render it. Everything a host needs to scope its
/// answer, so a source never has to read the manifest itself.</summary>
public sealed record WorkloadHostBootstrapRequest
{
    /// <summary>The instance the values are for.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>The provider the spec is rendered for.</summary>
    public required WorkloadProviderId Provider { get; init; }

    /// <summary>The workload's id on this side, for a refusal to name.</summary>
    public required string WorkloadName { get; init; }

    /// <summary>This side's extension namespaces, which the host reads its own settings from.</summary>
    public required WorkloadExtensions Extensions { get; init; }

    /// <summary>This side's health mode.</summary>
    public required WorkloadHealthMode HealthMode { get; init; }

    /// <summary>The <c>bootstrap:&lt;key&gt;</c> keys this side's env references, so a host can refuse only what
    /// a workload actually asks for.</summary>
    public required IReadOnlySet<string> DeclaredKeys { get; init; }

    /// <summary>The per-instance secret a <see cref="BootstrapValue.SecretRef"/> points into, named by the
    /// effective provider on every audience.</summary>
    public required string SecretName { get; init; }

    /// <summary>Who the rendered spec is for.</summary>
    public required WorkloadBootstrapAudience Audience { get; init; }
}

/// <summary>The host's answer: values for its own keys, env it forces, credentials it provisions outside the
/// env — or a refusal, which renders nothing.</summary>
public sealed record WorkloadHostBootstrapResult
{
    /// <summary>Nothing of the host's own. The library's two keys still render.</summary>
    public static WorkloadHostBootstrapResult Empty { get; } = new();

    /// <summary>A refusal to render this side at all, with the reason the library reports. A refusal must say
    /// why, so a null or blank reason throws.</summary>
    public static WorkloadHostBootstrapResult Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new() { Refusal = reason };
    }

    /// <summary>A value per <c>bootstrap:&lt;key&gt;</c>, keyed by the key. A key the manifest references and this
    /// map lacks renders a null literal. The library's own keys may not appear here.</summary>
    public IReadOnlyDictionary<string, BootstrapValue> Values { get; init; } =
        new Dictionary<string, BootstrapValue>(StringComparer.Ordinal);

    /// <summary>Env forced over any the author declared under the same name, hashed like every other env unless it is
    /// flagged <see cref="ResolvedEnvVar.ExcludedFromSpecHash"/>.</summary>
    public IReadOnlyList<ResolvedEnvVar> HostEnv { get; init; } = [];

    /// <summary>Credentials the provider writes at apply whether or not an env declares them. Outside the hash.</summary>
    public IReadOnlyList<ProvisionedCredential> ProvisionedCredentials { get; init; } = [];

    /// <summary>Why the host will not have this side rendered, or null when it answered. Set only through
    /// <see cref="Refused"/>, so a refusal always says why.</summary>
    public string? Refusal { get; private init; }
}

/// <summary>The default: the host contributes nothing, and a workload renders only the library's own keys.
/// Correct for a host with nothing to add; a key such a host's manifests reference renders a null literal.</summary>
public sealed class EmptyWorkloadHostBootstrapSource : IWorkloadHostBootstrapSource
{
    /// <inheritdoc />
    public Task<WorkloadHostBootstrapResult> GetAsync(
        WorkloadHostBootstrapRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(WorkloadHostBootstrapResult.Empty);
}
