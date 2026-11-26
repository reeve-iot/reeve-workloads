using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.EntityFrameworkCore.Persistence;

/// <summary>The parts of the workloads model that each database spells its own way, stated without a dialect.
/// A conventions package reads this and nothing else, so the model can grow without it being edited.</summary>
public static class WorkloadsModelContract
{
    /// <summary>Typed JSON documents, each a complex property stored whole in one column. The agnostic model leaves
    /// the column the provider's default for JSON; a database pins its own, so a provider upgrade never moves it.</summary>
    public static IReadOnlyList<WorkloadsJsonColumn> JsonDocumentColumns { get; } =
    [
        new(typeof(WorkloadInstance), nameof(WorkloadInstance.ParametersDocument)),
        new(typeof(WorkloadInstance), nameof(WorkloadInstance.RetainedVolumesSnapshotDocument)),
        new(typeof(WorkloadInstance), nameof(WorkloadInstance.AttachmentPinDocument)),
        new(typeof(WorkloadUpdateIntent), nameof(WorkloadUpdateIntent.TargetSpecsDocument)),
        new(typeof(WorkloadUpdateIntent), nameof(WorkloadUpdateIntent.PreviousSpecsDocument)),
        new(typeof(WorkloadUpdateIntent), nameof(WorkloadUpdateIntent.PreviousParametersDocument)),
        new(typeof(WorkloadUpdateIntent), nameof(WorkloadUpdateIntent.OperatorParametersDocument)),
        new(typeof(WorkloadUpdateIntent), nameof(WorkloadUpdateIntent.PreviousPasswordParameterNamesDocument)),
        new(typeof(WorkloadUpdateIntent), nameof(WorkloadUpdateIntent.CandidatePasswordParameterNamesDocument)),
        new(typeof(WorkloadUpdateIntent), nameof(WorkloadUpdateIntent.FromDigestsDocument)),
        new(typeof(WorkloadUpdateIntent), nameof(WorkloadUpdateIntent.ToDigestsDocument)),
        new(typeof(WorkloadUpdateIntent), nameof(WorkloadUpdateIntent.EvidenceDocument)),
        new(typeof(WorkloadUpdateIntent), nameof(WorkloadUpdateIntent.StageJournalDocument)),
        new(typeof(WorkloadUpdateIntentStep), nameof(WorkloadUpdateIntentStep.GateClockStateDocument)),
    ];

    /// <summary>Lists of scalars, each in one column, which a database may store as a native array.</summary>
    public static IReadOnlyList<WorkloadsJsonColumn> ListColumns { get; } =
    [
        new(typeof(WorkloadInstance), nameof(WorkloadInstance.LastScanFailedNodes)),
        new(typeof(WorkloadUpdateIntent), nameof(WorkloadUpdateIntent.AdoptedResourceWorkloadIds)),
    ];

    /// <summary>Unique indexes that must hold over ACTIVE rows only. Without a filter they hold over every row,
    /// which blocks re-recording a purged volume and a second update after a finished one. Some databases can
    /// express the filter and some cannot; the agnostic model states it here and filters nothing.</summary>
    public static IReadOnlyList<WorkloadsActiveRowIndex> ActiveRowIndexes { get; } =
    [
        new(
            typeof(RetainedVolume),
            "ix_retained_volumes_source_workload_instance_id_volume_id",
            [nameof(RetainedVolume.ReattachedAtUtc), nameof(RetainedVolume.PurgedAtUtc)]),
        new(
            typeof(RetainedVolume),
            "ux_retained_volumes_active_resource_name",
            [nameof(RetainedVolume.ReattachedAtUtc), nameof(RetainedVolume.PurgedAtUtc)]),
        new(
            typeof(WorkloadUpdateIntent),
            "ux_workload_update_intents_active_module_instance_id",
            NullProperties: [],
            ExcludedProperty: nameof(WorkloadUpdateIntent.Phase),
            ExcludedValues:
            [
                nameof(WorkloadUpdatePhase.Completed),
                nameof(WorkloadUpdatePhase.RolledBack),
                nameof(WorkloadUpdatePhase.RollbackFailed),
            ]),
    ];
}

/// <summary>One JSON-bearing property: the entity it is on and the property's CLR name.</summary>
public sealed record WorkloadsJsonColumn(Type EntityType, string PropertyName)
{
    /// <summary>The complex type this names, on the model being built, for a document column.</summary>
    public IMutableComplexType DocumentTypeIn(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return (modelBuilder.Entity(EntityType).Metadata.FindComplexProperty(PropertyName)
            ?? throw new InvalidOperationException($"'{EntityType.Name}' has no complex property '{PropertyName}'."))
            .ComplexType;
    }

    /// <summary>The scalar property this names, on the model being built, for a string or list column.</summary>
    public IMutableProperty PropertyIn(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return modelBuilder.Entity(EntityType).Metadata.FindProperty(PropertyName)
            ?? throw new InvalidOperationException($"'{EntityType.Name}' has no mapped property '{PropertyName}'.");
    }
}

/// <summary>A unique index whose rows count only while active: every <see cref="NullProperties"/> is null and
/// <see cref="ExcludedProperty"/>, when set, holds none of <see cref="ExcludedValues"/> (stored as strings).
/// The conditions are joined with AND, in the order given.</summary>
public sealed record WorkloadsActiveRowIndex(
    Type EntityType,
    string DatabaseName,
    IReadOnlyList<string> NullProperties,
    string? ExcludedProperty = null,
    IReadOnlyList<string>? ExcludedValues = null)
{
    /// <summary>This index, on the model being built.</summary>
    public IMutableIndex FindIn(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return modelBuilder.Entity(EntityType).Metadata.GetIndexes()
            .Single(index => string.Equals(index.GetDatabaseName(), DatabaseName, StringComparison.Ordinal));
    }

    /// <summary>The column a property of <see cref="EntityType"/> is stored in, on the model being built.</summary>
    public string ColumnOf(ModelBuilder modelBuilder, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var property = modelBuilder.Entity(EntityType).Metadata.FindProperty(propertyName)
            ?? throw new InvalidOperationException($"'{EntityType.Name}' has no mapped property '{propertyName}'.");
        return property.GetColumnName();
    }
}
