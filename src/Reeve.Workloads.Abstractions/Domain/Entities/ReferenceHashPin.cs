namespace Reeve.Workloads.Abstractions.Domain.Entities;

internal sealed record ReferenceHashPin(string Binding, bool IsVersionPinned);

internal static class ReferenceHashPinRules
{
    internal const string HashPinnedBindingsJsonName = "hashPinnedBindings";

    internal enum Applicability { Invalid, Unpinned, VersionPinned }

    internal static Applicability Evaluate(ReferenceHashPin? marker, string? storedBinding, string? candidateBinding)
    {
        if (marker is null || string.IsNullOrWhiteSpace(marker.Binding)
            || !string.Equals(marker.Binding, storedBinding, StringComparison.Ordinal)
            || !string.Equals(marker.Binding, candidateBinding, StringComparison.Ordinal))
            return Applicability.Invalid;
        return marker.IsVersionPinned ? Applicability.VersionPinned : Applicability.Unpinned;
    }

    internal static bool RolloutKeyRequired(IReadOnlyDictionary<string, string?> references, IEnumerable<string> passwordNames)
    {
        var names = passwordNames as ISet<string> ?? passwordNames.ToHashSet(StringComparer.Ordinal);
        return references.Any(pair => pair.Value is not null && names.Contains(pair.Key));
    }
}
