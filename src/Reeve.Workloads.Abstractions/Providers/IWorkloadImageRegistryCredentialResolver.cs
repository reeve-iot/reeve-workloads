using System.Threading;
using System.Threading.Tasks;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Abstractions.Providers;

/// <summary>Resolves a fallback pull credential for a workload sidecar image from a configured OCI plugin
/// repository whose registry host EXACTLY matches (case-insensitive, never a substring or wildcard match, never returned for a non-matching host); consulted only when no operator-configured credential already covers the host.</summary>
public interface IWorkloadImageRegistryCredentialResolver
{
    /// <summary>Returns a pull credential for <paramref name="registryHost"/>, or <c>null</c> when none matches or
    /// the resolved token is empty. The returned <see cref="RegistryCredential.HostPattern"/> is the exact host, never a wildcard.</summary>
    Task<RegistryCredential?> ResolveAsync(string registryHost, CancellationToken cancellationToken = default);
}

/// <summary>The shipped default: no fallback credential, ever. Identical in effect to leaving the port
/// unregistered, with the one difference that matters — this one is REPORTED as a decline rather than absent.</summary>
public sealed class NoWorkloadImageRegistryCredentialResolver : IWorkloadImageRegistryCredentialResolver
{
    /// <inheritdoc />
    public Task<RegistryCredential?> ResolveAsync(
        string registryHost, CancellationToken cancellationToken = default)
        => Task.FromResult<RegistryCredential?>(null);
}
