using System.Collections.Frozen;
using System.Text.Json;

namespace Reeve.Workloads.Abstractions.Contracts.Deployment;

/// <summary>A host's claim on one manifest extension namespace. The parser carries every namespace verbatim and
/// judges none of them; a registered validator is the only thing that can say a host's own half is malformed.</summary>
public interface IWorkloadExtensionValidator
{
    /// <summary>The <c>extensions</c> key this host owns (e.g. <c>reeve</c>). One validator per namespace.</summary>
    string Namespace { get; }

    /// <summary>Whether every workload must address this host. Required is how a MISSPELLED key becomes an error
    /// instead of a silently dropped contract — the open namespace cannot tell a typo from a foreign host.</summary>
    bool Required { get; }

    /// <summary>The <c>bootstrap:&lt;key&gt;</c> keys this host resolves. A manifest may reference these and the
    /// library's own (<c>instanceId</c>, <c>specHash</c>); any other key is <c>UnknownBootstrapKey</c>.</summary>
    /// <remarks>Never null — a host resolving none returns an empty set, which is the default — and never one of the
    /// library's own keys. The parser throws <see cref="ArgumentException"/> for either: it is a host's programming
    /// error, not a manifest's. Two hosts may declare the same key.</remarks>
    IReadOnlySet<string> BootstrapKeys => FrozenSet<string>.Empty;

    /// <summary>Validates this host's object. The whole <paramref name="workload"/> comes along because a host
    /// rule can correlate with a library field it does not own — <c>health.mode</c> is the standing example.</summary>
    void Validate(
        JsonElement workload, JsonElement extension, string path, ICollection<DeploymentValidationError> errors);
}
