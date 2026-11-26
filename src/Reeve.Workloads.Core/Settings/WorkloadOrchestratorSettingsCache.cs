using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Core;

/// <summary>Holds each provider's auto-detect answer. In auto mode a read of the effective provider is a live
/// probe of every registered provider, and the engine reads it on every reconcile, probe and render.</summary>
/// <remarks>Dropped by <see cref="IWorkloadOrchestratorSettingsChanges.ChangedAsync"/>. An entry also expires
/// after <see cref="TimeToLive"/>, because the daemon can come and go without any settings change.</remarks>
public sealed class WorkloadOrchestratorSettingsCache
{
    /// <summary>How long an answer is served before the provider is probed again.</summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(15);

    private readonly IWorkloadClock clock;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> availability = new(StringComparer.Ordinal);

    private long generation;

    public WorkloadOrchestratorSettingsCache(IWorkloadClock clock)
    {
        this.clock = clock;
    }

    /// <summary>Whether <paramref name="provider"/> reported itself available under
    /// <paramref name="providerSettings"/>, probed when no live entry has it.</summary>
    /// <remarks>Keyed on the SETTINGS as well as the provider, not on the provider alone, because one call can
    /// ask twice with two different entries: a judge resolves the effective provider for the served document and
    /// for the proposed one, and a key that ignored the difference would answer the second question with the
    /// first one's result.
    /// <para>The key holds a HASH of the entry, never the entry: a kubeconfig reaches this method and must not end
    /// up as a dictionary key that outlives the read.</para></remarks>
    public async Task<bool> IsProviderAvailableAsync(
        WorkloadProviderId provider,
        JsonElement providerSettings,
        Func<CancellationToken, Task<bool>> probe,
        CancellationToken cancellationToken)
    {
        var key = $"{provider}|{Digest(providerSettings)}";
        long seen;
        lock (gate)
        {
            if (availability.TryGetValue(key, out var hit) && clock.UtcNow < hit.ExpiresAtUtc)
            {
                return hit.Value;
            }

            seen = generation;
        }

        var value = await probe(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (generation == seen)
            {
                availability[key] = new Entry(value, clock.UtcNow + TimeToLive);
            }
        }

        return value;
    }

    private static string Digest(JsonElement providerSettings)
    {
        var text = providerSettings.ValueKind is JsonValueKind.Undefined ? string.Empty : providerSettings.GetRawText();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Drops every answer, so the next read goes to the providers' own probes.</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            generation++;
            availability.Clear();
        }
    }

    private sealed record Entry(bool Value, DateTimeOffset ExpiresAtUtc);
}
