namespace Reeve.Workloads.Abstractions.Domain.DesiredSpec;

/// <summary>Maps a bundled/system workload's <c>image.component</c> slug to concrete image coordinates
/// tracking the running backend. Unset ⇒ empty catalog — any component then fail-closes at render.</summary>
public sealed class ProductImageOptions
{
    /// <summary>The bound configuration section name.</summary>
    public const string SectionName = "Workloads:ProductImages";

    /// <summary>Product-image entries keyed by component slug. Ordinal keys — the slug is a stable
    /// identifier, not culture-sensitive text.</summary>
    public IDictionary<string, ProductImageEntry> Components { get; set; }
        = new Dictionary<string, ProductImageEntry>(StringComparer.Ordinal);

    /// <summary>Tolerates the key-casing <c>IConfiguration</c> can surface (config keys are
    /// case-insensitive). Returns null when no entry exists — the caller fail-closes on that.</summary>
    public ProductImageEntry? Get(string component)
    {
        if (string.IsNullOrEmpty(component) || Components is null)
        {
            return null;
        }

        if (Components.TryGetValue(component, out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in Components)
        {
            if (string.Equals(key, component, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary><see cref="Ref"/> has NO tag — same shape as a manifest <c>image.ref</c>, so the existing
/// registry-mirror rewrite applies unchanged. Tag-only (no <see cref="Digest"/>) is permitted only under <c>allowUnpinnedImages</c>.</summary>
public sealed class ProductImageEntry
{
    /// <summary>Registry-host/repository, NO tag (e.g. <c>registry.example.com/reeve/virtual-devices</c>).</summary>
    public string? Ref { get; set; }

    /// <summary>Image tag tracking the running app version (e.g. the chart's backend/app tag). Optional.</summary>
    public string? Tag { get; set; }

    /// <summary>Optional CI-stamped <c>sha256:</c> index digest. When present the render pins by digest.</summary>
    public string? Digest { get; set; }
}
