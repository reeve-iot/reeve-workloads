using Reeve.Workloads.Abstractions;

namespace Reeve.Workloads.Core.Tests;

internal sealed class MintingCredentialPort : IWorkloadCredentialPort
{
    private readonly Dictionary<Guid, List<string>> live = [];

    public bool RevokeFails { get; set; }

    public List<string> Calls { get; } = [];

    public IReadOnlyList<string> LiveTokens(Guid instanceId)
        => live.TryGetValue(instanceId, out var tokens) ? tokens : [];

    public Task<string?> ResolveOrMintAsync(
        Guid instanceId, string credentialId, string? existingMaterial, CancellationToken cancellationToken = default)
    {
        if (existingMaterial is not null && LiveTokens(instanceId).Contains(existingMaterial))
        {
            return Task.FromResult<string?>(existingMaterial);
        }

        var token = $"token-{Guid.NewGuid():N}";
        if (!live.TryGetValue(instanceId, out var tokens))
        {
            live[instanceId] = tokens = [];
        }

        tokens.Add(token);
        Calls.Add($"mint:{token}");
        return Task.FromResult<string?>(token);
    }

    public Task RevokeAllAsync(Guid instanceId, Guid? actorUserId = null, CancellationToken cancellationToken = default)
    {
        if (RevokeFails)
        {
            throw new InvalidOperationException("credential store unavailable (test)");
        }

        live.Remove(instanceId);
        Calls.Add("revoke");
        return Task.CompletedTask;
    }
}
