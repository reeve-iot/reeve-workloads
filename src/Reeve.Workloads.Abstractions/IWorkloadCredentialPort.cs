namespace Reeve.Workloads.Abstractions;

/// <summary>The credentials a workload authenticates with — the storage leaves, this stays. Required, never a
/// nullable parameter: with the shipped default (<see cref="NoCredentialPort"/>), a revocation mints and revokes
/// nothing, and the workload keeps whatever credential it already has.</summary>
public interface IWorkloadCredentialPort
{
    /// <summary>Returns the credential <paramref name="credentialId"/> names to deploy with. The id is the host's
    /// own, from its bootstrap answer. <paramref name="existingMaterial"/> is what was read back out of the live
    /// runtime: still valid, it is returned unchanged, so a benign re-apply never rotates. Otherwise a host that
    /// issues credentials mints a new one BESIDE the instance's live ones and never revokes them — an artifact
    /// reveal mints for each workload it renders, and must not cut off the ones running. Material must be
    /// high-entropy: providers stamp an unkeyed digest of it on the runtime, readable without access to the
    /// secret itself.</summary>
    Task<string?> ResolveOrMintAsync(
        Guid instanceId, string credentialId, string? existingMaterial, CancellationToken cancellationToken = default);

    /// <summary>Invalidates every credential the instance holds. Idempotent, and the load-bearing half of a
    /// host suspend or a teardown — a caller treats a fault as "retry", never as "done". Called on every
    /// reconcile pass over a host-held instance.</summary>
    Task RevokeAllAsync(Guid instanceId, Guid? actorUserId = null, CancellationToken cancellationToken = default);
}

/// <summary>The shipped no-op for a host whose workloads carry no credential: it mints and revokes nothing,
/// which is only safe BECAUSE nothing was issued. Registering it is a decision, not an omission.</summary>
public sealed class NoCredentialPort : IWorkloadCredentialPort
{
    /// <inheritdoc />
    public Task<string?> ResolveOrMintAsync(
        Guid instanceId, string credentialId, string? existingMaterial, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task RevokeAllAsync(Guid instanceId, Guid? actorUserId = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
