using System.Collections.Frozen;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Rendering;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core.Bootstrap;

internal static class WorkloadHostBootstrapStep
{
    internal const string InstanceIdKey = "instanceId";

    internal const string SpecHashKey = "specHash";

    internal static readonly FrozenSet<string> LibraryKeys =
        FrozenSet.ToFrozenSet([InstanceIdKey, SpecHashKey], StringComparer.Ordinal);

    private const string BootstrapPrefix = "bootstrap:";

    internal static WorkloadHostBootstrapRequest Request(
        Guid instanceId,
        string groupId,
        WorkloadProviderId provider,
        IWorkloadResourceNamer namer,
        WorkloadManifest workload,
        WorkloadBootstrapAudience audience)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(namer);
        ArgumentNullException.ThrowIfNull(workload);
        return new WorkloadHostBootstrapRequest
        {
            InstanceId = instanceId,
            Provider = provider,
            WorkloadName = workload.Id,
            Extensions = workload.Extensions,
            HealthMode = workload.Health.Mode,
            DeclaredKeys = DeclaredKeys(workload),
            SecretName = namer.SecretName(new WorkloadNamingSubject
            {
                GroupId = groupId,
                WorkloadId = workload.Id,
                Inst8 = WorkloadInstance.ComputeInst8(instanceId),
            }),
            Audience = audience,
        };
    }

    internal static IReadOnlySet<string> DeclaredKeys(WorkloadManifest workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        return workload.Env
            .Where(e => !string.IsNullOrEmpty(e.Name)
                && e.From is not null
                && e.From.StartsWith(BootstrapPrefix, StringComparison.Ordinal))
            .Select(e => e.From[BootstrapPrefix.Length..])
            .ToFrozenSet(StringComparer.Ordinal);
    }

    internal static async Task<WorkloadHostBootstrapComposition> ComposeAsync(
        IWorkloadHostBootstrapSource source,
        WorkloadHostBootstrapRequest request,
        WorkloadManifest workload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workload);

        var host = await source.GetAsync(request, cancellationToken).ConfigureAwait(false);
        if (host is null || host.Values is null || host.HostEnv is null || host.ProvisionedCredentials is null)
        {
            var missing = host is null ? "no answer" : "an answer with a null collection";
            throw new InvalidOperationException(
                $"The bootstrap source '{source.GetType().FullName}' gave {missing} for workload '{request.WorkloadName}'.");
        }

        if (host.Refusal is { } refused)
        {
            return WorkloadHostBootstrapComposition.Refused(refused);
        }

        if (host.Values.Keys.FirstOrDefault(LibraryKeys.Contains) is { } key)
        {
            return WorkloadHostBootstrapComposition.Refused(
                $"The host's bootstrap source answered the library's own key '{key}' for workload '{request.WorkloadName}'.");
        }

        if (host.HostEnv.FirstOrDefault(e => IsLibraryEnv(e.Name)) is { } env)
        {
            return WorkloadHostBootstrapComposition.Refused(
                $"The host's bootstrap source forced the library's env '{env.Name}' onto workload '{request.WorkloadName}'.");
        }

        if (host.ProvisionedCredentials.FirstOrDefault(c => IsLibraryEnv(c.Name)) is { } credential)
        {
            return WorkloadHostBootstrapComposition.Refused(
                $"The host's bootstrap source provisioned a credential under the library's env '{credential.Name}' for workload '{request.WorkloadName}'.");
        }

        if (host.HostEnv.FirstOrDefault(e => !WorkloadArtifactEscaping.IsSafeEnvName(e.Name)) is { } badEnv)
        {
            return WorkloadHostBootstrapComposition.Refused(
                $"The host's bootstrap source forced an env named '{badEnv.Name}', which is not a valid env name, onto workload '{request.WorkloadName}'.");
        }

        if (host.ProvisionedCredentials.FirstOrDefault(c => !WorkloadArtifactEscaping.IsSafeEnvName(c.Name)) is { } badName)
        {
            return WorkloadHostBootstrapComposition.Refused(
                $"The host's bootstrap source provisioned a credential under '{badName.Name}', which is not a valid env name, for workload '{request.WorkloadName}'.");
        }

        if (host.HostEnv.FirstOrDefault(e => e.CredentialId is not null && !WorkloadSpecCredentials.Claims(e)) is { } uncarried)
        {
            return WorkloadHostBootstrapComposition.Refused(
                $"The host's bootstrap source forced env '{uncarried.Name}' as a {uncarried.Kind} carrying credential id '{uncarried.CredentialId}' onto workload '{request.WorkloadName}'; only a secret reference with a non-blank id carries a credential.");
        }

        if (host.HostEnv.FirstOrDefault(e => e.ExcludedFromSpecHash && e.Kind != ResolvedEnvKind.Literal) is { } unhashed)
        {
            return WorkloadHostBootstrapComposition.Refused(
                $"The host's bootstrap source forced env '{unhashed.Name}' as a {unhashed.Kind} outside the spec hash onto workload '{request.WorkloadName}'; only a literal may be left out of the hash.");
        }

        if (CredentialNamedTwice(host, workload) is { } name)
        {
            return WorkloadHostBootstrapComposition.Refused(
                $"The host's bootstrap answer names '{name}' for more than one credential on workload '{request.WorkloadName}'.");
        }

        var values = new Dictionary<string, BootstrapValue>(host.Values, StringComparer.Ordinal)
        {
            [InstanceIdKey] = BootstrapValue.Literal(request.InstanceId.ToString("D")),
            [SpecHashKey] = BootstrapValue.SpecHash,
        };
        return new WorkloadHostBootstrapComposition(values, host.HostEnv, host.ProvisionedCredentials, Refusal: null);
    }

    private static string? CredentialNamedTwice(WorkloadHostBootstrapResult host, WorkloadManifest workload)
    {
        var forced = new Dictionary<string, ResolvedEnvVar>(StringComparer.Ordinal);
        foreach (var env in host.HostEnv)
        {
            forced[env.Name] = env;
        }

        var declared = workload.Env
            .Where(e => !string.IsNullOrEmpty(e.Name) && !forced.ContainsKey(e.Name) && !IsLibraryEnv(e.Name)
                && e.From is { } from && from.StartsWith(BootstrapPrefix, StringComparison.Ordinal))
            .Select(e => (e.Name, Value: host.Values.GetValueOrDefault(e.From[BootstrapPrefix.Length..])))
            .Where(e => e.Value?.CredentialId is not null)
            .Select(e => (e.Value!.CredentialId!, e.Name));
        var claims = host.ProvisionedCredentials.Select(c => (c.CredentialId, c.Name))
            .Concat(declared)
            .Concat(forced.Values.Where(WorkloadSpecCredentials.Claims).Select(e => (e.CredentialId!, e.Name)));

        return WorkloadSpecCredentials.ConflictingNames(claims).FirstOrDefault();
    }

    private static bool IsLibraryEnv(string? name)
        => name is WorkloadDesiredSpecFactory.SpecHashEnv or WorkloadDesiredSpecFactory.AppVersionEnv;

    internal static WorkloadBootstrapContext Context(
        WorkloadHostBootstrapRequest request, WorkloadHostBootstrapComposition composed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(composed);
        if (composed.Values is not { } values)
        {
            throw new ArgumentException("A refused composition renders no context.", nameof(composed));
        }

        return new WorkloadBootstrapContext
        {
            InstanceId = request.InstanceId,
            SecretName = request.SecretName,
            Values = values,
            HostEnv = composed.HostEnv,
            ProvisionedCredentials = composed.ProvisionedCredentials,
        };
    }
}

internal sealed record WorkloadHostBootstrapComposition(
    IReadOnlyDictionary<string, BootstrapValue>? Values,
    IReadOnlyList<ResolvedEnvVar> HostEnv,
    IReadOnlyList<ProvisionedCredential> ProvisionedCredentials,
    string? Refusal)
{
    internal static WorkloadHostBootstrapComposition Refused(string reason) => new(null, [], [], reason);
}
