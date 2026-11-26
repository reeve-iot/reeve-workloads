namespace Reeve.Workloads.Abstractions.Domain.DesiredSpec;

/// <summary>The already-resolved bootstrap inputs the desired-spec factory expands into env values. An INPUT
/// record: the caller does the I/O so <c>WorkloadDesiredSpecFactory</c> stays pure.</summary>
public sealed record WorkloadBootstrapContext
{
    /// <summary>Every resolved <c>bootstrap:&lt;key&gt;</c>, keyed by the key itself: the host's, plus the library's
    /// <c>instanceId</c> and <c>specHash</c>. An unlisted key renders a null literal, exactly as an unresolvable one
    /// always did.</summary>
    public IReadOnlyDictionary<string, BootstrapValue> Values { get; init; } =
        new Dictionary<string, BootstrapValue>(StringComparer.Ordinal);

    /// <summary>The instance GUID, the same value that becomes the plugin instance's <c>AdapterInstanceId</c>.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>The instance's operator-restart nonce, excluded from the SpecHash. Defaults to 0 for render paths
    /// carrying no restart intent, such as previews and artifacts.</summary>
    public long RestartNonce { get; init; }

    /// <summary>The version this spec deploys, injected as the reserved <c>REEVE_APP_VERSION</c> env so the workload
    /// can report it to the gate. Excluded from the SpecHash; null or blank omits the env and the workload's own
    /// default applies.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Env the HOST forces onto this workload from its own extension namespace, applied over any the
    /// author declared under the same name, whose secret material it drops. Hashed like every other env unless
    /// flagged <see cref="ResolvedEnvVar.ExcludedFromSpecHash"/>; the library reads none of the names.</summary>
    public IReadOnlyList<ResolvedEnvVar> HostEnv { get; init; } = [];

    /// <summary>The per-instance secret a <see cref="BootstrapValue.HostCredential"/> env references, named by the
    /// effective provider. Blank where no instance exists to name one for, such as a preview; a credential env
    /// then renders with no ref name rather than being dropped.</summary>
    public required string SecretName { get; init; }

    /// <summary>Credentials the host provisions outside the env, carried onto the spec for the provider to write
    /// at apply. Outside the hash.</summary>
    public IReadOnlyList<ProvisionedCredential> ProvisionedCredentials { get; init; } = [];
}

/// <summary>A credential the provider writes at apply under <paramref name="Name"/>, whether or not an env
/// declares it. The id is the host's own; the provider resolves the value through the credential port.</summary>
/// <param name="CredentialId">The host's opaque id for the credential.</param>
/// <param name="Name">The name the provider writes it under: a container env or a key of the per-instance secret.</param>
/// <remarks>Which names a credential owns when an env or the material fills the same one is laid out on
/// <see cref="WorkloadSpecCredentials"/>.</remarks>
public sealed record ProvisionedCredential(string CredentialId, string Name)
{
    /// <summary>The host's opaque id for the credential; never blank, as with <see cref="BootstrapValue.HostCredential"/>,
    /// through the constructor or a <c>with</c>.</summary>
    public string CredentialId { get; init => field = NotBlank(value, nameof(CredentialId)); } = NotBlank(CredentialId, nameof(CredentialId));

    /// <summary>The name the provider writes it under; never blank, through the constructor or a <c>with</c>.</summary>
    public string Name { get; init => field = NotBlank(value, nameof(Name)); } = NotBlank(Name, nameof(Name));

    private static string NotBlank(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

/// <summary>What one bootstrap key resolved to. A literal, a reference into the per-instance Secret, or the
/// self-referential spec-hash marker — the three shapes an env can take, and nothing about what the key means.</summary>
public sealed record BootstrapValue
{
    private BootstrapValue(ResolvedEnvKind kind) => Kind = kind;

    /// <summary>Which of the three shapes this is.</summary>
    public ResolvedEnvKind Kind { get; }

    /// <summary>The literal value, when <see cref="Kind"/> is <see cref="ResolvedEnvKind.Literal"/>.</summary>
    public string? Value { get; private init; }

    /// <summary>The Secret's reference NAME, hashed in place of the value so a rotation never moves the SpecHash.</summary>
    public string? SecretRefName { get; private init; }

    /// <summary>The secret's REAL value, written into the per-instance Secret at apply. Never a pod env, never
    /// hashed.</summary>
    public string? SecretMaterial { get; private init; }

    /// <summary>The host's id for the credential behind a <see cref="HostCredential"/>; null for every other value.</summary>
    public string? CredentialId { get; private init; }

    internal bool ExcludedFromSpecHash { get; private init; }

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Kind = ").Append(Kind)
            .Append(", Value = ").Append(Value)
            .Append(", SecretRefName = ").Append(SecretRefName)
            .Append(", SecretMaterial = ").Append(SecretMaterial)
            .Append(", CredentialId = ").Append(CredentialId);
        if (ExcludedFromSpecHash)
        {
            builder.Append(", ExcludedFromSpecHash = ").Append(true);
        }

        return true;
    }

    /// <summary>A plain value. Null renders the env with no value, which is what an unresolved key has always done.</summary>
    public static BootstrapValue Literal(string? value)
        => new(ResolvedEnvKind.Literal) { Value = value };

    /// <summary>A plain value the SpecHash does not see: the env renders exactly as a <see cref="Literal"/> would,
    /// but its name and value are left out of the hash, so changing it never reads as drift and rolls nothing out.</summary>
    /// <remarks>Its <see cref="Kind"/> is <see cref="ResolvedEnvKind.Literal"/>. The value reaches a workload at its
    /// next apply, which replaces the container or pod, so a host that needs a change delivered must redeploy.</remarks>
    public static BootstrapValue UnhashedLiteral(string? value)
        => new(ResolvedEnvKind.Literal) { Value = value, ExcludedFromSpecHash = true };

    /// <summary>A reference into the per-instance Secret, optionally carrying the material to write there.</summary>
    public static BootstrapValue SecretRef(string? secretRefName, string? material = null)
        => new(ResolvedEnvKind.SecretRef) { SecretRefName = secretRefName, SecretMaterial = material };

    /// <summary>A credential the host issues, named by its own opaque id. Renders a SecretRef to the per-instance
    /// secret; the provider resolves the value through IWorkloadCredentialPort at apply.</summary>
    /// <remarks>Always rendered, even with no ref name: the provider backs it by id, so nothing is left dangling.</remarks>
    public static BootstrapValue HostCredential(string credentialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        return new(ResolvedEnvKind.SecretRef) { CredentialId = credentialId };
    }

    /// <summary>The self-referential marker: the value is filled in after the hash it feeds is computed.</summary>
    public static readonly BootstrapValue SpecHash = new(ResolvedEnvKind.SpecHash);
}
