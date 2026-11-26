using System;
using System.Collections.Generic;
using System.Linq;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;

namespace Reeve.Workloads.Core.Rendering;

/// <summary>The ONE secret-handling rule for the artifact renderers, so every format stays byte-consistent:
/// only a revealed credential is ever inlined, and only into an entry that carries it as
/// <see cref="WorkloadSpecCredentials.CredentialFor"/> says; every other SecretRef renders a placeholder.</summary>
public static class WorkloadEnvRendering
{
    /// <summary>The placeholder rendered for any secret the operator must supply out-of-band.</summary>
    public const string SecretPlaceholder = "<set-this-secret>";

    /// <summary>A single resolved env entry for rendering: its name, the string to render, and whether it is a still-unresolved secret placeholder.</summary>
    public readonly record struct RenderedEnvEntry(string Name, string Value, bool IsSecretPlaceholder);

    /// <summary>Resolves Env into deterministically-sorted entries. A credential is inlined ONLY when its value was
    /// revealed AND external-secret mode is off.</summary>
    public static IReadOnlyList<RenderedEnvEntry> Resolve(WorkloadDesiredSpec spec, WorkloadArtifactRenderOptions options)
    {
        var revealed = options.ExternalSecretMode
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : options.RevealedCredentials;

        var credentials = WorkloadSpecCredentials.Of(spec);
        return spec.Env
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => Resolve(e, credentials, revealed))
            .ToList();
    }

    private static RenderedEnvEntry Resolve(
        ResolvedEnvVar e, WorkloadSpecCredentials credentials, IReadOnlyDictionary<string, string> revealed)
    {
        switch (e.Kind)
        {
            case ResolvedEnvKind.SecretRef:
                if (credentials.CredentialFor(e) is { } credentialId && revealed.TryGetValue(credentialId, out var value))
                {
                    return new RenderedEnvEntry(e.Name, value, IsSecretPlaceholder: false);
                }

                return new RenderedEnvEntry(e.Name, SecretPlaceholder, IsSecretPlaceholder: true);

            case ResolvedEnvKind.Literal:
            case ResolvedEnvKind.SpecHash:
            default:
                return new RenderedEnvEntry(e.Name, e.Value ?? string.Empty, IsSecretPlaceholder: false);
        }
    }
}
