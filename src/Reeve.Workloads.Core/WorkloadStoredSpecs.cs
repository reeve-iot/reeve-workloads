using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core;

internal static class WorkloadStoredSpecs
{
    public static WorkloadDesiredSpec WithHostValuesOf(WorkloadDesiredSpec stored, WorkloadDesiredSpec rendered)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(rendered);

        return SameVersion(stored, rendered)
            ? FromItsOwnVersion(stored, rendered)
            : FromAnotherVersion(stored, rendered);
    }

    internal static bool SameVersion(WorkloadDesiredSpec stored, WorkloadDesiredSpec rendered)
        => string.Equals(VersionOf(stored), VersionOf(rendered), StringComparison.Ordinal);

    private static WorkloadDesiredSpec FromAnotherVersion(WorkloadDesiredSpec stored, WorkloadDesiredSpec rendered)
        => stored with
        {
            SecretMaterial = rendered.SecretMaterial
                .Where(entry => BacksNameWithMaterial(stored, entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            Env = WithCurrentUnhashedEnv(stored.Env, rendered.Env),
        };

    private static WorkloadDesiredSpec FromItsOwnVersion(WorkloadDesiredSpec stored, WorkloadDesiredSpec rendered)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var credential in WorkloadSpecCredentials.Of(rendered).Credentials)
        {
            foreach (var name in credential.Names)
            {
                owners.TryAdd(name, credential.CredentialId);
            }
        }

        var carriesNone = stored.ProvisionedCredentials.Count == 0 && !stored.Env.Any(WorkloadSpecCredentials.Claims);

        return stored with
        {
            SecretMaterial = rendered.SecretMaterial,
            Env = [.. WithCurrentUnhashedEnv(stored.Env, rendered.Env).Select(env =>
                env.Kind == ResolvedEnvKind.SecretRef
                && !WorkloadSpecCredentials.Claims(env)
                && owners.TryGetValue(env.Name, out var owner)
                    ? env with { CredentialId = owner }
                    : env)],
            ProvisionedCredentials = carriesNone ? rendered.ProvisionedCredentials : stored.ProvisionedCredentials,
        };
    }

    private static IReadOnlyList<ResolvedEnvVar> WithCurrentUnhashedEnv(
        IReadOnlyList<ResolvedEnvVar> stored, IReadOnlyList<ResolvedEnvVar> rendered)
        => [.. stored.Select(env => env.ExcludedFromSpecHash
            && rendered.FirstOrDefault(r => r.ExcludedFromSpecHash && string.Equals(r.Name, env.Name, StringComparison.Ordinal)) is { } current
                ? current
                : env)];

    private static bool BacksNameWithMaterial(WorkloadDesiredSpec stored, string name)
    {
        var entries = stored.Env.Where(env => string.Equals(env.Name, name, StringComparison.Ordinal)).ToList();
        return entries.Count > 0
            && entries.All(env => env.Kind == ResolvedEnvKind.SecretRef && !WorkloadSpecCredentials.Claims(env))
            && !stored.ProvisionedCredentials.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal));
    }

    private static string? VersionOf(WorkloadDesiredSpec spec)
        => spec.Env
            .FirstOrDefault(env => string.Equals(env.Name, WorkloadDesiredSpecFactory.AppVersionEnv, StringComparison.Ordinal))
            ?.Value;
}
