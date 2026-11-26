using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Tests.Shared;

internal sealed class RecordingCredentialPort : IWorkloadCredentialPort
{
    public HashSet<string> Live { get; } = new(StringComparer.Ordinal);

    public List<(Guid InstanceId, string CredentialId, string? Existing)> Asks { get; } = [];

    public List<string> Minted { get; } = [];

    public int RevokeAllCount { get; private set; }

    public bool AllowRevoke { get; init; }

    public Func<Guid, string, string?>? Answer { get; set; }

    public Task<string?> ResolveOrMintAsync(
        Guid instanceId, string credentialId, string? existingMaterial, CancellationToken cancellationToken = default)
    {
        Asks.Add((instanceId, credentialId, existingMaterial));
        if (Answer is { } answer)
        {
            return Task.FromResult(answer(instanceId, credentialId));
        }

        if (!string.IsNullOrEmpty(existingMaterial) && Live.Contains(existingMaterial))
        {
            return Task.FromResult<string?>(existingMaterial);
        }

        var minted = $"minted-{credentialId}-{Minted.Count + 1}";
        Minted.Add(minted);
        Live.Add(minted);
        return Task.FromResult<string?>(minted);
    }

    public Task RevokeAllAsync(Guid instanceId, Guid? actorUserId = null, CancellationToken cancellationToken = default)
    {
        RevokeAllCount++;
        return AllowRevoke
            ? Task.CompletedTask
            : throw new NotSupportedException("Nothing a provider or a renderer does revokes a credential.");
    }
}
