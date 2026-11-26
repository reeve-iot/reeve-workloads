using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core.Tests;

internal sealed class FakeWorkloadCredentialPort : IWorkloadCredentialPort
{
    public List<Guid> RevokedInstances { get; } = [];

    public bool Throws { get; set; }

    public Action<Guid>? OnRevoke { get; set; }

    public Task<string?> ResolveOrMintAsync(
        Guid instanceId, string credentialId, string? existingMaterial, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "these tests do not exercise the mint path; a call here is a behaviour change, not a fixture gap.");

    public Task RevokeAllAsync(
        Guid instanceId, Guid? actorUserId = null, CancellationToken cancellationToken = default)
    {
        if (Throws)
        {
            throw new InvalidOperationException("credential plane unavailable (test)");
        }

        OnRevoke?.Invoke(instanceId);
        RevokedInstances.Add(instanceId);
        return Task.CompletedTask;
    }
}
