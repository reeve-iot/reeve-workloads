using Microsoft.AspNetCore.DataProtection;
using Reeve.Workloads.Abstractions.Contracts.Deployment;

namespace Reeve.Workloads.Core;

/// <summary>Protects password parameters in the instance parameter document. Values remain opaque strings outside
/// this boundary. A stored password value is <c>enc:v1:</c> ciphertext, a <c>ref:</c> reference or empty; any other
/// value is undecryptable.</summary>
public sealed class WorkloadParameterSecretCodec(IDataProtectionProvider provider)
{
    public const string Purpose = "Reeve.Workloads.WorkloadParameters";
    public const string Prefix = "enc:v1:";
    private readonly IDataProtector protector = (provider ?? throw new ArgumentNullException(nameof(provider)))
        .CreateProtector(Purpose);

    public IReadOnlyDictionary<string, string?> Protect(
        WorkloadManifest workload, IReadOnlyDictionary<string, string?> values)
        => Transform(workload, values, protect: true);

    /// <summary>Protects changed literals while preserving byte-identical stored values, including ciphertext the
    /// current key ring can no longer authenticate.</summary>
    public IReadOnlyDictionary<string, string?> ProtectAgainstStored(
        WorkloadManifest workload,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?> stored)
    {
        var result = new Dictionary<string, string?>(Protect(workload, values), StringComparer.Ordinal);
        foreach (var name in WorkloadPasswordNames.For(workload))
            if (values.TryGetValue(name, out var value)
                && stored.TryGetValue(name, out var prior)
                && string.Equals(value, prior, StringComparison.Ordinal))
                result[name] = prior;
        return result;
    }

    /// <summary>Protects only values whose names were password-typed in an earlier manifest, for rollback writes.</summary>
    public IReadOnlyDictionary<string, string?> ProtectNamedParameters(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyCollection<string> passwordNames)
    {
        var names = passwordNames.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string?>(values.Count, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (value is null || !names.Contains(name) || value == "***" || value.StartsWith("ref:", StringComparison.Ordinal))
            {
                result[name] = value;
                continue;
            }
            if (value.StartsWith(Prefix, StringComparison.Ordinal))
            {
                try { _ = protector.Unprotect(value[Prefix.Length..]); result[name] = value; }
                catch (System.Security.Cryptography.CryptographicException) { result[name] = value; }
            }
            else result[name] = Prefix + protector.Protect(value);
        }
        return result;
    }

    public IReadOnlyDictionary<string, string?> Unprotect(
        WorkloadManifest workload, IReadOnlyDictionary<string, string?> values)
        => Transform(workload, values, protect: false);

    internal IReadOnlyDictionary<string, string?> UnprotectNamed(
        WorkloadManifest workload, IReadOnlyDictionary<string, string?> values, IEnumerable<string> passwordNames)
        => Transform(workload, values, protect: false, passwordNames.ToHashSet(StringComparer.Ordinal));

    private IReadOnlyDictionary<string, string?> Transform(
        WorkloadManifest workload, IReadOnlyDictionary<string, string?> values, bool protect,
        IReadOnlySet<string>? passwordNamesOverride = null)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(values);
        var passwords = WorkloadPasswordNames.For(workload, passwordNamesOverride);
        var result = new Dictionary<string, string?>(values.Count, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (value is null || value == "***" || value.StartsWith("ref:", StringComparison.Ordinal))
            {
                result[name] = value;
            }
            else if (!passwords.Contains(name))
            {
                result[name] = value;
            }
            else if (!protect)
            {
                result[name] = value.Length == 0 ? value : UnprotectValue(name, value);
            }
            else
            {
                if (value.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    try
                    {
                        _ = protector.Unprotect(value[Prefix.Length..]);
                        result[name] = value;
                        continue;
                    }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                    }
                }
                result[name] = Prefix + protector.Protect(value);
            }
        }
        return result;
    }

    private string UnprotectValue(string name, string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) throw Undecryptable(name);
        try { return protector.Unprotect(value[Prefix.Length..]); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw Undecryptable(name);
        }
    }

    private static Reeve.Workloads.Domain.DesiredSpec.WorkloadParameterResolutionException Undecryptable(string name)
        => new(name, "stored password is undecryptable; restore the Data Protection key ring or replace this value.");
}
