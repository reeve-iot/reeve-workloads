using System.Collections.ObjectModel;

namespace Reeve.Workloads.Abstractions.Domain.DesiredSpec;

/// <summary>One credential a desired spec carries: the host's id for it, and the names it owns. Only
/// <see cref="WorkloadSpecCredentials.Of"/> makes one.</summary>
public sealed record WorkloadSpecCredential
{
    internal WorkloadSpecCredential(string credentialId, IEnumerable<string> names)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        ArgumentNullException.ThrowIfNull(names);
        CredentialId = credentialId;
        Names = new ReadOnlyCollection<string>(names.ToArray());
    }

    /// <summary>The host's opaque id, as the credential port takes it.</summary>
    public string CredentialId { get; }

    /// <summary>Every name the credential is written under, provisioned names first, each once. Never empty.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <inheritdoc />
    public bool Equals(WorkloadSpecCredential? other)
        => other is not null
            && string.Equals(CredentialId, other.CredentialId, StringComparison.Ordinal)
            && Names.SequenceEqual(other.Names, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CredentialId, StringComparer.Ordinal);
        foreach (var name in Names)
        {
            hash.Add(name, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Which credential owns which name in a desired spec, computed once. Every reader that decides which
/// credential is asked for, where one is written or what an env entry carries asks this type: the Docker and
/// Kubernetes providers, Docker's container env, the env/compose/Helm artifact env, the Kubernetes artifact's Secret
/// one-liner and notes, and the artifact reveal. Composition refuses through its claim rule and conflict check.</summary>
/// <remarks>
/// <para>Claims: a provisioned credential claims its name for its id, and a secret-reference env entry carrying a
/// credential id claims its own name for that id. An entry of any other kind carrying an id, or one whose id is
/// blank, claims nothing; composition refuses a host that forces one, and a spec that reached a provider some other
/// way is read as if the id were not there.</para>
/// <para>A name the workload fills some other way is no credential's: any env entry that is not a secret reference
/// carrying an id (a literal, the spec-hash marker, a secret reference with no id) or a key of the secret material.
/// That entry or that material keeps the name, and no credential is written there or read back from it. A name
/// that occurs more than once in the env is therefore a credential's only when every entry of that name is a
/// secret reference carrying that same id; every entry is still rendered.</para>
/// <para>A name claimed by two different ids belongs to neither, and the spec is refused. Composition refuses it
/// first; this refuses a spec that reached a provider some other way.</para>
/// <para>An id left with no name is not asked for at all.</para>
/// <para>Read back: the first owned name holding a non-empty value, in <see cref="WorkloadSpecCredential.Names"/>
/// order, is what the port is handed as the existing value. An empty value counts as none.</para>
/// <para>Write: the port's one answer for an id goes under every name that id owns. A null answer writes an empty
/// value on a provider and reveals nothing in an artifact.</para>
/// <para>An env entry carries a credential exactly when <see cref="CredentialFor"/> names one: it is a secret
/// reference carrying an id that owns the entry's name. A secret-reference entry is backed when the material
/// carries its name or it carries a credential. Any other is refused, before any credential is asked for.</para>
/// </remarks>
public sealed class WorkloadSpecCredentials
{
    private readonly IReadOnlyDictionary<string, string> _owners;

    private WorkloadSpecCredentials(
        IReadOnlyList<WorkloadSpecCredential> credentials,
        IReadOnlyDictionary<string, string> owners,
        IReadOnlyList<string> conflicts,
        IReadOnlyList<string> unbacked)
    {
        Credentials = credentials;
        _owners = owners;
        Conflicts = conflicts;
        UnbackedSecretRefs = unbacked;
    }

    /// <summary>Each id that owns a name, in the order the ids first appear, provisioned ones first.</summary>
    public IReadOnlyList<WorkloadSpecCredential> Credentials { get; }

    /// <summary>Names claimed by more than one id, sorted ordinally.</summary>
    public IReadOnlyList<string> Conflicts { get; }

    /// <summary>Secret-reference env entries with nothing behind them, by name, in spec order.</summary>
    public IReadOnlyList<string> UnbackedSecretRefs { get; }

    /// <summary>Every owned name, each once: the names a provider reads back.</summary>
    public IReadOnlyList<string> Names => Credentials.SelectMany(c => c.Names).ToList();

    /// <summary>Why the spec cannot be applied, or null.</summary>
    public string? Refusal => Conflicts.Count > 0
        ? $"name(s) claimed by more than one credential: {string.Join(", ", Conflicts)}"
        : UnbackedSecretRefs.Count > 0
            ? $"secret reference(s) with no resolved material: {string.Join(", ", UnbackedSecretRefs)}"
            : null;

    /// <summary>The id of the credential <paramref name="env"/> carries, or null: non-null only for a secret
    /// reference carrying an id that owns the entry's name.</summary>
    public string? CredentialFor(ResolvedEnvVar env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return CarriedBy(_owners, env);
    }

    /// <summary>The spec's credentials, as the type's remarks lay out.</summary>
    public static WorkloadSpecCredentials Of(WorkloadDesiredSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var claims = spec.ProvisionedCredentials.Select(c => (c.CredentialId, c.Name))
            .Concat(spec.Env.Where(Claims).Select(e => (e.CredentialId!, e.Name)))
            .ToList();
        var conflicts = ConflictingNames(claims);
        var filledOtherwise = spec.Env.Where(e => !Claims(e)).Select(e => e.Name)
            .Concat(spec.SecretMaterial.Keys)
            .ToHashSet(StringComparer.Ordinal);

        var order = new List<string>();
        var names = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (credentialId, name) in claims)
        {
            if (filledOtherwise.Contains(name) || conflicts.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            if (!names.TryGetValue(credentialId, out var owned))
            {
                owned = [];
                names[credentialId] = owned;
                order.Add(credentialId);
            }

            if (owners.TryAdd(name, credentialId))
            {
                owned.Add(name);
            }
        }

        var unbacked = spec.Env
            .Where(e => e.Kind == ResolvedEnvKind.SecretRef
                && !spec.SecretMaterial.ContainsKey(e.Name)
                && CarriedBy(owners, e) is null)
            .Select(e => e.Name)
            .ToList();

        return new WorkloadSpecCredentials(
            order.Select(id => new WorkloadSpecCredential(id, names[id])).ToList(), owners, conflicts, unbacked);
    }

    private static string? CarriedBy(IReadOnlyDictionary<string, string> owners, ResolvedEnvVar env)
        => Claims(env)
            && owners.TryGetValue(env.Name, out var owner)
            && string.Equals(owner, env.CredentialId, StringComparison.Ordinal)
                ? owner
                : null;

    internal static bool Claims(ResolvedEnvVar env)
        => env is { Kind: ResolvedEnvKind.SecretRef, CredentialId: { } id } && !string.IsNullOrWhiteSpace(id);

    /// <summary>Asks the port once per id, handing it the value read back as the type's remarks lay out. An id
    /// the port answered null for is absent from the result.</summary>
    /// <param name="port">The host's credential port.</param>
    /// <param name="instanceId">The instance the credentials belong to.</param>
    /// <param name="held">The values the runtime holds, by name; empty where nothing is running.</param>
    /// <param name="cancellationToken">Cancels the asks.</param>
    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IWorkloadCredentialPort port,
        Guid instanceId,
        IReadOnlyDictionary<string, string> held,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(held);

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var credential in Credentials)
        {
            var existing = credential.Names
                .Select(name => held.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null)
                .FirstOrDefault(value => value is not null);
            if (await port.ResolveOrMintAsync(
                        instanceId, credentialId: credential.CredentialId, existingMaterial: existing, cancellationToken: cancellationToken)
                    .ConfigureAwait(false) is { } value)
            {
                resolved[credential.CredentialId] = value;
            }
        }

        return resolved;
    }

    /// <summary>A digest of the resolved credential values by id, or null when none has a value. Hashed under a
    /// domain prefix and unkeyed, so a provider can stamp it on a runtime without writing the value there.</summary>
    /// <param name="resolved">The values <see cref="ResolveAsync"/> answered, by credential id.</param>
    public static string? Digest(IReadOnlyDictionary<string, string> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var valued = resolved
            .Where(c => !string.IsNullOrEmpty(c.Value))
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .ToList();
        if (valued.Count == 0)
        {
            return null;
        }

        var buffer = new System.Text.StringBuilder("reeve-workload-credential-digest\0");
        foreach (var (id, value) in valued)
        {
            buffer.Append(id).Append('\0').Append(value).Append('\0');
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(buffer.ToString()))).ToLowerInvariant();
    }

    internal static IReadOnlyList<string> ConflictingNames(IEnumerable<(string CredentialId, string Name)> claims)
        => claims
            .GroupBy(claim => claim.Name, StringComparer.Ordinal)
            .Where(group => group.Select(claim => claim.CredentialId).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
}
