using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.EntityFrameworkCore.Persistence;

/// <summary>Maps <see cref="RetainedVolumeSchemeId"/> to the volume-naming-scheme column's string.</summary>
/// <remarks>
/// The column holds the scheme's persisted spelling: <c>Kubernetes</c> and <c>Docker</c> keep the enum-member spelling, so the column — and its <c>'Kubernetes'</c> default — never moves, and any other scheme is written in its canonical lowercase.
/// Reads go through the case-insensitive <see cref="RetainedVolumeSchemeId.Parse"/>, so either spelling
/// materialises as the same id; nothing in this library filters on the column in SQL.
/// </remarks>
public sealed class RetainedVolumeSchemeIdConverter : ValueConverter<RetainedVolumeSchemeId, string>
{
    public RetainedVolumeSchemeIdConverter()
        : base(id => id.PersistedValue, stored => RetainedVolumeSchemeId.Parse(stored))
    {
    }
}

/// <summary>Value comparison for the id, so EF's change tracker compares the canonical string rather than
/// falling back on reference equality for a struct it does not know.</summary>
public sealed class RetainedVolumeSchemeIdComparer : ValueComparer<RetainedVolumeSchemeId>
{
    public RetainedVolumeSchemeIdComparer()
        : base((left, right) => left.Value == right.Value, id => id.Value.GetHashCode(StringComparison.Ordinal))
    {
    }
}
