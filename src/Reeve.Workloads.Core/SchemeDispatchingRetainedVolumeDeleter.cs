using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Core;

internal sealed class SchemeDispatchingRetainedVolumeDeleter : IRetainedVolumeDeleter
{
    private readonly IReadOnlyList<IRetainedVolumeSchemeDeleter> deleters;
    private readonly ILogger<SchemeDispatchingRetainedVolumeDeleter> logger;

    public SchemeDispatchingRetainedVolumeDeleter(
        IEnumerable<IRetainedVolumeSchemeDeleter> deleters,
        ILogger<SchemeDispatchingRetainedVolumeDeleter> logger)
    {
        this.deleters = (deleters ?? throw new ArgumentNullException(nameof(deleters))).ToList();
        foreach (var group in this.deleters.GroupBy(deleter => deleter.NamingScheme).Where(group => group.Count() > 1))
        {
            var types = group.Select(deleter => deleter.GetType().FullName).ToList();
            throw new InvalidOperationException(
                $"Retained volume naming scheme '{group.Key}' is claimed by two deleters: {types[0]} and {types[1]}.");
        }

        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<bool> TryDeletePhysicalVolumeAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedVolume);

        var owner = deleters.SingleOrDefault(deleter => deleter.NamingScheme == retainedVolume.VolumeNamingScheme);
        if (owner is null)
        {
            logger.LogWarning(
                "Purge of retained volume {ResourceName} refused: no registered provider owns the naming scheme " +
                "{Scheme} (registered: {Registered}) — leaving it retained (fail-closed).",
                retainedVolume.ResourceName,
                retainedVolume.VolumeNamingScheme,
                string.Join(", ", deleters.Select(deleter => deleter.NamingScheme.ToString())));
            return Task.FromResult(false);
        }

        return owner.TryDeletePhysicalVolumeAsync(retainedVolume, cancellationToken);
    }
}
