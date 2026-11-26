using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Network;

namespace Reeve.Workloads.Abstractions.Domain.Entities.Documents;

internal static partial class WorkloadDocumentMapper
{
    public static WorkloadStringMapDocument StringMap(IReadOnlyDictionary<string, string?> map) => new()
    {
        Entries = map
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new WorkloadStringPairDocument { Key = pair.Key, Value = pair.Value })
            .ToList(),
    };

    public static IReadOnlyDictionary<string, string?> FromDocument(WorkloadStringMapDocument document)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            map[entry.Key] = entry.Value;
        }

        return map;
    }

    public static WorkloadParameterMapDocument ParameterMap(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> map) => new()
    {
        Workloads = map
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new WorkloadParametersDocument
            {
                WorkloadId = pair.Key,
                Entries = pair.Value
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new WorkloadStringPairDocument { Key = entry.Key, Value = entry.Value })
                    .ToList(),
            })
            .ToList(),
    };

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> FromDocument(
        WorkloadParameterMapDocument document)
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var workload in document.Workloads)
        {
            var entries = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var entry in workload.Entries)
            {
                entries[entry.Key] = entry.Value;
            }

            map[workload.WorkloadId] = entries;
        }

        return map;
    }

    public static RetainedVolumesSnapshotDocument RetainedVolumes(
        IReadOnlyList<RetainedVolumeSnapshotItem> volumes) => new()
    {
        Volumes = volumes
            .Where(volume => volume is not null)
            .Select(volume => new RetainedVolumeSnapshotDocument
            {
                VolumeId = volume.VolumeId,
                ResourceName = volume.ResourceName,
                VolumeNamingScheme = volume.VolumeNamingScheme.PersistedValue,
                Size = volume.Size,
                Criticality = volume.Criticality.ToString(),
                Description = volume.Description,
            })
            .ToList(),
    };

    public static IReadOnlyList<RetainedVolumeSnapshotItem> FromDocument(RetainedVolumesSnapshotDocument document)
        => document.Volumes
            .Select(volume => new RetainedVolumeSnapshotItem
            {
                VolumeId = volume.VolumeId,
                ResourceName = volume.ResourceName,
                VolumeNamingScheme = RetainedVolumeSchemeId.Parse(volume.VolumeNamingScheme),
                Size = volume.Size,
                Criticality = Parse<VolumeCriticality>(volume.Criticality),
                Description = volume.Description,
            })
            .ToList();

    public static WorkloadAttachmentPinDocument AttachmentPin(WorkloadAttachmentPin pin) => new()
    {
        Mode = pin.Mode.ToString(),
        AttachmentNames = [.. pin.AttachmentNames],
    };

    public static WorkloadAttachmentPin FromDocument(WorkloadAttachmentPinDocument document) => new()
    {
        Mode = Parse<WorkloadNetworkMode>(document.Mode),
        AttachmentNames = [.. document.AttachmentNames],
    };

    private static List<string> Sorted(IEnumerable<string> values) => values.OrderBy(value => value, StringComparer.Ordinal).ToList();

    private static TEnum Parse<TEnum>(string name) where TEnum : struct, Enum
        => Array.IndexOf(Enum.GetNames<TEnum>(), name) >= 0
            ? Enum.Parse<TEnum>(name)
            : throw new FormatException($"'{name}' is not a {typeof(TEnum).Name} member name.");
}
