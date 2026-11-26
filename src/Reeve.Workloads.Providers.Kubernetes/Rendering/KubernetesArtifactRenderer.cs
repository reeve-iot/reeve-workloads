using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using k8s;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Providers.Kubernetes.Rendering;

/// <summary>Renders a <see cref="WorkloadDesiredSpec"/> into a multi-document Kubernetes manifest, reusing
/// <see cref="KubernetesWorkloadRenderer"/> for the core API objects. CRITICAL: no secret VALUE is ever inlined —
/// every credential, and every password-typed parameter, render as a secretKeyRef and the Secret shell itself is
/// excluded, so what is rendered carries no material; those ship via a separate one-liner or operator-created Secret.</summary>
public sealed class KubernetesArtifactRenderer : IWorkloadArtifactRenderer
{
    private const string DefaultNamespace = "reeve";

    private static readonly KubernetesWorkloadRenderer ObjectRenderer = new();

    private static readonly UnsupportedPodPatchApplier PatchApplier = new();

    private static readonly KubernetesPatchRevalidator PatchRevalidator = new();

    /// <summary>The format this renderer declares: <c>kubernetes</c>.</summary>
    public static WorkloadArtifactFormatId FormatId { get; } = WorkloadArtifactFormatId.Parse("kubernetes");

    /// <inheritdoc />
    public WorkloadArtifactFormatId Format => FormatId;

    /// <inheritdoc />
    public bool IsSelfContained => true;

    /// <inheritdoc />
    public RenderedWorkloadArtifact Render(WorkloadDesiredSpec spec, WorkloadArtifactRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(options);

        var ctx = new KubernetesRenderContext { Namespace = DefaultNamespace };
        var objects = ObjectRenderer.Render(spec, ctx);

        var patchViolations = new List<string>();
        if (spec.UnsupportedPodPatch is { } declaredPatch)
        {
            objects = PatchApplier.Apply(objects, declaredPatch.PatchJson);
            patchViolations = PatchRevalidator.Validate(spec, objects).ToList();
        }

        var documents = new List<object>();
        if (objects.Deployment is not null)
        {
            documents.Add(objects.Deployment);
        }

        if (objects.DaemonSet is not null)
        {
            documents.Add(objects.DaemonSet);
        }

        if (objects.Service is not null)
        {
            documents.Add(objects.Service);
        }

        var adoptedClaimNames = spec.Volumes
            .Where(v => v.ExistingClaim && !string.IsNullOrWhiteSpace(v.ResourceName))
            .Select(v => v.ResourceName.Trim())
            .ToHashSet(StringComparer.Ordinal);

        documents.AddRange(objects.PersistentVolumeClaims
            .Where(pvc => pvc.Metadata?.Name is not { Length: > 0 } name || !adoptedClaimNames.Contains(name)));

        var yaml = SerializeMultiDocument(documents);

        return new RenderedWorkloadArtifact
        {
            Format = Format,
            PrimaryContent = yaml,
            SecretOneLiner = BuildSecretOneLiner(spec, ctx.Namespace, options),
            Notes = BuildNotes(spec, ctx.Namespace, options, patchViolations, adoptedClaimNames),
        };
    }

    private static string SerializeMultiDocument(IReadOnlyList<object> documents)
    {
        var parts = documents
            .Select(d => KubernetesYaml.Serialize(d).TrimEnd('\n'))
            .ToList();

        return string.Join("\n---\n", parts);
    }

    private static IReadOnlyList<(string Key, string? Value)> CredentialKeys(
        WorkloadDesiredSpec spec, WorkloadArtifactRenderOptions options)
        => WorkloadSpecCredentials.Of(spec).Credentials
            .SelectMany(credential => credential.Names.Select(name => (
                Key: name,
                Value: !options.ExternalSecretMode && options.RevealedCredentials.TryGetValue(credential.CredentialId, out var value)
                    ? value
                    : null)))
            .OrderBy(key => key.Key, StringComparer.Ordinal)
            .ToList();

    private static string ShellQuoted(string word) => "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string? BuildSecretOneLiner(
        WorkloadDesiredSpec spec, string @namespace, WorkloadArtifactRenderOptions options)
    {
        var revealed = CredentialKeys(spec, options).Where(key => key.Value is not null).ToList();
        if (options.ExternalSecretMode || revealed.Count == 0)
        {
            return null;
        }

        return
            $"kubectl create secret generic {spec.SecretName} "
            + string.Concat(revealed.Select(key => $"--from-literal={ShellQuoted($"{key.Key}={key.Value}")} "))
            + $"-n {@namespace}";
    }

    private static IReadOnlyList<string> BuildNotes(
        WorkloadDesiredSpec spec, string @namespace, WorkloadArtifactRenderOptions options,
        IReadOnlyList<string> patchViolations, IReadOnlyCollection<string> adoptedClaimNames)
    {
        var notes = new List<string>
        {
            "The image is pinned by digest (ref@digest); Reeve cannot verify your out-of-band pull path — ensure the digest is reachable from your cluster's nodes.",
            "This manifest inlines no secret VALUE: every credential and every password-typed parameter render as a secretKeyRef into the named Secret, which is not part of this file. Non-secret parameters ARE inlined verbatim — check them before committing.",
        };

        if (adoptedClaimNames.Count > 0)
        {
            notes.Add(
                "RESTORE: this manifest OMITS the PVC(s) for the volume(s) you chose to restore — they reference RETAINED "
                + "data that must already exist. The following PersistentVolumeClaim(s) MUST already exist in namespace '"
                + @namespace + "' before you apply (do NOT create them fresh — that would deploy an empty volume and lose "
                + "the restore): " + string.Join(", ", adoptedClaimNames.OrderBy(n => n, StringComparer.Ordinal)) + ".");
        }

        var keys = CredentialKeys(spec, options);
        var unrevealed = keys.Where(key => key.Value is null).Select(key => $"'{key.Key}'").ToList();
        if (BuildSecretOneLiner(spec, @namespace, options) is null)
        {
            notes.Add(
                $"Create the Secret yourself: name '{spec.SecretName}' in namespace '{@namespace}'"
                + (unrevealed.Count > 0
                    ? $", with key(s) {string.Join(", ", unrevealed)} set to the workload's credentials."
                    : "."));
        }
        else
        {
            notes.Add(
                "Run the accompanying `kubectl create secret` one-liner ONCE to create the credential Secret — it is shown only this once."
                + (unrevealed.Count > 0
                    ? $" Add key(s) {string.Join(", ", unrevealed)} to it yourself: no value was revealed for them."
                    : string.Empty));
        }

        if (spec.UnsupportedPodPatch is not null)
        {
            notes.Add(
                "This manifest APPLIES the declared unsupportedPodPatch escape hatch to the pod-bearing object — "
                + "the same override the managed Kubernetes provider applies.");
        }

        if (patchViolations.Count > 0)
        {
            notes.Add(
                "WARNING: the applied unsupportedPodPatch escalates beyond the typed privileges the desired spec resolves to — the managed "
                + "Kubernetes provider would REJECT it. This manual manifest still contains it (you own the apply); "
                + "review before deploying: " + string.Join("; ", patchViolations));
        }

        return notes;
    }
}
