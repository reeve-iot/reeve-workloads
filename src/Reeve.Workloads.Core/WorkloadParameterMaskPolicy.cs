using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core.Network;

using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core;

/// <summary>The ONE statement of what <c>"***"</c> means on a workload's parameter map, for both the READ paths
/// that produce it and the WRITE paths that receive it back — independently written, the two once diverged as non-inverses and corrupted values in the gap (including blocking network-mode writes). See <see cref="UnmaskedNames"/> and <see cref="RetainMaskedValues"/>; fail-closed and pure.</summary>
public static class WorkloadParameterMaskPolicy
{
    /// <summary>The masked-value sentinel . Never a real value: it is neither read out nor written in.</summary>
    public const string MaskSentinel = "***";

    /// <summary>Names whose stored value may ride VERBATIM on a read: declared non-password parameters plus
    /// Reeve-synthesized reserved names, minus any reserved name declared a password (fail-closed). A <c>null</c> workload yields the EMPTY set — nothing provably non-secret, so the caller masks everything.</summary>
    public static IReadOnlySet<string> UnmaskedNames(WorkloadManifest? workload)
    {
        if (workload is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var declaredPasswords = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in workload.Parameters)
        {
            if (string.IsNullOrEmpty(parameter.Name))
            {
                continue;
            }

            if (parameter.Type == WorkloadParameterType.Password)
            {
                declaredPasswords.Add(parameter.Name);
            }
            else
            {
                names.Add(parameter.Name);
            }
        }

        foreach (var descriptor in ReservedParameterDescriptorProjector.Project(workload, EveryConditionalName))
        {
            if (!declaredPasswords.Contains(descriptor.Name))
            {
                names.Add(descriptor.Name);
            }
        }

        return names;
    }

    private static readonly WorkloadProviderDescriptor EveryConditionalName = new()
    {
        Id = WorkloadProviderId.Parse("any"),
        PublishesHostPorts = true,
        SupportsUserNamespaces = true,
    };

    /// <summary>Projects <paramref name="resolved"/> for a read surface: every non-empty value not in
    /// <paramref name="unmaskedNames"/> becomes <see cref="MaskSentinel"/>. Blank/absent values ride unchanged; an EMPTY set masks everything (fail-closed).</summary>
    public static IReadOnlyDictionary<string, string?> Mask(
        IReadOnlyDictionary<string, string?> resolved, IReadOnlySet<string> unmaskedNames)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(unmaskedNames);

        if (resolved.Count == 0)
        {
            return resolved;
        }

        var masked = new Dictionary<string, string?>(resolved.Count, StringComparer.Ordinal);
        foreach (var (key, value) in resolved)
        {
            masked[key] = !unmaskedNames.Contains(key) && !string.IsNullOrEmpty(value)
                && !value.StartsWith("ref:", StringComparison.Ordinal)
                ? MaskSentinel
                : value;
        }

        return masked;
    }

    /// <summary>The inverse of <see cref="Mask"/>: an <paramref name="incoming"/> sentinel takes the
    /// <paramref name="stored"/> value or is DROPPED if nothing was stored (never persisting the literal); every other value passes verbatim. Deliberately name-blind — the caller can't tell which values the read masked.</summary>
    public static IReadOnlyDictionary<string, string?> RetainMaskedValues(
        IReadOnlyDictionary<string, string?> stored, IReadOnlyDictionary<string, string?> incoming)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(incoming);

        var result = new Dictionary<string, string?>(incoming.Count, StringComparer.Ordinal);
        foreach (var (key, value) in incoming)
        {
            if (!string.Equals(value, MaskSentinel, StringComparison.Ordinal))
            {
                result[key] = value;
                continue;
            }

            if (stored.TryGetValue(key, out var prior) && !string.IsNullOrEmpty(prior))
            {
                result[key] = prior;
            }
        }

        return result;
    }
}
