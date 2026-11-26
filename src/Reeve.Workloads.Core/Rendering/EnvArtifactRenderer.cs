using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;

namespace Reeve.Workloads.Core.Rendering;

/// <summary>Renders a <see cref="WorkloadDesiredSpec"/> into a <c>.env</c> file: one deterministically-sorted
/// <c>KEY=value</c> line per var. A SecretRef reveals its credential (reveal-once) or a placeholder; a chmod 600 note is always emitted since the file may carry credentials in plaintext.</summary>
public sealed class EnvArtifactRenderer : IWorkloadArtifactRenderer
{
    /// <summary>The format this renderer declares: <c>env</c>.</summary>
    public static WorkloadArtifactFormatId FormatId { get; } = WorkloadArtifactFormatId.Parse("env");

    /// <inheritdoc />
    public WorkloadArtifactFormatId Format => FormatId;

    /// <inheritdoc />
    /// <remarks>An env file configures a runtime; it does not declare one.</remarks>
    public bool IsSelfContained => false;

    /// <inheritdoc />
    public RenderedWorkloadArtifact Render(WorkloadDesiredSpec spec, WorkloadArtifactRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(options);

        var entries = WorkloadEnvRendering.Resolve(spec, options);

        var sb = new StringBuilder();
        sb.Append("# Reeve workload environment — ").Append(spec.ResourceName).Append('\n');
        sb.Append("# One KEY=value per line. Deploy the sidecar container with this environment.");
        var dropped = new List<string>();
        foreach (var e in entries)
        {
            if (!WorkloadArtifactEscaping.IsSafeEnvName(e.Name)
                || !WorkloadArtifactEscaping.IsSafeDotEnvValue(e.Value))
            {
                dropped.Add(e.Name);
                continue;
            }

            sb.Append('\n').Append(e.Name).Append('=').Append(WorkloadArtifactEscaping.DotEnvValue(e.Value));
        }

        return new RenderedWorkloadArtifact
        {
            Format = Format,
            PrimaryContent = sb.ToString(),
            SecretOneLiner = null,
            Notes = BuildNotes(spec, entries, dropped),
        };
    }

    private static IReadOnlyList<string> BuildNotes(
        WorkloadDesiredSpec spec,
        IReadOnlyList<WorkloadEnvRendering.RenderedEnvEntry> entries,
        IReadOnlyList<string> droppedNames)
    {
        var notes = new List<string>
        {
            "This file may contain the workload's credentials in plaintext — restrict it: chmod 600 the .env file.",
        };

        if (droppedNames.Count > 0)
        {
            notes.Add(
                "OMITTED — a .env file cannot represent these entries safely, either because the name is not a "
                + "valid identifier or because the value contains a line break: "
                + string.Join(", ", droppedNames) + ". Set them by another means.");
        }

        var placeholders = entries.Where(e => e.IsSecretPlaceholder).Select(e => e.Name).ToList();
        if (placeholders.Count > 0)
        {
            notes.Add(
                "Set these secret values yourself before starting the container: "
                + string.Join(", ", placeholders) + ".");
        }

        var adoptedClaims = spec.Volumes
            .Where(v => v.ExistingClaim && !string.IsNullOrWhiteSpace(v.ResourceName))
            .OrderBy(v => v.ResourceName, StringComparer.Ordinal)
            .Select(v => v.ResourceName.Trim() + " (mount at " + v.MountPath + ")")
            .ToList();
        if (adoptedClaims.Count > 0)
        {
            notes.Add(
                "RESTORE: this .env file does NOT describe volumes — you must mount the retained data yourself. The "
                + "following physical volume(s) must already exist and be mounted into the sidecar for the restore to "
                + "work (mounting fresh empty storage instead silently discards the retained data): "
                + string.Join(", ", adoptedClaims) + ".");
        }

        return notes;
    }
}
