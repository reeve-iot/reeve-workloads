using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.EntityFrameworkCore.Persistence;

/// <summary>Maps <see cref="WorkloadProviderId"/> to the provider column's string.</summary>
/// <remarks>
/// The id canonicalises to lowercase, and <see cref="WorkloadProviderId.Parse"/> is case-insensitive so a row holding the PascalCase spelling (<c>Docker</c>) still READS. It does not MATCH a lowercase SQL query, though; normalise the provider columns of <c>workload_instances</c> and <c>retained_volumes</c> with <c>lower("Provider")</c> to make it do so.
/// </remarks>
public sealed class WorkloadProviderIdConverter : ValueConverter<WorkloadProviderId, string>
{
    public WorkloadProviderIdConverter()
        : base(id => id.Value, stored => WorkloadProviderId.Parse(stored))
    {
    }
}

/// <summary>Value comparison for the id, so EF's change tracker compares the canonical string rather than
/// falling back on reference equality for a struct it does not know.</summary>
public sealed class WorkloadProviderIdComparer : ValueComparer<WorkloadProviderId>
{
    public WorkloadProviderIdComparer()
        : base((left, right) => left.Value == right.Value, id => id.Value.GetHashCode(StringComparison.Ordinal))
    {
    }
}
