using System.Text.Json;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core;

/// <summary>Judges a proposed settings document against the instances and retained volumes that exist and the
/// document served now. It writes nothing.</summary>
public sealed class WorkloadOrchestratorSettingsJudge : IWorkloadOrchestratorSettingsJudge
{
    private readonly IWorkloadOrchestratorSettingsSource source;
    private readonly IWorkloadInstanceRepository instanceRepository;
    private readonly IRetainedVolumeRepository volumeRepository;
    private readonly IReadOnlyDictionary<WorkloadProviderId, IWorkloadProviderSettingsCodec> codecs;
    private readonly EffectiveProviderResolver resolver;

    public WorkloadOrchestratorSettingsJudge(
        IWorkloadOrchestratorSettingsSource source,
        IWorkloadInstanceRepository instanceRepository,
        IRetainedVolumeRepository volumeRepository,
        ICompiledProviderCatalog catalog,
        IEnumerable<IWorkloadProviderSettingsCodec> codecs,
        IEnumerable<IWorkloadProviderAutoDetect> autoDetects,
        WorkloadOrchestratorSettingsCache? cache = null)
    {
        this.source = source;
        this.instanceRepository = instanceRepository;
        this.volumeRepository = volumeRepository;
        var byProvider = new Dictionary<WorkloadProviderId, IWorkloadProviderSettingsCodec>();
        foreach (var codec in codecs)
        {
            byProvider[codec.Provider] = codec;
        }

        this.codecs = byProvider;
        resolver = new EffectiveProviderResolver(catalog, autoDetects, cache);
    }

    public async Task<WorkloadSettingsVerdict> JudgeAsync(
        WorkloadOrchestratorSettings proposed,
        WorkloadSettingsHostFacts facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(facts);

        var current = await source.GetAsync(cancellationToken).ConfigureAwait(false);
        var instances = await instanceRepository.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var retained = await volumeRepository.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var proposedWithSet = proposed.ConfiguredProviders is null
            ? proposed with { ConfiguredProviders = current.ConfiguredProviders }
            : proposed;
        var requested = (await resolver.ResolveAsync(proposedWithSet, cancellationToken).ConfigureAwait(false)).Provider;
        var active = (await resolver.ResolveAsync(current, cancellationToken).ConfigureAwait(false)).Provider;

        var blocks = new List<WorkloadSettingsBlock>();
        blocks.AddRange(SwitchBlocks(instances, requested, active));
        blocks.AddRange(EntryBlocks(current, proposed, active, instances, retained));
        if (proposed.Provider is { } declared
            && current.ConfiguredProviders is { } configured
            && !configured.Contains(declared))
        {
            blocks.Add(new WorkloadSettingsBlock
            {
                Code = WorkloadSettingsBlockCodes.TargetNotConfigured,
                Provider = declared,
                Reason = $"The '{declared}' provider cannot be the target: it is not among the configured providers.",
            });
        }
        else if (proposed.Provider is { } target && facts.UnavailableTargets.TryGetValue(target, out var why))
        {
            blocks.Add(new WorkloadSettingsBlock
            {
                Code = WorkloadSettingsBlockCodes.TargetNotConfigured,
                Provider = target,
                Reason = $"The '{target}' provider cannot be the target: {why}",
            });
        }

        return new WorkloadSettingsVerdict { Blocks = blocks, RequestedProvider = requested, ActiveProvider = active };
    }

    private static IEnumerable<WorkloadSettingsBlock> SwitchBlocks(
        IReadOnlyList<WorkloadInstance> instances,
        WorkloadProviderId requested,
        WorkloadProviderId active)
    {
        if (requested == active)
        {
            yield break;
        }

        var blocking = instances
            .Where(i => i.State != WorkloadInstanceState.Terminating && i.Provider == active)
            .ToArray();
        if (blocking.Length == 0)
        {
            yield break;
        }

        var target = requested.IsEmpty ? "no provider" : $"'{requested}'";
        yield return new WorkloadSettingsBlock
        {
            Code = WorkloadSettingsBlockCodes.ProviderSwitchBlocked,
            Provider = active,
            Reason = $"Cannot switch the workload provider from '{active}' to {target}: "
                + $"{Describe(blocking)} still run on '{active}'. Migrate or remove them first.",
            Instances = [.. blocking.Select(Blocker)],
        };
    }

    private IEnumerable<WorkloadSettingsBlock> EntryBlocks(
        WorkloadOrchestratorSettings current,
        WorkloadOrchestratorSettings proposed,
        WorkloadProviderId active,
        IReadOnlyList<WorkloadInstance> instances,
        IReadOnlyList<RetainedVolume> retained)
    {
        foreach (var provider in current.Providers.Keys.Union(proposed.Providers.Keys))
        {
            var before = current.Entry(provider);
            var after = proposed.Entry(provider);
            var dropped = after.ValueKind is JsonValueKind.Undefined && before.ValueKind is not JsonValueKind.Undefined;
            var moved = after.ValueKind is JsonValueKind.Undefined ? [] : Relocated(provider, before, after);
            if (!dropped && moved.Count == 0)
            {
                continue;
            }

            var boundInstances = instances.Where(i => i.Provider == provider).ToArray();
            var heldVolumes = retained.Where(v => v.Provider == provider).ToArray();
            var dropsActive = dropped && provider == active;
            if (!dropsActive && boundInstances.Length == 0 && heldVolumes.Length == 0)
            {
                continue;
            }

            var what = dropped ? "drop" : $"relocate ({string.Join(", ", moved)})";
            yield return new WorkloadSettingsBlock
            {
                Code = WorkloadSettingsBlockCodes.ProviderInUse,
                Provider = provider,
                Reason = $"Cannot {what} the '{provider}' provider entry: {Uses(dropsActive, boundInstances, heldVolumes)}",
                Instances = [.. boundInstances.Select(Blocker)],
                Volumes = [.. heldVolumes.Select(v => new WorkloadSettingsBlockingVolume
                {
                    Id = v.Id,
                    GroupId = v.GroupId,
                    WorkloadId = v.WorkloadId,
                    VolumeId = v.VolumeId,
                })],
            };
        }
    }

    private List<string> Relocated(WorkloadProviderId provider, JsonElement before, JsonElement after)
    {
        if (!codecs.TryGetValue(provider, out var codec))
        {
            return [];
        }

        var was = codec.Placement(before);
        var now = codec.Placement(after);
        return [.. was.Keys
            .Where(k => was[k] != now.GetValueOrDefault(k))
            .Order(StringComparer.Ordinal)];
    }

    private static string Uses(bool dropsActive, WorkloadInstance[] instances, RetainedVolume[] volumes)
    {
        var uses = new List<string>();
        if (dropsActive)
        {
            uses.Add("it is the active provider");
        }

        if (instances.Length > 0)
        {
            uses.Add($"{Describe(instances)} are bound to it");
        }

        if (volumes.Length > 0)
        {
            uses.Add($"{string.Join(", ", volumes.Select(v => $"{v.GroupId}/{v.WorkloadId}:{v.VolumeId}"))} are retained on it");
        }

        return string.Join("; ", uses) + ".";
    }

    private static string Describe(IEnumerable<WorkloadInstance> instances)
    {
        var list = instances.ToArray();
        return $"{list.Length} workload instance(s) ({string.Join(", ", list.Select(i => $"{i.GroupId}/{i.WorkloadId}"))})";
    }

    private static WorkloadProviderSwitchBlocker Blocker(WorkloadInstance i)
        => new() { Id = i.Id, GroupId = i.GroupId, WorkloadId = i.WorkloadId };
}
