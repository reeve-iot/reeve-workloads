namespace Reeve.Workloads.Core;

internal static class WorkloadParameterAcceptList
{
    public static void ThrowIfUndeclared(
        string workloadId,
        IEnumerable<string> submittedNames,
        IReadOnlyDictionary<string, string?> resolved)
    {
        ArgumentNullException.ThrowIfNull(submittedNames);
        ArgumentNullException.ThrowIfNull(resolved);

        var names = submittedNames as IReadOnlyCollection<string> ?? submittedNames.ToList();

        var serverDerived = names
            .Where(name => name is not null && name.Contains(':', StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (serverDerived.Count > 0)
        {
            throw new WorkloadParameterValidationException(
                $"Reserved parameter name(s) for workload '{workloadId}': {string.Join(", ", serverDerived)}. " +
                "A name containing ':' carries a server-derived value and can never be set by a client.");
        }

        var undeclared = names
            .Where(name => !resolved.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (undeclared.Count == 0)
        {
            return;
        }

        throw new WorkloadParameterValidationException(
            $"Unknown parameter(s) for workload '{workloadId}': {string.Join(", ", undeclared)}. " +
            "Only the workload's declared and reserved parameters may be set.");
    }
}
