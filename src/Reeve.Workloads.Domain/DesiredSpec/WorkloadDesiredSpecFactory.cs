using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;

using Reeve.Workloads.Domain.Network;

namespace Reeve.Workloads.Domain.DesiredSpec;

/// <summary>The pure heart of the reconciler's desired-state computation: <see cref="Compute"/> returns a fully
/// resolved, provider-agnostic spec with a stable hash. PURE — no DB, no I/O, no clock; the caller hands it all in.</summary>
public static class WorkloadDesiredSpecFactory
{
    /// <summary>Env var name for the injected SpecHash — present on EVERY workload implicitly.</summary>
    public const string SpecHashEnv = "REEVE_SPEC_HASH";

    /// <summary>Env var name for the injected app/module VERSION — the value a running workload may report to
    /// its host, which a host's certifier can compare against the update's ToVersion. EXCLUDED from the SpecHash.</summary>
    public const string AppVersionEnv = "REEVE_APP_VERSION";

    /// <summary>Reserved resolved-parameter PREFIX carrying an operator-requested UPWARD volume size override,
    /// <c>volumeSize.{volumeId}</c>. Written ONLY by the upward-only save gate; a raw parameter edit rejects it.</summary>
    public const string VolumeSizeOverrideParameterPrefix = "volumeSize.";

    /// <summary>Computes the desired spec for one workload instance. <paramref name="installationId"/> feeds the
    /// installation label; <paramref name="provider"/> is the effective active provider, and
    /// <paramref name="namer"/> is that provider's naming seam — resolved from the namer registry, never
    /// reconstructed here.</summary>
    public static WorkloadDesiredSpec Compute(
        WorkloadManifest workload,
        WorkloadOrchestratorSettings settings,
        IReadOnlyDictionary<string, string?> resolvedParameters,
        WorkloadBootstrapContext bootstrap,
        WorkloadProviderDescriptor provider,
        IWorkloadResourceNamer namer,
        Guid pluginInstanceId,
        string pluginTypeId,
        Guid instanceId,
        string installationId,
        ProductImageOptions? productImages = null)
        => ComputeWithParameterReferences(workload, settings, resolvedParameters, null, bootstrap, provider, namer,
            pluginInstanceId, pluginTypeId, instanceId, installationId, productImages);

    /// <summary>Computes a desired spec from resolved values while hashing the opaque parameter references that
    /// produced them. The resolved secret values remain only in secret material and never enter the hash.</summary>
    public static WorkloadDesiredSpec ComputeWithParameterReferences(
        WorkloadManifest workload,
        WorkloadOrchestratorSettings settings,
        IReadOnlyDictionary<string, string?> resolvedParameters,
        IReadOnlyDictionary<string, string?>? parameterReferencesForHash,
        WorkloadBootstrapContext bootstrap,
        WorkloadProviderDescriptor provider,
        IWorkloadResourceNamer namer,
        Guid pluginInstanceId,
        string pluginTypeId,
        Guid instanceId,
        string installationId,
        ProductImageOptions? productImages = null)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(resolvedParameters);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(namer);

        var inst8 = WorkloadInstance.ComputeInst8(instanceId);
        var subject = new WorkloadNamingSubject
        {
            GroupId = pluginTypeId,
            WorkloadId = workload.Id,
            Inst8 = inst8,
        };

        var image = ResolveImage(workload.Image, settings, productImages);

        var resources = ResolveResources(workload.Resources, resolvedParameters);

        var network = ResolveNetwork(workload.Network, resolvedParameters, provider);

        var userNamespaceRequested = provider.SupportsUserNamespaces
            && WorkloadParameterResolver.IsUserNamespaceRequested(resolvedParameters);
        var userNamespaceSkipped = userNamespaceRequested && network?.HostNetwork == true;

        var privileges = ResolvePrivileges(workload, provider, pluginTypeId, resolvedParameters);
        var userNamespaceSkippedForDevices = userNamespaceRequested
            && WorkloadDevicePassthrough.NeedsPrivileged(provider, privileges?.DevicePaths.Count ?? 0);

        var nodeSelector = ResolveNodeSelector(workload, resolvedParameters);

        var volumes = ResolveVolumes(workload, settings, namer, subject, resolvedParameters);

        var configMapMounts = ResolveConfigMapMounts(workload, resolvedParameters);

        var env = ResolveEnv(workload, resolvedParameters, bootstrap, out var secretMaterial);

        var unsupportedPodPatch = workload.UnsupportedPodPatch is { } patch
            ? new UnsupportedPodPatchSpec { PatchJson = patch.Body.GetRawText() }
            : null;

        var resourceName = namer.ObjectName(subject);
        var secretName = namer.SecretName(subject);

        var draft = new WorkloadDesiredSpec
        {
            GroupInstanceId = pluginInstanceId,
            GroupId = pluginTypeId,
            WorkloadId = workload.Id,
            InstanceId = instanceId,
            Inst8 = inst8,
            Provider = provider.Id,
            ResourceName = resourceName,
            SecretName = secretName,
            Labels = new Dictionary<string, string>(),
            Image = image,
            Platforms = workload.Image.Platforms,
            Kind = workload.Kind,
            NodeSelector = nodeSelector,
            Resources = resources,
            Network = network,
            Privileges = privileges,
            UserNamespace = userNamespaceRequested && !userNamespaceSkipped && !userNamespaceSkippedForDevices,
            UserNamespaceSkippedForHostNetwork = userNamespaceSkipped,
            UserNamespaceSkippedForDevicePassthrough = userNamespaceSkippedForDevices,
            Volumes = volumes,
            ConfigMapMounts = configMapMounts,
            Health = workload.Health,
            Observability = workload.Observability,
            Env = env,
            HostRequirements = workload.HostRequirements,
            UnsupportedPodPatch = unsupportedPodPatch,
            RestartNonce = bootstrap.RestartNonce,
            SecretMaterial = secretMaterial,
            ProvisionedCredentials = bootstrap.ProvisionedCredentials,
            SpecHash = string.Empty,
        };

        var specHash = SpecHasher.Compute(draft, parameterReferencesForHash);

        var finalEnv = env
            .Select(e => e.Kind == ResolvedEnvKind.SpecHash ? e with { Value = specHash } : e)
            .ToList();

        var labels = namer.Labels(new WorkloadLabelSubject
        {
            InstanceId = instanceId,
            InstallationId = installationId,
            SpecHash = specHash,
            Criticality = DominantCriticality(volumes),
        });

        return draft with
        {
            Env = finalEnv,
            Labels = labels,
            SpecHash = specHash,
        };
    }


    internal static WorkloadDesiredSpec WithSpecHash(
        WorkloadDesiredSpec spec, string specHash, IWorkloadResourceNamer namer, string installationId)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(specHash);
        ArgumentNullException.ThrowIfNull(namer);

        return spec with
        {
            Env = spec.Env
                .Select(e => e.Kind == ResolvedEnvKind.SpecHash ? e with { Value = specHash } : e)
                .ToList(),
            Labels = namer.Labels(new WorkloadLabelSubject
            {
                InstanceId = spec.InstanceId,
                InstallationId = installationId,
                SpecHash = specHash,
                Criticality = DominantCriticality(spec.Volumes),
            }),
            SpecHash = specHash,
        };
    }

    /// <summary>Resolves a product-image component slug to its concrete pull reference outside the workload render.
    /// Fail-CLOSED: a component with no configured entry throws. Delegates to <see cref="ResolveImage"/>.</summary>
    public static string ResolveComponentImage(string component, WorkloadOrchestratorSettings settings, ProductImageOptions? productImages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentNullException.ThrowIfNull(settings);
        return ResolveImage(new WorkloadImage { Component = component }, settings, productImages);
    }

    private static string ResolveImage(WorkloadImage image, WorkloadOrchestratorSettings settings, ProductImageOptions? productImages)
    {
        if (!string.IsNullOrEmpty(image.Component))
        {
            var component = image.Component;

            var entry = productImages?.Get(component);
            if (entry is null)
            {
                throw new WorkloadImageResolutionException(
                    $"image.component '{component}' has no matching product-image configuration entry " +
                    $"('{ProductImageOptions.SectionName}') — a component is resolvable only for a bundled/system product " +
                    "workload with a configured product image (fail-closed).",
                    WorkloadImageResolutionFailure.NoProductImageConfig);
            }

            if (string.IsNullOrWhiteSpace(entry.Ref))
            {
                throw new WorkloadImageResolutionException(
                    $"product-image entry for component '{component}' has no 'ref' (registry/repository) configured.",
                    WorkloadImageResolutionFailure.MissingRef);
            }

            var digest = string.IsNullOrWhiteSpace(entry.Digest) ? null : entry.Digest!.Trim();
            var rewritten = RegistryMirrorRewriter.Rewrite(entry.Ref!.Trim(), digest ?? string.Empty, settings.Mirrors);

            if (digest is not null)
            {
                return $"{rewritten}@{digest}";
            }

            if (!settings.AllowUnpinnedImages)
            {
                throw new WorkloadImageResolutionException(
                    $"product-image component '{component}' resolved to an UNPINNED (tag-only) image but " +
                    "'allowUnpinnedImages' is not enabled — configure a CI-stamped digest or enable unpinned images.",
                    WorkloadImageResolutionFailure.UnpinnedImageBlocked);
            }

            var tag = string.IsNullOrWhiteSpace(entry.Tag) ? null : entry.Tag!.Trim();
            return tag is null ? rewritten : $"{rewritten}:{tag}";
        }

        var rewrittenRef = RegistryMirrorRewriter.Rewrite(image.Ref, image.Digest, settings.Mirrors);
        return string.IsNullOrEmpty(image.Digest) ? rewrittenRef : $"{rewrittenRef}@{image.Digest}";
    }


    private static ResolvedResources? ResolveResources(
        WorkloadResources? declared, IReadOnlyDictionary<string, string?> resolved)
    {
        string? Pick(string overrideName, string? declaredValue)
            => resolved.TryGetValue(overrideName, out var v) && !string.IsNullOrWhiteSpace(v) ? v : declaredValue;

        var reqCpu = Pick("resources.requests.cpu", declared?.Requests?.Cpu);
        var reqMem = Pick("resources.requests.memory", declared?.Requests?.Memory);
        var limCpu = Pick("resources.limits.cpu", declared?.Limits?.Cpu);
        var limMem = Pick("resources.limits.memory", declared?.Limits?.Memory);

        if (reqCpu is null && reqMem is null && limCpu is null && limMem is null)
            return null;

        return new ResolvedResources
        {
            RequestsCpu = reqCpu,
            RequestsMemory = reqMem,
            LimitsCpu = limCpu,
            LimitsMemory = limMem,
        };
    }

    private static ResolvedNetwork? ResolveNetwork(
        WorkloadNetwork? net, IReadOnlyDictionary<string, string?> resolved, WorkloadProviderDescriptor provider)
    {
        if (net is null)
            return null;

        var ports = new List<ResolvedPort>();
        foreach (var p in net.Ports)
        {
            int? hostPort = null;
            if (provider.PublishesHostPorts && !string.IsNullOrEmpty(p.Name)
                && resolved.TryGetValue($"port.{p.Name}.hostPort", out var hp)
                && int.TryParse(hp, out var parsed))
            {
                hostPort = parsed;
            }
            ports.Add(new ResolvedPort { Name = p.Name, Port = p.Port, Protocol = p.Protocol, HostPort = hostPort });
        }

        var mode = ResolveNetworkMode(net, resolved);

        var attachments = mode == WorkloadNetworkMode.OperatorNetwork
            ? FlattenAttachments(net, resolved)
            : [];

        return new ResolvedNetwork
        {
            HostNetwork = mode == WorkloadNetworkMode.HostNetwork,
            Mode = mode,
            Attachments = attachments,
            Mdns = net.Mdns,
            Ipv6Required = net.Ipv6Required,
            Ports = ports,
        };
    }

    private static WorkloadNetworkMode ResolveNetworkMode(
        WorkloadNetwork net, IReadOnlyDictionary<string, string?> resolved)
    {
        var declared = net.HostNetwork ? WorkloadNetworkMode.HostNetwork : WorkloadNetworkMode.Cluster;

        return resolved.TryGetValue(WorkloadNetworkAttachmentGrammar.NetworkModeParameter, out var raw)
               && WorkloadNetworkAttachmentGrammar.ParseMode(raw?.Trim()) is { } parsed
            ? parsed
            : declared;
    }

    private static IReadOnlyList<string> FlattenAttachments(
        WorkloadNetwork net, IReadOnlyDictionary<string, string?> resolved)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Append(string parameterName)
        {
            if (!resolved.TryGetValue(parameterName, out var raw) || string.IsNullOrWhiteSpace(raw))
                return;

            foreach (var piece in raw.Split(','))
            {
                var name = piece.Trim();
                if (name.Length == 0)
                    continue;
                if (seen.Add(name))
                    ordered.Add(name);
            }
        }

        Append(WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter);
        foreach (var slot in net.Attachments)
        {
            if (!string.IsNullOrEmpty(slot.Parameter))
                Append(slot.Parameter);
        }

        return ordered;
    }

    /// <summary>Every name that can carry placement, in read order: the manifest's own NodeSelector parameters
    /// (sorted), then the reserved operator name last so an operator's placement wins a key the author also set.</summary>
    public static IReadOnlyList<string> NodeSelectorParameterNames(WorkloadManifest workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        var declared = workload.Parameters
            .Where(p => p.Type == WorkloadParameterType.NodeSelector && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (!declared.Contains(WorkloadParameterResolver.NodeSelectorParameterName, StringComparer.Ordinal))
            declared.Add(WorkloadParameterResolver.NodeSelectorParameterName);

        return declared;
    }

    private static IReadOnlyDictionary<string, string> ResolveNodeSelector(
        WorkloadManifest workload, IReadOnlyDictionary<string, string?> resolved)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in NodeSelectorParameterNames(workload))
        {
            if (!resolved.TryGetValue(name, out var raw))
                continue;

            foreach (var (key, value) in WorkloadNodeSelectorGrammar.Parse(raw))
                map[key] = value;
        }

        return map;
    }

    private static ResolvedPrivileges? ResolvePrivileges(
        WorkloadManifest workload, WorkloadProviderDescriptor provider, string pluginTypeId,
        IReadOnlyDictionary<string, string?> resolved)
    {
        var priv = workload.Privileges;
        if (priv is null)
            return null;

        var caps = priv.Capabilities.Select(CapabilityWire).ToList();

        string? dbusPath = null;
        if (priv.HostDbus)
        {
            dbusPath = resolved.TryGetValue(WorkloadPrivilegeRequirements.DbusHostPathParameter, out var v) && !string.IsNullOrWhiteSpace(v)
                ? v!.Trim()
                : WorkloadPrivilegeRequirements.DefaultDbusHostPath;
        }

        var devicePaths = new List<string>();
        var deviceGroupIds = new SortedSet<int>();
        var joinsDeviceGroups = false;
        foreach (var device in priv.Devices)
        {
            if (string.IsNullOrEmpty(device.Parameter))
                continue;

            IEnumerable<string> keys = device.MaxCount > 1
                ? Enumerable.Range(0, device.MaxCount).Select(i => WorkloadParameterResolver.DeviceIndexKey(device.Parameter, i))
                : new[] { device.Parameter };

            foreach (var key in keys)
            {
                if (!resolved.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                    continue;

                devicePaths.Add(value!.Trim());

                if (device.Group != WorkloadDeviceGroup.Auto)
                    continue;

                joinsDeviceGroups = true;

                if (resolved.TryGetValue(WorkloadParameterResolver.DeviceGroupIdKey(key), out var gidRaw)
                    && int.TryParse(gidRaw, out var gid) && gid > 0)
                {
                    deviceGroupIds.Add(gid);
                }
            }
        }

        var hostPaths = new List<ResolvedHostPath>();
        foreach (var hp in priv.HostPaths)
        {
            if (!string.IsNullOrEmpty(hp.Parameter)
                && !string.IsNullOrEmpty(hp.MountPath)
                && resolved.TryGetValue(hp.Parameter, out var hpValue)
                && !string.IsNullOrWhiteSpace(hpValue))
            {
                hostPaths.Add(new ResolvedHostPath { HostPath = hpValue!.Trim(), MountPath = hp.MountPath });
            }
        }

        ResolvedNodeLocalState? nls = null;
        if (priv.NodeLocalState is { } declared)
        {
            var root = provider.NodeStateRoot;
            var basePath = $"{TrimTrailingSlash(root)}/{pluginTypeId}/{workload.Id}";
            var hostPath = string.IsNullOrWhiteSpace(declared.Subdir)
                ? basePath
                : $"{basePath}/{declared.Subdir.Trim('/')}";
            nls = new ResolvedNodeLocalState { HostPath = hostPath, Criticality = declared.Criticality };
        }

        return new ResolvedPrivileges
        {
            Capabilities = caps,
            Privileged = priv.Privileged,
            DevicePassthroughPrivileged = !priv.Privileged
                && WorkloadDevicePassthrough.NeedsPrivileged(provider, devicePaths.Distinct().Count()),
            RunAsRoot = priv.RunAsRoot,
            FsGroup = priv.FsGroup,
            TunDevice = priv.TunDevice,
            CallsBackendRest = priv.CallsBackendRest,
            DbusHostPath = dbusPath,
            DevicePaths = devicePaths.Distinct().ToList(),
            DeviceGroupIds = deviceGroupIds.ToList(),
            JoinsDeviceGroups = joinsDeviceGroups,
            HostPaths = hostPaths,
            NodeLocalState = nls,
        };
    }

    private static IReadOnlyList<ResolvedVolume> ResolveVolumes(
        WorkloadManifest workload, WorkloadOrchestratorSettings settings, IWorkloadResourceNamer namer,
        WorkloadNamingSubject subject,
        IReadOnlyDictionary<string, string?> resolvedParameters)
    {
        var list = new List<ResolvedVolume>();
        foreach (var v in workload.Volumes)
        {
            var adopted = resolvedParameters.TryGetValue($"existingClaim.{v.Id}", out var claim)
                          && !string.IsNullOrWhiteSpace(claim);
            var resourceName = adopted
                ? claim!.Trim()
                : namer.VolumeName(subject, v.Id);

            var size = resolvedParameters.TryGetValue($"{VolumeSizeOverrideParameterPrefix}{v.Id}", out var sizeOverride)
                       && !string.IsNullOrWhiteSpace(sizeOverride)
                ? MaxStorageSize(sizeOverride!.Trim(), v.Size)
                : v.Size;

            list.Add(new ResolvedVolume
            {
                Id = v.Id,
                MountPath = v.MountPath,
                Size = size,
                Criticality = v.Criticality,
                AccessMode = v.AccessMode,
                ResourceName = resourceName,
                ExistingClaim = adopted,
            });
        }
        return list;
    }

    private static string MaxStorageSize(string @override, string declared)
    {
        var overrideOk = StorageQuantity.TryParseBytes(@override, out var overrideBytes);
        var declaredOk = StorageQuantity.TryParseBytes(declared, out var declaredBytes);

        if (overrideOk && declaredOk)
        {
            return overrideBytes >= declaredBytes ? @override : declared;
        }

        return declaredOk ? declared : @override;
    }

    private static IReadOnlyList<ResolvedConfigMapMount> ResolveConfigMapMounts(
        WorkloadManifest workload, IReadOnlyDictionary<string, string?> resolved)
    {
        var list = new List<ResolvedConfigMapMount>();
        foreach (var cm in workload.ConfigMapMounts)
        {
            if (resolved.TryGetValue(cm.Parameter, out var v) && !string.IsNullOrWhiteSpace(v))
                list.Add(new ResolvedConfigMapMount { MountPath = cm.MountPath, ConfigMapName = v.Trim() });
        }
        return list;
    }

    private static bool IsPasswordParameter(WorkloadManifest workload, string parameterName)
        => workload.Parameters.Any(p =>
            p.Type == WorkloadParameterType.Password
            && string.Equals(p.Name, parameterName, StringComparison.Ordinal));

    private static IReadOnlyList<ResolvedEnvVar> ResolveEnv(
        WorkloadManifest workload,
        IReadOnlyDictionary<string, string?> resolved,
        WorkloadBootstrapContext bootstrap,
        out IReadOnlyDictionary<string, string> secretMaterial)
    {
        var env = new List<ResolvedEnvVar>();
        var material = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var e in workload.Env)
        {
            if (string.IsNullOrEmpty(e.Name) || string.IsNullOrEmpty(e.From))
                continue;

            if (e.From.StartsWith("parameter:", StringComparison.Ordinal))
            {
                var name = e.From["parameter:".Length..];
                resolved.TryGetValue(name, out var value);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (IsPasswordParameter(workload, name))
                {
                    env.Add(new ResolvedEnvVar { Name = e.Name, Kind = ResolvedEnvKind.SecretRef });
                    material[e.Name] = value;
                }
                else
                {
                    env.Add(new ResolvedEnvVar { Name = e.Name, Kind = ResolvedEnvKind.Literal, Value = value });
                }
            }
            else if (e.From.StartsWith("literal:", StringComparison.Ordinal))
            {
                env.Add(new ResolvedEnvVar { Name = e.Name, Kind = ResolvedEnvKind.Literal, Value = e.From["literal:".Length..] });
            }
            else if (e.From.StartsWith("bootstrap:", StringComparison.Ordinal))
            {
                var key = e.From["bootstrap:".Length..];
                var value = bootstrap.Values.TryGetValue(key, out var resolvedValue)
                    ? resolvedValue
                    : BootstrapValue.Literal(null);

                if (value.CredentialId is { } credentialId)
                {
                    env.Add(new ResolvedEnvVar
                    {
                        Name = e.Name,
                        Kind = ResolvedEnvKind.SecretRef,
                        SecretRef = string.IsNullOrEmpty(bootstrap.SecretName) ? null : bootstrap.SecretName,
                        CredentialId = credentialId,
                    });
                    continue;
                }

                if (value.Kind == ResolvedEnvKind.SecretRef
                    && string.IsNullOrEmpty(value.SecretRefName)
                    && string.IsNullOrEmpty(value.SecretMaterial))
                {
                    continue;
                }

                env.Add(value.Kind switch
                {
                    ResolvedEnvKind.SecretRef => new ResolvedEnvVar { Name = e.Name, Kind = ResolvedEnvKind.SecretRef, SecretRef = value.SecretRefName },
                    ResolvedEnvKind.SpecHash => new ResolvedEnvVar { Name = e.Name, Kind = ResolvedEnvKind.SpecHash },
                    _ => new ResolvedEnvVar
                    {
                        Name = e.Name,
                        Kind = ResolvedEnvKind.Literal,
                        Value = value.Value,
                        ExcludedFromSpecHash = value.ExcludedFromSpecHash,
                    },
                });

                if (value.SecretMaterial is { Length: > 0 } secretMaterialValue)
                {
                    material[e.Name] = secretMaterialValue;
                }
            }
        }

        var hasSpecHashEnv = false;
        for (var i = 0; i < env.Count; i++)
        {
            if (!string.Equals(env[i].Name, SpecHashEnv, StringComparison.Ordinal))
                continue;

            hasSpecHashEnv = true;
            env[i] = new ResolvedEnvVar { Name = SpecHashEnv, Kind = ResolvedEnvKind.SpecHash };
        }

        if (!hasSpecHashEnv)
        {
            env.Add(new ResolvedEnvVar { Name = SpecHashEnv, Kind = ResolvedEnvKind.SpecHash });
        }

        foreach (var forced in bootstrap.HostEnv)
        {
            var replaced = false;
            for (var i = 0; i < env.Count; i++)
            {
                if (!string.Equals(env[i].Name, forced.Name, StringComparison.Ordinal))
                    continue;

                env[i] = forced;
                replaced = true;
            }

            if (replaced)
            {
                material.Remove(forced.Name);
            }
            else
            {
                env.Add(forced);
            }
        }

        var appVersion = string.IsNullOrWhiteSpace(bootstrap.AppVersion) ? null : bootstrap.AppVersion!.Trim();
        var hasAppVersionEnv = false;
        for (var i = 0; i < env.Count; i++)
        {
            if (!string.Equals(env[i].Name, AppVersionEnv, StringComparison.Ordinal))
                continue;

            hasAppVersionEnv = true;
            if (appVersion is not null)
                env[i] = new ResolvedEnvVar { Name = AppVersionEnv, Kind = ResolvedEnvKind.Literal, Value = appVersion };
        }

        if (!hasAppVersionEnv && appVersion is not null)
        {
            env.Add(new ResolvedEnvVar { Name = AppVersionEnv, Kind = ResolvedEnvKind.Literal, Value = appVersion });
        }

        secretMaterial = material;
        return env;
    }

    private static string? DominantCriticality(IReadOnlyList<ResolvedVolume> volumes)
    {
        if (volumes.Count == 0)
            return null;
        var worst = volumes.Max(v => v.Criticality);
        return worst switch
        {
            VolumeCriticality.Cache => "cache",
            VolumeCriticality.Recoverable => "recoverable",
            VolumeCriticality.Critical => "critical",
            _ => null,
        };
    }

    private static string TrimTrailingSlash(string s) => s.Length > 1 ? s.TrimEnd('/') : s;

    private static string CapabilityWire(WorkloadCapability capability) => capability switch
    {
        WorkloadCapability.NetAdmin => "NET_ADMIN",
        WorkloadCapability.NetRaw => "NET_RAW",
        WorkloadCapability.SysAdmin => "SYS_ADMIN",
        WorkloadCapability.NetBindService => "NET_BIND_SERVICE",
        _ => capability.ToString(),
    };
}
