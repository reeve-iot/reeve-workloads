using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain.Network;

namespace Reeve.Workloads.Domain.DesiredSpec;

/// <summary>Pure resolution of a workload's install-time parameters into a name→value map (feeds both the
/// desired spec and WorkloadPrivilegeRequirements). Resolved path and network values are re-validated here, fail-closed, against the OPERATOR's input — manifest parsing only ever checks the author's declared default.</summary>
public static class WorkloadParameterResolver
{
    /// <summary>Resolves a workload's parameters against operator values for the given provider, returning
    /// the fully-resolved set (declared + reserved). A value may be null for an optional parameter with no default and no operator value.</summary>
    public static IReadOnlyDictionary<string, string?> Resolve(
        WorkloadManifest workload,
        IReadOnlyDictionary<string, string?> operatorValues,
        WorkloadProviderDescriptor provider)
        => ResolveCore(workload, operatorValues, provider, enforcePolicies: true);

    internal static IReadOnlyDictionary<string, string?> ResolveForStoredSpec(
        WorkloadManifest workload,
        IReadOnlyDictionary<string, string?> operatorValues,
        WorkloadProviderDescriptor provider)
        => ResolveCore(workload, operatorValues, provider, enforcePolicies: false);

    private static IReadOnlyDictionary<string, string?> ResolveCore(
        WorkloadManifest workload,
        IReadOnlyDictionary<string, string?> operatorValues,
        WorkloadProviderDescriptor provider,
        bool enforcePolicies)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(operatorValues);

        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);

        var declaredNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in workload.Parameters)
        {
            if (string.IsNullOrEmpty(p.Name))
                continue;
            declaredNames.Add(p.Name);
            resolved[p.Name] = ResolveOne(p.Name, p.Default, operatorValues);
        }

        foreach (var (name, _) in WorkloadReservedParameters.For(workload, provider))
        {
            if (!declaredNames.Contains(name))
                resolved[name] = ResolveOne(name, WorkloadReservedParameters.SynthesizedDefault(workload, name), operatorValues);
        }

        if (workload.Privileges is { } priv)
        {
            foreach (var device in priv.Devices)
            {
                if (string.IsNullOrEmpty(device.Parameter) || device.MaxCount <= 1)
                    continue;

                var declaredDefault = workload.Parameters
                    .FirstOrDefault(p => string.Equals(p.Name, device.Parameter, StringComparison.Ordinal))?.Default;

                for (var i = 0; i < device.MaxCount; i++)
                {
                    var key = DeviceIndexKey(device.Parameter, i);
                    resolved[key] = ResolveOne(key, i == 0 ? declaredDefault : null, operatorValues);
                }
            }
        }

        if (workload.Privileges is { } gidPriv)
        {
            foreach (var device in gidPriv.Devices)
            {
                if (string.IsNullOrEmpty(device.Parameter) || device.Group != WorkloadDeviceGroup.Auto)
                    continue;

                IEnumerable<string> keys = device.MaxCount > 1
                    ? Enumerable.Range(0, device.MaxCount).Select(i => DeviceIndexKey(device.Parameter, i))
                    : new[] { device.Parameter };

                foreach (var key in keys)
                {
                    var gidKey = DeviceGroupIdKey(key);
                    resolved[gidKey] = ResolveOne(gidKey, defaultValue: null, operatorValues);
                }
            }
        }

        if (enforcePolicies)
        {
            EnforcePathPolicies(workload, resolved);

            EnforceNetworkAttachmentGrammar(workload, resolved, provider);
            EnforceUserNamespace(resolved, provider);
            EnforceHostPorts(workload, resolved);
            EnforceConfigMapNames(workload, resolved);
        }

        return resolved;
    }

    /// <summary>The synthesized resolution key for index <paramref name="index"/> of a multi-device slot.</summary>
    public static string DeviceIndexKey(string parameter, int index) => $"{parameter}[{index}]";

    /// <summary>The reserved key carrying a resolved device key's owning group id. Synthesized for every key a
    /// group:auto slot can bind; only a server-side writer fills it, since the accept-list refuses a submitted ':' name.</summary>
    public static string DeviceGroupIdKey(string deviceKey) => $"{deviceKey}:gid";

    /// <summary>The reserved node-placement name, always available like the resource overrides: which node runs a
    /// workload is the operator's call, not something an author grants. A declared NodeSelector param still works.</summary>
    public const string NodeSelectorParameterName = "nodeSelector";

    /// <summary>The reserved user-namespace switch, synthesized only where the provider supports it.</summary>
    public const string UserNamespaceParameterName = "security.userNamespace";

    /// <summary>True when the resolved map turns the user namespace on.</summary>
    public static bool IsUserNamespaceRequested(IReadOnlyDictionary<string, string?> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return resolved.TryGetValue(UserNamespaceParameterName, out var raw) && bool.TryParse(raw, out var on) && on;
    }

    /// <summary>Drops a submitted switch the provider cannot render, unless the manifest declares the name.</summary>
    public static IReadOnlyDictionary<string, string?> WithoutUnsupportedUserNamespace(
        IReadOnlyDictionary<string, string?> values, WorkloadManifest workload, WorkloadProviderDescriptor provider)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(provider);

        if (provider.SupportsUserNamespaces || !values.ContainsKey(UserNamespaceParameterName)
            || workload.Parameters.Any(p => string.Equals(p.Name, UserNamespaceParameterName, StringComparison.Ordinal)))
            return values;
        var kept = new Dictionary<string, string?>(values, StringComparer.Ordinal);
        kept.Remove(UserNamespaceParameterName);
        return kept;
    }

    /// <summary>The always-available operator resource-override reserved names.</summary>
    public static readonly IReadOnlyList<string> ResourceOverrideParameterNames =
    [
        "resources.requests.cpu", "resources.requests.memory",
        "resources.limits.cpu", "resources.limits.memory",
    ];

    private static string? ResolveOne(string name, string? defaultValue, IReadOnlyDictionary<string, string?> operatorValues)
    {
        if (operatorValues.TryGetValue(name, out var opValue) && !string.IsNullOrWhiteSpace(opValue))
            return opValue;
        return defaultValue;
    }

    private static void EnforcePathPolicies(WorkloadManifest workload, Dictionary<string, string?> resolved)
    {
        var priv = workload.Privileges;
        if (priv is null)
            return;

        foreach (var device in priv.Devices)
        {
            if (string.IsNullOrEmpty(device.Parameter))
                continue;

            var keys = new List<string> { device.Parameter };
            for (var i = 0; i < device.MaxCount; i++)
                keys.Add(DeviceIndexKey(device.Parameter, i));

            foreach (var key in keys)
            {
                if (resolved.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                    && !WorkloadHostPathPolicy.IsAllowedDevicePath(value.Trim()))
                {
                    throw new WorkloadParameterResolutionException(
                        key, $"device path '{value}' must be under /dev/** (a resolved value cannot widen the mount).");
                }
            }
        }

        foreach (var hp in priv.HostPaths)
        {
            if (string.IsNullOrEmpty(hp.Parameter))
                continue;
            if (resolved.TryGetValue(hp.Parameter, out var hpValue) && !string.IsNullOrWhiteSpace(hpValue))
            {
                var allowed = WorkloadHostPathPolicy.IsAllowedHostInspectPath(hpValue.Trim());
                if (!allowed)
                    throw new WorkloadParameterResolutionException(
                        hp.Parameter,
                        $"host path '{hpValue}' must be under /sys/**, /proc/**, /run/**, /var/run/**, /dev/serial, or /dev/dri, {WorkloadHostPathRefusal.Display} (a resolved value cannot widen the mount).");
            }
        }

        if (priv.HostDbus
            && resolved.TryGetValue(WorkloadPrivilegeRequirements.DbusHostPathParameter, out var dbus)
            && !string.IsNullOrWhiteSpace(dbus))
        {
            if (!WorkloadHostPathPolicy.IsAllowedDbusPath(dbus.Trim()))
                throw new WorkloadParameterResolutionException(
                    WorkloadPrivilegeRequirements.DbusHostPathParameter,
                    $"dbus host path '{dbus}' must be under /run/** or /var/run/**, {WorkloadHostPathRefusal.Display} (a resolved value cannot widen the mount).");
        }
    }

    private static void EnforceNetworkAttachmentGrammar(
        WorkloadManifest workload,
        Dictionary<string, string?> resolved,
        WorkloadProviderDescriptor provider)
    {
        if (workload.Network is not { } net || !(net.HostNetwork || net.Attachments.Count > 0))
            return;

        resolved.TryGetValue(WorkloadNetworkAttachmentGrammar.NetworkModeParameter, out var rawMode);
        WorkloadNetworkMode mode;
        if (string.IsNullOrWhiteSpace(rawMode))
        {
            mode = net.HostNetwork ? WorkloadNetworkMode.HostNetwork : WorkloadNetworkMode.Cluster;
        }
        else if (WorkloadNetworkAttachmentGrammar.ParseMode(rawMode) is { } parsed)
        {
            mode = parsed;
        }
        else
        {
            throw new WorkloadParameterResolutionException(
                WorkloadNetworkAttachmentGrammar.NetworkModeParameter,
                $"network mode '{rawMode}' is not one of "
                + $"'{WorkloadNetworkAttachmentGrammar.ClusterMode}', "
                + $"'{WorkloadNetworkAttachmentGrammar.HostNetworkMode}', "
                + $"'{WorkloadNetworkAttachmentGrammar.OperatorNetworkMode}' "
                + "(the wire spellings are case-sensitive).");
        }

        resolved.TryGetValue(WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter, out var rawAttachments);

        var normalized = WorkloadNetworkAttachmentGrammar.NormalizeForMode(mode, rawAttachments, out _);
        resolved[WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter] = normalized;

        var strict = provider.RequiresRfc1123AttachmentNames;
        if (!WorkloadNetworkAttachmentGrammar.IsValidValue(normalized, strict))
        {
            throw new WorkloadParameterResolutionException(
                WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter,
                $"network attachment value '{normalized}' must be a comma-separated list of at most "
                + $"{WorkloadNetworkAttachmentGrammar.MaxAttachments} distinct non-empty network names, each matching "
                + $"'{WorkloadNetworkAttachmentGrammar.NamePatternDisplay}'"
                + (strict
                    ? $" and, on {provider.Label}, additionally an RFC-1123 DNS subdomain "
                      + $"('{WorkloadNetworkAttachmentGrammar.Rfc1123SubdomainPatternDisplay}') naming a NetworkAttachmentDefinition."
                    : "."));
        }

        if (mode == WorkloadNetworkMode.OperatorNetwork && string.IsNullOrWhiteSpace(normalized))
        {
            throw new WorkloadParameterResolutionException(
                WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter,
                $"network mode '{WorkloadNetworkAttachmentGrammar.OperatorNetworkMode}' requires at least one "
                + $"network attachment name, but the resolved value is '{normalized}'.");
        }
    }

    private static void EnforceUserNamespace(Dictionary<string, string?> resolved, WorkloadProviderDescriptor provider)
    {
        if (provider.SupportsUserNamespaces
            && resolved.TryGetValue(UserNamespaceParameterName, out var raw)
            && !string.IsNullOrWhiteSpace(raw)
            && !bool.TryParse(raw, out _))
            throw new WorkloadParameterResolutionException(UserNamespaceParameterName, $"value '{raw}' is not 'true' or 'false'.");
    }

    private static void EnforceHostPorts(WorkloadManifest workload, Dictionary<string, string?> resolved)
    {
        foreach (var port in workload.Network?.Ports ?? [])
        {
            if (string.IsNullOrEmpty(port.Name))
                continue;
            var name = WorkloadReservedParameters.HostPortParameter(port.Name);
            if (resolved.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw)
                && !(int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var hostPort)
                     && hostPort is >= 1 and <= 65535))
            {
                throw new WorkloadParameterResolutionException(name, $"host port '{raw}' must be a whole number from 1 to 65535.");
            }
        }
    }

    private static void EnforceConfigMapNames(WorkloadManifest workload, Dictionary<string, string?> resolved)
    {
        foreach (var mount in workload.ConfigMapMounts)
        {
            if (string.IsNullOrEmpty(mount.Parameter))
                continue;
            if (resolved.TryGetValue(mount.Parameter, out var raw) && !string.IsNullOrWhiteSpace(raw)
                && !WorkloadNetworkAttachmentGrammar.IsRfc1123Subdomain(raw.Trim()))
            {
                throw new WorkloadParameterResolutionException(
                    mount.Parameter,
                    $"ConfigMap name '{raw}' must be a lowercase RFC 1123 DNS subdomain "
                    + $"('{WorkloadNetworkAttachmentGrammar.Rfc1123SubdomainPatternDisplay}', at most "
                    + $"{WorkloadNetworkAttachmentGrammar.MaxRfc1123SubdomainLength} characters).");
            }
        }
    }
}

/// <summary>Thrown when a resolved parameter value violates its path-prefix or network-value security
/// policy. Fail-closed: the desired spec is never materialized from a policy-violating resolution.</summary>
public class WorkloadParameterResolutionException : Exception
{
    public WorkloadParameterResolutionException(string parameterName, string message)
        : base($"Parameter '{parameterName}': {message}")
    {
        ParameterName = parameterName;
    }

    /// <summary>The offending parameter's name.</summary>
    public string ParameterName { get; }
}
