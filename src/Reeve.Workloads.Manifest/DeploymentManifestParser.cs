using System.Text.Json;
using System.Text.RegularExpressions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Manifest;

/// <summary>Strict parser and validator for the manifest <c>deployment</c> object: maps a <see cref="JsonElement"/> to a
/// typed <see cref="DeploymentManifest"/>, collecting every violation instead of throwing at the first — a non-empty <c>errors</c> list means reject, even when a model is still returned.</summary>
public static class DeploymentManifestParser
{
    private const int SupportedSchemaVersion = 1;

    private const int DefaultHeartbeatSeconds = 30;

    private static readonly Regex SlugPattern =
        new("^[a-z0-9-]{1,40}\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DigestPattern =
        new("^sha256:[0-9a-f]{64}\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PortNamePattern =
        new("^(?!.*--)(?=.*[a-z])[a-z0-9](?:[a-z0-9-]{0,13}[a-z0-9])?\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EnvNamePattern =
        new(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ImageRefPattern =
        new("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,254}\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JsonPointerPattern =
        new("^(?:/(?:[^/~]|~0|~1)*)*\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> LibraryBootstrapKeys = new(StringComparer.Ordinal) { "instanceId", "specHash" };

    private static readonly HashSet<string> JsonPatchOps = new(StringComparer.Ordinal)
    {
        "add", "remove", "replace", "move", "copy", "test",
    };

    private static readonly HashSet<string> DeploymentKeys = new(StringComparer.Ordinal) { "schemaVersion", "workloads" };
    private static readonly HashSet<string> WorkloadKeys = new(StringComparer.Ordinal)
    {
        "id", "displayName", "description", "image", "kind", "resources", "network", "privileges", "volumes",
        "configMapMount", "health", "extensions", "hostRequirements", "observability", "env", "parameters",
        "unsupportedPodPatch",
        "protocol", "apiSchema",
    };
    private static readonly HashSet<string> ImageKeys = new(StringComparer.Ordinal) { "ref", "digest", "tag", "platforms", "component" };
    private static readonly HashSet<string> ResourcesKeys = new(StringComparer.Ordinal) { "requests", "limits" };
    private static readonly HashSet<string> QuantityKeys = new(StringComparer.Ordinal) { "cpu", "memory" };
    private static readonly HashSet<string> NetworkKeys = new(StringComparer.Ordinal) { "hostNetwork", "mdns", "ipv6Required", "ports", "attachments" };
    private static readonly HashSet<string> PortKeys = new(StringComparer.Ordinal) { "name", "port", "protocol" };
    private static readonly HashSet<string> NetworkAttachmentSlotKeys = new(StringComparer.Ordinal) { "parameter", "description" };
    private static readonly HashSet<string> PrivilegesKeys = new(StringComparer.Ordinal)
    {
        "capabilities", "privileged", "runAsRoot", "fsGroup", "hostDbus", "tunDevice", "callsBackendRest", "devices", "hostPaths", "nodeLocalState",
    };
    private static readonly HashSet<string> DeviceKeys =
        new(StringComparer.Ordinal) { "parameter", "maxCount", "deviceKind", "group" };
    private static readonly HashSet<string> HostPathKeys = new(StringComparer.Ordinal) { "parameter", "mountPath", "description" };
    private static readonly HashSet<string> NodeLocalStateKeys = new(StringComparer.Ordinal) { "subdir", "criticality", "description" };
    private static readonly HashSet<string> VolumeKeys = new(StringComparer.Ordinal) { "id", "mountPath", "size", "criticality", "description", "accessMode" };
    private static readonly HashSet<string> ConfigMapMountKeys = new(StringComparer.Ordinal) { "mountPath", "parameter" };
    private static readonly HashSet<string> HealthKeys =
        new(StringComparer.Ordinal) { "mode", "heartbeatSeconds", "http", "adapterType" };

    private static readonly (string Field, string Parent, string Fate)[] EvictedFields =
    {
        ("protocol", "", "it belongs in a host extension (extensions.<namespace>)"),
        ("apiSchema", "", "it belongs in a host extension (extensions.<namespace>)"),
        ("adapterType", "health", "nothing reads it"),
    };
    private static readonly HashSet<string> HttpProbeKeys = new(StringComparer.Ordinal) { "port", "livenessPath", "readinessPath" };
    private static readonly HashSet<string> HostRequirementKeys = new(StringComparer.Ordinal) { "kind", "name", "description" };
    private static readonly HashSet<string> ObservabilityKeys = new(StringComparer.Ordinal) { "metrics" };
    private static readonly HashSet<string> MetricsKeys = new(StringComparer.Ordinal) { "port", "path" };
    private static readonly HashSet<string> EnvKeys = new(StringComparer.Ordinal) { "name", "from" };
    private static readonly HashSet<string> ParameterKeys = new(StringComparer.Ordinal)
    {
        "name", "type", "displayName", "description", "required", "default", "enumValues", "hint", "section",
        "widget", "enumLabels", "reveals",
    };
    private static readonly HashSet<string> JsonPatchOpKeys = new(StringComparer.Ordinal) { "op", "path", "from", "value" };

    /// <summary>Parses <paramref name="deployment"/> into a typed <see cref="DeploymentManifest"/>. Returns null only
    /// when the element isn't a JSON object; otherwise returns a best-effort model with problems reported via <paramref name="errors"/>.</summary>
    /// <exception cref="ArgumentException">An entry of <paramref name="extensionValidators"/> is null, returns null
    /// <see cref="IWorkloadExtensionValidator.BootstrapKeys"/>, or declares one of the library's own bootstrap keys.</exception>
    public static DeploymentManifest? Parse(
        JsonElement deployment,
        IReadOnlyList<IWorkloadExtensionValidator> extensionValidators,
        out IReadOnlyList<DeploymentValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(extensionValidators);
        var bootstrapKeys = CollectBootstrapKeys(extensionValidators);
        var errs = new List<DeploymentValidationError>();
        errors = errs;

        if (deployment.ValueKind != JsonValueKind.Object)
        {
            errs.Add(new(
                "deployment",
                $"The 'deployment' value must be a JSON object but was {deployment.ValueKind}.",
                DeploymentValidationCodes.NotAnObject));
            return null;
        }

        RejectUnknownKeys(deployment, "deployment", DeploymentKeys, errs);

        var schemaVersion = ReadRequiredInt(deployment, "schemaVersion", "deployment.schemaVersion", errs);
        if (schemaVersion is not null && schemaVersion.Value != SupportedSchemaVersion)
        {
            errs.Add(new(
                "deployment.schemaVersion",
                $"Unsupported schemaVersion {schemaVersion.Value}; this build understands schemaVersion {SupportedSchemaVersion} only.",
                DeploymentValidationCodes.UnsupportedSchemaVersion));
        }

        var workloads = new List<WorkloadManifest>();
        var workloadElements = new List<JsonElement>();
        if (!deployment.TryGetProperty("workloads", out var workloadsEl))
        {
            errs.Add(new("deployment.workloads", "Required field 'workloads' is missing.", DeploymentValidationCodes.MissingField));
        }
        else if (workloadsEl.ValueKind != JsonValueKind.Array)
        {
            errs.Add(new(
                "deployment.workloads",
                $"Field 'workloads' must be an array but was {workloadsEl.ValueKind}.",
                DeploymentValidationCodes.WrongType));
        }
        else
        {
            var i = 0;
            foreach (var wl in workloadsEl.EnumerateArray())
            {
                workloads.Add(ParseWorkload(wl, $"deployment.workloads[{i}]", errs, extensionValidators));
                workloadElements.Add(wl);
                i++;
            }

            if (i == 0)
                errs.Add(new("deployment.workloads", "A 'deployment' object must declare at least one workload.", DeploymentValidationCodes.NoWorkloads));
        }

        ValidateWorkloadIdUniqueness(workloads, errs);
        var siblingIds = workloads.Select(w => w.Id).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < workloads.Count; i++)
            ValidateWorkloadSemantics(workloads[i], workloadElements[i], $"deployment.workloads[{i}]", siblingIds, bootstrapKeys, errs);

        return new DeploymentManifest
        {
            SchemaVersion = schemaVersion ?? 0,
            Workloads = workloads,
        };
    }

    private static HashSet<string> CollectBootstrapKeys(IReadOnlyList<IWorkloadExtensionValidator> extensionValidators)
    {
        var keys = new HashSet<string>(LibraryBootstrapKeys, StringComparer.Ordinal);
        for (var i = 0; i < extensionValidators.Count; i++)
        {
            var validator = extensionValidators[i]
                ?? throw new ArgumentException($"Extension validator [{i}] is null.", nameof(extensionValidators));
            var declared = validator.BootstrapKeys
                ?? throw new ArgumentException(
                    $"Extension validator '{validator.Namespace}' returned null BootstrapKeys; a host resolving none returns an empty set.",
                    nameof(extensionValidators));
            foreach (var key in declared)
            {
                if (LibraryBootstrapKeys.Contains(key))
                {
                    throw new ArgumentException(
                        $"Extension validator '{validator.Namespace}' declares bootstrap key '{key}', which the library "
                        + "resolves itself (instanceId, specHash). A host declares only the keys it resolves.",
                        nameof(extensionValidators));
                }

                keys.Add(key);
            }
        }

        return keys;
    }

    private static WorkloadManifest ParseWorkload(
        JsonElement wl, string path, List<DeploymentValidationError> errs,
        IReadOnlyList<IWorkloadExtensionValidator> extensionValidators)
    {
        if (wl.ValueKind != JsonValueKind.Object)
        {
            errs.Add(new(path, $"Each workload must be a JSON object but was {wl.ValueKind}.", DeploymentValidationCodes.NotAnObject));
            return new WorkloadManifest
            {
                Id = string.Empty,
                DisplayName = string.Empty,
                Image = new WorkloadImage { Ref = string.Empty, Digest = string.Empty },
                Kind = WorkloadKind.Service,
                Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            };
        }

        RejectUnknownKeys(wl, path, WorkloadKeys, errs);

        var id = ReadRequiredString(wl, "id", $"{path}.id", errs);
        var displayName = ReadRequiredString(wl, "displayName", $"{path}.displayName", errs);
        var description = ReadOptionalStringChecked(wl, "description", $"{path}.description", errs);
        var image = ParseImage(wl, path, errs);
        var kind = ReadRequiredEnum(wl, "kind", $"{path}.kind", errs, ParseKind, WorkloadKind.Service);
        var resources = ParseResources(wl, path, errs);
        var network = ParseNetwork(wl, path, errs);
        var privileges = ParsePrivileges(wl, path, errs);
        var volumes = ParseVolumes(wl, path, errs);
        var configMapMounts = ParseConfigMapMounts(wl, path, errs);
        var health = ParseHealth(wl, path, errs);
        RejectEvictedFields(wl, path, errs);
        var extensions = ParseExtensions(wl, path, errs, extensionValidators);
        var hostRequirements = ParseHostRequirements(wl, path, errs);
        var observability = ParseObservability(wl, path, errs);
        var env = ParseEnv(wl, path, errs);
        var parameters = ParseParameters(wl, path, errs);
        var patch = ParseUnsupportedPodPatch(wl, path, errs);

        return new WorkloadManifest
        {
            Id = id ?? string.Empty,
            DisplayName = displayName ?? string.Empty,
            Description = description,
            Image = image,
            Kind = kind,
            Resources = resources,
            Network = network,
            Privileges = privileges,
            Volumes = volumes,
            ConfigMapMounts = configMapMounts,
            Health = health,
            Extensions = extensions,
            HostRequirements = hostRequirements,
            Observability = observability,
            Env = env,
            Parameters = parameters,
            UnsupportedPodPatch = patch,
        };
    }

    private static WorkloadImage ParseImage(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.image";
        if (!TryGetObject(wl, "image", path, required: true, errs, out var img))
            return new WorkloadImage { Ref = string.Empty, Digest = string.Empty };

        RejectUnknownKeys(img, path, ImageKeys, errs);

        var platforms = ReadStringArray(img, "platforms", $"{path}.platforms", errs);
        for (var pi = 0; pi < platforms.Count; pi++)
        {
            if (!WorkloadPathPolicy.IsValidPlatformToken(platforms[pi]))
            {
                errs.Add(new(
                    $"{path}.platforms[{pi}]",
                    $"image.platforms[] entry '{platforms[pi]}' is not a valid os/arch token (e.g. linux/amd64).",
                    DeploymentValidationCodes.InvalidPlatformToken));
            }
        }

        var hasComponentKey = img.TryGetProperty("component", out var componentEl)
            && componentEl.ValueKind != JsonValueKind.Null;
        if (hasComponentKey)
        {
            var component = ReadRequiredString(img, "component", $"{path}.component", errs) ?? string.Empty;

            if (!string.IsNullOrEmpty(component) && !SlugPattern.IsMatch(component))
            {
                errs.Add(new(
                    $"{path}.component",
                    $"image.component '{component}' must match ^[a-z0-9-]{{1,40}}$.",
                    DeploymentValidationCodes.InvalidImageComponent));
            }

            foreach (var forbidden in ImageComponentExclusiveKeys)
            {
                if (IsMeaningfullyPresent(img, forbidden))
                {
                    errs.Add(new(
                        $"{path}.{forbidden}",
                        $"image.{forbidden} must not be combined with image.component — the product-image form is " +
                        "mutually exclusive with ref/digest/tag.",
                        DeploymentValidationCodes.ImageComponentConflict));
                }
            }

            return new WorkloadImage
            {
                Component = component,
                Ref = string.Empty,
                Digest = string.Empty,
                Platforms = platforms,
            };
        }

        var reference = ReadRequiredString(img, "ref", $"{path}.ref", errs) ?? string.Empty;
        var digest = ReadRequiredString(img, "digest", $"{path}.digest", errs) ?? string.Empty;

        if (!string.IsNullOrEmpty(reference) && reference.Contains('@'))
        {
            errs.Add(new(
                $"{path}.ref",
                "image.ref must be repository-only (no '@digest'); pin the image via the separate 'image.digest' field.",
                DeploymentValidationCodes.ImageRefHasTag));
        }
        else if (!string.IsNullOrEmpty(reference) && RefHasTag(reference))
        {
            errs.Add(new(
                $"{path}.ref",
                "image.ref must be repository-only (no ':tag'); the digest pins the image. Move any tag into 'image.tag'.",
                DeploymentValidationCodes.ImageRefHasTag));
        }

        if (!string.IsNullOrEmpty(reference) && !ImageRefPattern.IsMatch(reference))
        {
            errs.Add(new(
                $"{path}.ref",
                $"image.ref must match ^[A-Za-z0-9][A-Za-z0-9._:/-]{{0,254}}$ but was '{reference}'.",
                DeploymentValidationCodes.InvalidImageRef));
        }

        if (!string.IsNullOrEmpty(digest) && !DigestPattern.IsMatch(digest))
        {
            errs.Add(new(
                $"{path}.digest",
                $"image.digest must match ^sha256:[0-9a-f]{{64}}$ but was '{digest}'.",
                DeploymentValidationCodes.InvalidImageDigest));
        }

        return new WorkloadImage
        {
            Ref = reference,
            Digest = digest,
            Tag = ReadOptionalStringChecked(img, "tag", $"{path}.tag", errs),
            Platforms = platforms,
        };
    }

    private static readonly string[] ImageComponentExclusiveKeys = { "ref", "digest", "tag" };

    private static bool IsMeaningfullyPresent(JsonElement obj, string field)
    {
        if (!obj.TryGetProperty(field, out var el) || el.ValueKind == JsonValueKind.Null)
            return false;
        if (el.ValueKind == JsonValueKind.String)
            return !string.IsNullOrEmpty(el.GetString());
        return true;
    }

    private static WorkloadResources? ParseResources(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.resources";
        if (!TryGetObject(wl, "resources", path, required: false, errs, out var res))
            return null;

        RejectUnknownKeys(res, path, ResourcesKeys, errs);

        return new WorkloadResources
        {
            Requests = ParseQuantities(res, "requests", $"{path}.requests", errs),
            Limits = ParseQuantities(res, "limits", $"{path}.limits", errs),
        };
    }

    private static ResourceQuantities? ParseQuantities(JsonElement parentEl, string field, string path, List<DeploymentValidationError> errs)
    {
        if (!TryGetObject(parentEl, field, path, required: false, errs, out var q))
            return null;

        RejectUnknownKeys(q, path, QuantityKeys, errs);

        var cpu = ReadOptionalStringChecked(q, "cpu", $"{path}.cpu", errs);
        var memory = ReadOptionalStringChecked(q, "memory", $"{path}.memory", errs);

        if (cpu is not null && !WorkloadPathPolicy.IsValidQuantity(cpu))
            errs.Add(new($"{path}.cpu", $"'{cpu}' is not a valid Kubernetes CPU quantity (e.g. 100m, 0.5).", DeploymentValidationCodes.InvalidQuantity));
        if (memory is not null && !WorkloadPathPolicy.IsValidQuantity(memory))
            errs.Add(new($"{path}.memory", $"'{memory}' is not a valid Kubernetes memory quantity (e.g. 256Mi, 1Gi).", DeploymentValidationCodes.InvalidQuantity));

        return new ResourceQuantities { Cpu = cpu, Memory = memory };
    }

    private static WorkloadNetwork? ParseNetwork(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.network";
        if (!TryGetObject(wl, "network", path, required: false, errs, out var net))
            return null;

        RejectUnknownKeys(net, path, NetworkKeys, errs);

        var ports = new List<WorkloadPort>();
        if (net.TryGetProperty("ports", out var portsEl))
        {
            if (portsEl.ValueKind != JsonValueKind.Array)
            {
                errs.Add(new($"{path}.ports", $"Field 'ports' must be an array but was {portsEl.ValueKind}.", DeploymentValidationCodes.WrongType));
            }
            else
            {
                var i = 0;
                var seenNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in portsEl.EnumerateArray())
                {
                    var pPath = $"{path}.ports[{i}]";
                    if (p.ValueKind != JsonValueKind.Object)
                    {
                        errs.Add(new(pPath, $"Each port must be a JSON object but was {p.ValueKind}.", DeploymentValidationCodes.NotAnObject));
                    }
                    else
                    {
                        RejectUnknownKeys(p, pPath, PortKeys, errs);
                        var name = ReadRequiredString(p, "name", $"{pPath}.name", errs) ?? string.Empty;
                        var parsedPort = ReadRequiredInt(p, "port", $"{pPath}.port", errs);
                        var port = parsedPort ?? 0;
                        var protocol = ReadRequiredString(p, "protocol", $"{pPath}.protocol", errs) ?? string.Empty;

                        if (parsedPort is { } declaredPort && !IsValidPort(declaredPort))
                            errs.Add(new($"{pPath}.port", $"Port {declaredPort} is out of range (1–65535).", DeploymentValidationCodes.InvalidPort));
                        if (!string.IsNullOrEmpty(protocol) && !IsTcpOrUdp(protocol))
                            errs.Add(new($"{pPath}.protocol", $"Port protocol '{protocol}' must be tcp or udp.", DeploymentValidationCodes.InvalidPortProtocol));
                        if (!string.IsNullOrEmpty(name) && !seenNames.Add(name))
                            errs.Add(new($"{path}.ports", $"Duplicate port name '{name}'.", DeploymentValidationCodes.DuplicatePortName));

                        if (!string.IsNullOrEmpty(name) && !PortNamePattern.IsMatch(name))
                            errs.Add(new($"{pPath}.name",
                                $"Port name '{name}' must be an IANA_SVC_NAME: 1–15 characters of lowercase letters, digits and '-', at least one letter, no leading, trailing or consecutive '-'.",
                                DeploymentValidationCodes.InvalidPortName));

                        ports.Add(new WorkloadPort { Name = name, Port = port, Protocol = protocol });
                    }
                    i++;
                }
            }
        }

        var attachments = new List<WorkloadNetworkAttachmentSlot>();
        if (net.TryGetProperty("attachments", out var attEl))
        {
            if (attEl.ValueKind != JsonValueKind.Array)
            {
                errs.Add(new($"{path}.attachments", $"Field 'attachments' must be an array but was {attEl.ValueKind}.", DeploymentValidationCodes.WrongType));
            }
            else
            {
                var i = 0;
                foreach (var a in attEl.EnumerateArray())
                {
                    var aPath = $"{path}.attachments[{i}]";
                    if (a.ValueKind != JsonValueKind.Object)
                    {
                        errs.Add(new(aPath, $"Each network attachment must be a JSON object but was {a.ValueKind}.", DeploymentValidationCodes.NotAnObject));
                    }
                    else
                    {
                        RejectUnknownKeys(a, aPath, NetworkAttachmentSlotKeys, errs);
                        var parameter = ReadRequiredString(a, "parameter", $"{aPath}.parameter", errs) ?? string.Empty;
                        var description = ReadOptionalStringChecked(a, "description", $"{aPath}.description", errs);

                        attachments.Add(new WorkloadNetworkAttachmentSlot { Parameter = parameter, Description = description });
                    }
                    i++;
                }
            }
        }

        return new WorkloadNetwork
        {
            HostNetwork = ReadOptionalBoolChecked(net, "hostNetwork", $"{path}.hostNetwork", errs),
            Mdns = ReadOptionalBoolChecked(net, "mdns", $"{path}.mdns", errs),
            Ipv6Required = ReadOptionalBoolChecked(net, "ipv6Required", $"{path}.ipv6Required", errs),
            Ports = ports,
            Attachments = attachments,
        };
    }

    private static WorkloadPrivileges? ParsePrivileges(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.privileges";
        if (!TryGetObject(wl, "privileges", path, required: false, errs, out var priv))
            return null;

        RejectUnknownKeys(priv, path, PrivilegesKeys, errs);

        var caps = new List<WorkloadCapability>();
        if (priv.TryGetProperty("capabilities", out var capsEl))
        {
            if (capsEl.ValueKind != JsonValueKind.Array)
            {
                errs.Add(new($"{path}.capabilities", $"Field 'capabilities' must be an array but was {capsEl.ValueKind}.", DeploymentValidationCodes.WrongType));
            }
            else
            {
                var i = 0;
                var seen = new HashSet<WorkloadCapability>();
                foreach (var c in capsEl.EnumerateArray())
                {
                    var cPath = $"{path}.capabilities[{i}]";
                    if (c.ValueKind != JsonValueKind.String)
                    {
                        errs.Add(new(cPath, $"Each capability must be a string but was {c.ValueKind}.", DeploymentValidationCodes.WrongType));
                    }
                    else if (ParseCapability(c.GetString()) is { } cap)
                    {
                        if (!seen.Add(cap))
                            errs.Add(new($"{path}.capabilities", $"Duplicate capability '{c.GetString()}'.", DeploymentValidationCodes.DuplicateCapability));
                        caps.Add(cap);
                    }
                    else
                    {
                        errs.Add(new(cPath, $"Unknown capability '{c.GetString()}'. Allowed: NET_ADMIN, NET_RAW, SYS_ADMIN.", DeploymentValidationCodes.UnknownEnumValue));
                    }
                    i++;
                }
            }
        }

        var devices = new List<WorkloadDevice>();
        if (priv.TryGetProperty("devices", out var devEl))
        {
            if (devEl.ValueKind != JsonValueKind.Array)
            {
                errs.Add(new($"{path}.devices", $"Field 'devices' must be an array but was {devEl.ValueKind}.", DeploymentValidationCodes.WrongType));
            }
            else
            {
                var i = 0;
                foreach (var d in devEl.EnumerateArray())
                {
                    var dPath = $"{path}.devices[{i}]";
                    if (d.ValueKind != JsonValueKind.Object)
                    {
                        errs.Add(new(dPath, $"Each device must be a JSON object but was {d.ValueKind}.", DeploymentValidationCodes.NotAnObject));
                    }
                    else
                    {
                        RejectUnknownKeys(d, dPath, DeviceKeys, errs);

                        var deviceParameter = ReadRequiredString(d, "parameter", $"{dPath}.parameter", errs) ?? string.Empty;

                        var maxCount = 1;
                        int? declaredMaxCount = null;
                        if (d.TryGetProperty("maxCount", out var maxCountEl) && maxCountEl.ValueKind != JsonValueKind.Null)
                        {
                            declaredMaxCount = ReadRequiredInt(d, "maxCount", $"{dPath}.maxCount", errs);
                            maxCount = declaredMaxCount ?? 0;
                        }

                        var kindText = ReadOptionalStringChecked(d, "deviceKind", $"{dPath}.deviceKind", errs);
                        var groupText = ReadOptionalStringChecked(d, "group", $"{dPath}.group", errs);

                        if (declaredMaxCount is { } declared && !IsValidDeviceMaxCount(declared))
                            errs.Add(new($"{dPath}.maxCount", $"Device maxCount {declared} is out of range (1–8).", DeploymentValidationCodes.InvalidDeviceMaxCount));
                        if (kindText is not null && ParseDeviceKind(kindText) is null)
                            errs.Add(new($"{dPath}.deviceKind", $"Device kind '{kindText}' is not one of usb-serial, gpu-render, accelerator.", DeploymentValidationCodes.InvalidDeviceKind));
                        if (groupText is not null && ParseDeviceGroup(groupText) is null)
                            errs.Add(new($"{dPath}.group", $"Device group '{groupText}' must be 'none' or 'auto'.", DeploymentValidationCodes.InvalidDeviceGroup));

                        devices.Add(new WorkloadDevice
                        {
                            Parameter = deviceParameter,
                            MaxCount = maxCount,
                            DeviceKind = ParseDeviceKind(kindText),
                            Group = ParseDeviceGroup(groupText) ?? WorkloadDeviceGroup.None,
                        });
                    }
                    i++;
                }
            }
        }

        var hostPaths = new List<WorkloadHostPath>();
        if (priv.TryGetProperty("hostPaths", out var hpEl))
        {
            if (hpEl.ValueKind != JsonValueKind.Array)
            {
                errs.Add(new($"{path}.hostPaths", $"Field 'hostPaths' must be an array but was {hpEl.ValueKind}.", DeploymentValidationCodes.WrongType));
            }
            else
            {
                var i = 0;
                var seenMountPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var hp in hpEl.EnumerateArray())
                {
                    var hpPath = $"{path}.hostPaths[{i}]";
                    if (hp.ValueKind != JsonValueKind.Object)
                    {
                        errs.Add(new(hpPath, $"Each hostPath must be a JSON object but was {hp.ValueKind}.", DeploymentValidationCodes.NotAnObject));
                    }
                    else
                    {
                        RejectUnknownKeys(hp, hpPath, HostPathKeys, errs);
                        var parameter = ReadRequiredString(hp, "parameter", $"{hpPath}.parameter", errs) ?? string.Empty;
                        var mountPath = ReadRequiredString(hp, "mountPath", $"{hpPath}.mountPath", errs) ?? string.Empty;

                        if (!string.IsNullOrEmpty(mountPath) && mountPath[0] != '/')
                            errs.Add(new($"{hpPath}.mountPath", $"hostPaths mountPath '{mountPath}' must be an absolute path.", DeploymentValidationCodes.InvalidHostPathMountPath));
                        if (!string.IsNullOrEmpty(mountPath) && !seenMountPaths.Add(mountPath))
                            errs.Add(new($"{path}.hostPaths", $"Duplicate hostPaths mountPath '{mountPath}'.", DeploymentValidationCodes.DuplicateHostPathMountPath));

                        hostPaths.Add(new WorkloadHostPath
                        {
                            Parameter = parameter,
                            MountPath = mountPath,
                            Description = ReadOptionalStringChecked(hp, "description", $"{hpPath}.description", errs),
                        });
                    }
                    i++;
                }
            }
        }

        NodeLocalState? nodeLocalState = null;
        var nlsPath = $"{path}.nodeLocalState";
        if (TryGetObject(priv, "nodeLocalState", nlsPath, required: false, errs, out var nls))
        {
            RejectUnknownKeys(nls, nlsPath, NodeLocalStateKeys, errs);
            var subdir = ReadRequiredString(nls, "subdir", $"{nlsPath}.subdir", errs) ?? string.Empty;

            if (!string.IsNullOrEmpty(subdir) && !WorkloadPathPolicy.IsSafeRelativeSubdir(subdir))
            {
                errs.Add(new(
                    $"{nlsPath}.subdir",
                    $"nodeLocalState.subdir '{subdir}' must be a safe relative path (no leading '/', no '..' segment).",
                    DeploymentValidationCodes.InvalidNodeLocalSubdir));
            }

            nodeLocalState = new NodeLocalState
            {
                Subdir = subdir,
                Criticality = ReadRequiredEnum(nls, "criticality", $"{nlsPath}.criticality", errs, ParseCriticality, VolumeCriticality.Cache),
                Description = ReadOptionalStringChecked(nls, "description", $"{nlsPath}.description", errs),
            };
        }

        return new WorkloadPrivileges
        {
            Capabilities = caps,
            Privileged = ReadOptionalBoolChecked(priv, "privileged", $"{path}.privileged", errs),
            RunAsRoot = ReadOptionalBoolChecked(priv, "runAsRoot", $"{path}.runAsRoot", errs),
            FsGroup = ReadFsGroup(priv, $"{path}.fsGroup", errs),
            HostDbus = ReadOptionalBoolChecked(priv, "hostDbus", $"{path}.hostDbus", errs),
            TunDevice = ReadOptionalBoolChecked(priv, "tunDevice", $"{path}.tunDevice", errs),
            CallsBackendRest = ReadOptionalBoolChecked(priv, "callsBackendRest", $"{path}.callsBackendRest", errs),
            Devices = devices,
            HostPaths = hostPaths,
            NodeLocalState = nodeLocalState,
        };
    }

    private static int? ReadFsGroup(JsonElement priv, string path, List<DeploymentValidationError> errs)
    {
        if (!priv.TryGetProperty("fsGroup", out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var gid))
        {
            errs.Add(new(path, $"Field 'fsGroup' must be an integer but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
            return null;
        }

        if (gid is < 1 or > 65534)
        {
            errs.Add(new(path, $"privileges.fsGroup {gid} is out of range (1–65534); group 0 is root's.", DeploymentValidationCodes.InvalidFsGroup));
            return null;
        }

        return gid;
    }

    private static IReadOnlyList<WorkloadVolume> ParseVolumes(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.volumes";
        var volumes = new List<WorkloadVolume>();
        if (!wl.TryGetProperty("volumes", out var volEl))
            return volumes;
        if (volEl.ValueKind != JsonValueKind.Array)
        {
            errs.Add(new(path, $"Field 'volumes' must be an array but was {volEl.ValueKind}.", DeploymentValidationCodes.WrongType));
            return volumes;
        }

        var i = 0;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in volEl.EnumerateArray())
        {
            var vPath = $"{path}[{i}]";
            if (v.ValueKind != JsonValueKind.Object)
            {
                errs.Add(new(vPath, $"Each volume must be a JSON object but was {v.ValueKind}.", DeploymentValidationCodes.NotAnObject));
            }
            else
            {
                RejectUnknownKeys(v, vPath, VolumeKeys, errs);

                var vid = ReadRequiredString(v, "id", $"{vPath}.id", errs) ?? string.Empty;
                var mountPath = ReadRequiredString(v, "mountPath", $"{vPath}.mountPath", errs) ?? string.Empty;
                var size = ReadRequiredString(v, "size", $"{vPath}.size", errs) ?? string.Empty;
                var accessMode = ReadOptionalStringChecked(v, "accessMode", $"{vPath}.accessMode", errs) ?? "rwo";

                if (!string.IsNullOrEmpty(vid) && !SlugPattern.IsMatch(vid))
                    errs.Add(new($"{vPath}.id", $"volume id '{vid}' must match ^[a-z0-9-]{{1,40}}$.", DeploymentValidationCodes.InvalidVolumeId));
                if (!string.IsNullOrEmpty(vid) && !seenIds.Add(vid))
                    errs.Add(new(path, $"Duplicate volume id '{vid}'.", DeploymentValidationCodes.DuplicateVolumeId));
                if (!string.IsNullOrEmpty(mountPath) && mountPath[0] != '/')
                    errs.Add(new($"{vPath}.mountPath", $"volume mountPath '{mountPath}' must be an absolute path.", DeploymentValidationCodes.InvalidMountPath));
                if (!string.IsNullOrEmpty(size) && !WorkloadPathPolicy.IsValidQuantity(size))
                    errs.Add(new($"{vPath}.size", $"volume size '{size}' is not a valid Kubernetes quantity (e.g. 64Mi, 1Gi).", DeploymentValidationCodes.InvalidQuantity));
                if (!string.Equals(accessMode, "rwo", StringComparison.Ordinal))
                    errs.Add(new($"{vPath}.accessMode", $"volume accessMode '{accessMode}' must be 'rwo' in schemaVersion 1.", DeploymentValidationCodes.InvalidAccessMode));

                volumes.Add(new WorkloadVolume
                {
                    Id = vid,
                    MountPath = mountPath,
                    Size = size,
                    Criticality = ReadRequiredEnum(v, "criticality", $"{vPath}.criticality", errs, ParseCriticality, VolumeCriticality.Cache),
                    Description = ReadOptionalStringChecked(v, "description", $"{vPath}.description", errs),
                    AccessMode = accessMode,
                });
            }
            i++;
        }
        return volumes;
    }

    private static IReadOnlyList<ConfigMapMount> ParseConfigMapMounts(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.configMapMount";
        var mounts = new List<ConfigMapMount>();
        if (!wl.TryGetProperty("configMapMount", out var cmEl))
            return mounts;
        if (cmEl.ValueKind != JsonValueKind.Array)
        {
            errs.Add(new(path, $"Field 'configMapMount' must be an array but was {cmEl.ValueKind}.", DeploymentValidationCodes.WrongType));
            return mounts;
        }

        var i = 0;
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cm in cmEl.EnumerateArray())
        {
            var cmPath = $"{path}[{i}]";
            if (cm.ValueKind != JsonValueKind.Object)
            {
                errs.Add(new(cmPath, $"Each configMapMount must be a JSON object but was {cm.ValueKind}.", DeploymentValidationCodes.NotAnObject));
            }
            else
            {
                RejectUnknownKeys(cm, cmPath, ConfigMapMountKeys, errs);

                var mountPath = ReadRequiredString(cm, "mountPath", $"{cmPath}.mountPath", errs) ?? string.Empty;
                var parameter = ReadRequiredString(cm, "parameter", $"{cmPath}.parameter", errs) ?? string.Empty;

                if (!string.IsNullOrEmpty(mountPath) && mountPath[0] != '/')
                    errs.Add(new($"{cmPath}.mountPath", $"configMapMount mountPath '{mountPath}' must be an absolute path.", DeploymentValidationCodes.InvalidConfigMapMountPath));
                if (!string.IsNullOrEmpty(mountPath) && !seenPaths.Add(mountPath))
                    errs.Add(new(path, $"Duplicate configMapMount mountPath '{mountPath}'.", DeploymentValidationCodes.DuplicateConfigMapMountPath));

                mounts.Add(new ConfigMapMount { MountPath = mountPath, Parameter = parameter });
            }
            i++;
        }
        return mounts;
    }

    private static WorkloadHealth ParseHealth(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.health";
        if (!TryGetObject(wl, "health", path, required: true, errs, out var h))
            return new WorkloadHealth { Mode = WorkloadHealthMode.Container };

        RejectUnknownKeys(h, path, HealthKeys, errs);

        var mode = ReadRequiredOpen(h, "mode", $"{path}.mode", errs, ParseHealthMode, WorkloadHealthMode.Container, out var modeRead);

        var httpProbe = ParseHttpProbe(h, path, errs);
        var httpPresent = h.TryGetProperty("http", out var httpKeyEl) && httpKeyEl.ValueKind != JsonValueKind.Null;

        var heartbeat = 0;
        var heartbeatPresent = false;
        var heartbeatValid = false;
        if (h.TryGetProperty("heartbeatSeconds", out var hbEl) && hbEl.ValueKind != JsonValueKind.Null)
        {
            heartbeatPresent = true;
            if (hbEl.ValueKind == JsonValueKind.Number && hbEl.TryGetInt32(out var hb))
            {
                heartbeat = hb;
                heartbeatValid = true;
            }
            else
            {
                errs.Add(new($"{path}.heartbeatSeconds", $"Field 'heartbeatSeconds' must be an integer but was {hbEl.ValueKind}.", DeploymentValidationCodes.WrongType));
            }
        }

        if (!modeRead)
        {
        }
        else if (!mode.IsLibraryOwned)
        {
            if (!heartbeatPresent)
            {
                heartbeat = DefaultHeartbeatSeconds;
            }
            else if (heartbeatValid && heartbeat <= 0)
            {
                errs.Add(new($"{path}.heartbeatSeconds", $"health.heartbeatSeconds must be > 0 but was {heartbeat}.", DeploymentValidationCodes.InvalidHeartbeat));
            }
        }
        else
        {
            if (mode == WorkloadHealthMode.Http && httpProbe is null)
                errs.Add(new($"{path}.http", "health.mode 'http' requires an 'http' probe object.", DeploymentValidationCodes.InvalidHealthShape));
            if (mode == WorkloadHealthMode.Container && httpPresent)
                errs.Add(new($"{path}.http", "health.mode 'container' uses no sub-fields ('http' belongs to http and to a host health mode).", DeploymentValidationCodes.InvalidHealthShape));
            if (heartbeatPresent)
                errs.Add(new($"{path}.heartbeatSeconds", $"health.mode '{mode.Value}' does not use 'heartbeatSeconds' (only a host health mode takes it).", DeploymentValidationCodes.InvalidHealthShape));
        }

        if (httpProbe is not null && httpProbe.Port != 0 && !IsValidPort(httpProbe.Port))
            errs.Add(new($"{path}.http.port", $"health.http.port {httpProbe.Port} is out of range (1–65535).", DeploymentValidationCodes.InvalidPort));

        return new WorkloadHealth
        {
            Mode = mode,
            HeartbeatSeconds = heartbeat,
            Http = httpProbe,
        };
    }

    private static WorkloadHttpProbe? ParseHttpProbe(JsonElement healthEl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.http";
        if (!TryGetObject(healthEl, "http", path, required: false, errs, out var http))
            return null;
        RejectUnknownKeys(http, path, HttpProbeKeys, errs);
        return new WorkloadHttpProbe
        {
            Port = ReadRequiredInt(http, "port", $"{path}.port", errs) ?? 0,
            LivenessPath = ReadOptionalStringChecked(http, "livenessPath", $"{path}.livenessPath", errs),
            ReadinessPath = ReadOptionalStringChecked(http, "readinessPath", $"{path}.readinessPath", errs),
        };
    }

    private static void RejectEvictedFields(JsonElement wl, string path, List<DeploymentValidationError> errs)
    {
        foreach (var (field, parent, fate) in EvictedFields)
        {
            var container = wl;
            if (parent.Length > 0
                && (!wl.TryGetProperty(parent, out container) || container.ValueKind != JsonValueKind.Object))
            {
                continue;
            }

            if (!container.TryGetProperty(field, out _))
            {
                continue;
            }

            var fieldPath = parent.Length > 0 ? $"{path}.{parent}.{field}" : $"{path}.{field}";
            errs.Add(new(
                fieldPath,
                $"'{field}' is not a manifest field: {fate}. Re-publish the plugin against the current schema.",
                DeploymentValidationCodes.EvictedManifestField));
        }
    }

    private static WorkloadExtensions ParseExtensions(
        JsonElement wl, string parent, List<DeploymentValidationError> errs,
        IReadOnlyList<IWorkloadExtensionValidator> extensionValidators)
    {
        var path = $"{parent}.extensions";
        var byNamespace = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var declared = new HashSet<string>(StringComparer.Ordinal);
        var shapeRejected = false;

        if (wl.TryGetProperty("extensions", out var el) && el.ValueKind != JsonValueKind.Null)
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                errs.Add(new(path, $"'extensions' must be a JSON object of host namespaces but was {el.ValueKind}.", DeploymentValidationCodes.InvalidExtensionShape));
                shapeRejected = true;
            }
            else
            {
                foreach (var ns in el.EnumerateObject())
                {
                    if (!declared.Add(ns.Name))
                    {
                        errs.Add(new($"{path}.{ns.Name}", $"Extension namespace '{ns.Name}' is declared more than once; only the first would be carried.", DeploymentValidationCodes.InvalidExtensionShape));
                        continue;
                    }

                    if (ns.Value.ValueKind != JsonValueKind.Object)
                    {
                        errs.Add(new($"{path}.{ns.Name}", $"Extension namespace '{ns.Name}' must be a JSON object but was {ns.Value.ValueKind}.", DeploymentValidationCodes.InvalidExtensionShape));
                        continue;
                    }

                    byNamespace[ns.Name] = ns.Value.Clone();
                }
            }
        }

        foreach (var validator in extensionValidators)
        {
            if (byNamespace.TryGetValue(validator.Namespace, out var owned))
            {
                validator.Validate(wl, owned, $"{path}.{validator.Namespace}", errs);
            }
            else if (validator.Required && !shapeRejected && !declared.Contains(validator.Namespace))
            {
                errs.Add(new(
                    $"{path}.{validator.Namespace}",
                    $"Required extension namespace '{validator.Namespace}' is missing. Every workload must declare it; "
                    + "a misspelled namespace reads as a foreign host's and would drop the contract silently.",
                    DeploymentValidationCodes.MissingExtensionNamespace));
            }
        }

        return byNamespace.Count == 0
            ? WorkloadExtensions.None
            : new WorkloadExtensions { ByNamespace = byNamespace };
    }

    private static IReadOnlyList<HostRequirement> ParseHostRequirements(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.hostRequirements";
        var list = new List<HostRequirement>();
        if (!wl.TryGetProperty("hostRequirements", out var hrEl))
            return list;
        if (hrEl.ValueKind != JsonValueKind.Array)
        {
            errs.Add(new(path, $"Field 'hostRequirements' must be an array but was {hrEl.ValueKind}.", DeploymentValidationCodes.WrongType));
            return list;
        }
        var i = 0;
        foreach (var hr in hrEl.EnumerateArray())
        {
            var hrPath = $"{path}[{i}]";
            if (hr.ValueKind != JsonValueKind.Object)
            {
                errs.Add(new(hrPath, $"Each hostRequirement must be a JSON object but was {hr.ValueKind}.", DeploymentValidationCodes.NotAnObject));
            }
            else
            {
                RejectUnknownKeys(hr, hrPath, HostRequirementKeys, errs);
                var kind = ReadRequiredEnum(hr, "kind", $"{hrPath}.kind", errs, ParseHostRequirementKind, HostRequirementKind.Multicast);
                var name = ReadOptionalStringChecked(hr, "name", $"{hrPath}.name", errs);

                if ((kind == HostRequirementKind.DbusService || kind == HostRequirementKind.KernelModule)
                    && string.IsNullOrWhiteSpace(name))
                {
                    errs.Add(new(
                        hrPath,
                        $"hostRequirement kind '{HostRequirementKindName(kind)}' must carry a 'name' (host requirements are mapped by name).",
                        DeploymentValidationCodes.MissingHostRequirementName));
                }

                list.Add(new HostRequirement
                {
                    Kind = kind,
                    Name = name,
                    Description = ReadOptionalStringChecked(hr, "description", $"{hrPath}.description", errs),
                });
            }
            i++;
        }
        return list;
    }

    private static WorkloadObservability? ParseObservability(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.observability";
        if (!TryGetObject(wl, "observability", path, required: false, errs, out var obs))
            return null;

        RejectUnknownKeys(obs, path, ObservabilityKeys, errs);

        WorkloadMetrics? metrics = null;
        var mPath = $"{path}.metrics";
        if (TryGetObject(obs, "metrics", mPath, required: false, errs, out var m))
        {
            RejectUnknownKeys(m, mPath, MetricsKeys, errs);
            var parsedPort = ReadRequiredInt(m, "port", $"{mPath}.port", errs);
            var port = parsedPort ?? 0;
            var metricsPath = ReadRequiredString(m, "path", $"{mPath}.path", errs) ?? string.Empty;

            if (parsedPort is { } declaredPort && !IsValidPort(declaredPort))
                errs.Add(new($"{mPath}.port", $"metrics.port {declaredPort} is out of range (1–65535).", DeploymentValidationCodes.InvalidPort));
            if (!string.IsNullOrEmpty(metricsPath) && metricsPath[0] != '/')
                errs.Add(new($"{mPath}.path", $"metrics.path '{metricsPath}' must start with '/'.", DeploymentValidationCodes.InvalidMetricsPath));

            metrics = new WorkloadMetrics { Port = port, Path = metricsPath };
        }
        return new WorkloadObservability { Metrics = metrics };
    }

    private static IReadOnlyList<WorkloadEnv> ParseEnv(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.env";
        var list = new List<WorkloadEnv>();
        if (!wl.TryGetProperty("env", out var envEl))
            return list;
        if (envEl.ValueKind != JsonValueKind.Array)
        {
            errs.Add(new(path, $"Field 'env' must be an array but was {envEl.ValueKind}.", DeploymentValidationCodes.WrongType));
            return list;
        }
        var i = 0;
        foreach (var e in envEl.EnumerateArray())
        {
            var ePath = $"{path}[{i}]";
            if (e.ValueKind != JsonValueKind.Object)
            {
                errs.Add(new(ePath, $"Each env entry must be a JSON object but was {e.ValueKind}.", DeploymentValidationCodes.NotAnObject));
            }
            else
            {
                RejectUnknownKeys(e, ePath, EnvKeys, errs);
                var envName = ReadRequiredString(e, "name", $"{ePath}.name", errs) ?? string.Empty;
                if (!string.IsNullOrEmpty(envName) && !EnvNamePattern.IsMatch(envName))
                {
                    errs.Add(new(
                        $"{ePath}.name",
                        $"env.name must match ^[A-Za-z_][A-Za-z0-9_]*$ but was '{envName}'.",
                        DeploymentValidationCodes.InvalidEnvName));
                }

                list.Add(new WorkloadEnv
                {
                    Name = envName,
                    From = ReadRequiredString(e, "from", $"{ePath}.from", errs) ?? string.Empty,
                });
            }
            i++;
        }
        return list;
    }

    private static IReadOnlyList<WorkloadParameter> ParseParameters(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.parameters";
        var list = new List<WorkloadParameter>();
        if (!wl.TryGetProperty("parameters", out var pEl))
            return list;
        if (pEl.ValueKind != JsonValueKind.Array)
        {
            errs.Add(new(path, $"Field 'parameters' must be an array but was {pEl.ValueKind}.", DeploymentValidationCodes.WrongType));
            return list;
        }
        var i = 0;
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pEl.EnumerateArray())
        {
            var pPath = $"{path}[{i}]";
            if (p.ValueKind != JsonValueKind.Object)
            {
                errs.Add(new(pPath, $"Each parameter must be a JSON object but was {p.ValueKind}.", DeploymentValidationCodes.NotAnObject));
            }
            else
            {
                RejectUnknownKeys(p, pPath, ParameterKeys, errs);

                IReadOnlyList<string>? enumValues = null;
                if (p.TryGetProperty("enumValues", out var evEl))
                {
                    if (evEl.ValueKind != JsonValueKind.Array)
                        errs.Add(new($"{pPath}.enumValues", $"Field 'enumValues' must be an array but was {evEl.ValueKind}.", DeploymentValidationCodes.WrongType));
                    else
                        enumValues = ReadStringArray(p, "enumValues", $"{pPath}.enumValues", errs);
                }

                var name = ReadRequiredString(p, "name", $"{pPath}.name", errs) ?? string.Empty;
                var type = ReadRequiredEnum(p, "type", $"{pPath}.type", errs, ParseParameterType, WorkloadParameterType.Text);

                var defaultRead = ReadDefaultValue(p, $"{pPath}.default", errs);
                var defaultValue = defaultRead.Value;

                if (!string.IsNullOrEmpty(name) && !seenNames.Add(name))
                    errs.Add(new(path, $"Duplicate parameter name '{name}'.", DeploymentValidationCodes.DuplicateParameterName));

                if (!string.IsNullOrEmpty(name) && name.AsSpan().IndexOfAny('[', ']', ':') >= 0)
                    errs.Add(new($"{pPath}.name",
                        $"Parameter name '{name}' may not contain '[', ']' or ':' — those are reserved for synthesized keys.",
                        DeploymentValidationCodes.InvalidParameterName));

                if (type == WorkloadParameterType.Enum && (enumValues is null || enumValues.Count == 0))
                    errs.Add(new(pPath, $"parameter '{name}' of type 'enum' must supply non-empty 'enumValues'.", DeploymentValidationCodes.MissingEnumValues));

                if (defaultRead.HasScalar && defaultValue is not null)
                    ValidateParameterDefault(type, name, defaultValue, defaultRead.Kind, defaultRead.IsIntegerNumber, enumValues, $"{pPath}.default", errs);

                var widget = ReadOptionalWidget(p, $"{pPath}.widget", errs);
                var enumLabels = ReadEnumLabels(p, $"{pPath}.enumLabels", errs);
                var reveals = ReadReveals(p, $"{pPath}.reveals", errs);
                ValidateSwitchKeys(name, type, enumValues, widget, enumLabels, reveals, pPath, errs);

                list.Add(new WorkloadParameter
                {
                    Name = name,
                    Type = type,
                    DisplayName = ReadOptionalStringChecked(p, "displayName", $"{pPath}.displayName", errs),
                    Description = ReadOptionalStringChecked(p, "description", $"{pPath}.description", errs),
                    Required = ReadOptionalBoolChecked(p, "required", $"{pPath}.required", errs),
                    Default = defaultValue,
                    EnumValues = enumValues,
                    Hint = ReadOptionalStringChecked(p, "hint", $"{pPath}.hint", errs),
                    Section = NormalizeSection(ReadOptionalStringChecked(p, "section", $"{pPath}.section", errs)),
                    Widget = widget,
                    EnumLabels = enumLabels,
                    Reveals = reveals,
                });
            }
            i++;
        }
        return list;
    }

    private static void ValidateParameterDefault(
        WorkloadParameterType type, string name, string defaultValue, JsonValueKind kind, bool isIntegerNumber,
        IReadOnlyList<string>? enumValues, string path, List<DeploymentValidationError> errs)
    {
        var kindOk = type switch
        {
            WorkloadParameterType.Integer => kind == JsonValueKind.Number && isIntegerNumber,
            WorkloadParameterType.Boolean => kind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.String,
            _ => kind == JsonValueKind.String,
        };
        if (!kindOk)
        {
            errs.Add(new(path, $"parameter '{name}' default '{defaultValue}' is not a valid default for a '{ParameterTypeName(type)}' parameter.", DeploymentValidationCodes.InvalidParameterDefault));
            return;
        }

        switch (type)
        {
            case WorkloadParameterType.Enum:
                if (enumValues is not null && enumValues.Count > 0 && !enumValues.Contains(defaultValue, StringComparer.Ordinal))
                    errs.Add(new(path, $"parameter '{name}' default '{defaultValue}' is not one of its enumValues.", DeploymentValidationCodes.InvalidParameterDefault));
                break;
            case WorkloadParameterType.DevicePath:
                if (!WorkloadPathPolicy.IsAllowedDevicePath(defaultValue))
                    errs.Add(new(path, $"parameter '{name}' devicePath default '{defaultValue}' must be under /dev/**.", DeploymentValidationCodes.DisallowedPathPrefix));
                break;
            case WorkloadParameterType.HostPath:
                if (!WorkloadPathPolicy.IsAllowedHostInspectPath(defaultValue))
                    errs.Add(new(path, $"parameter '{name}' hostPath default '{defaultValue}' must be under /sys/**, /proc/**, /run/**, /var/run/**, /dev/serial, or /dev/dri, {WorkloadHostPathRefusal.Display}.", DeploymentValidationCodes.DisallowedPathPrefix));
                break;
            case WorkloadParameterType.Boolean:
                if (!bool.TryParse(defaultValue, out _))
                    errs.Add(new(path, $"parameter '{name}' boolean default '{defaultValue}' is not a valid boolean (true/false).", DeploymentValidationCodes.InvalidParameterDefault));
                break;
            case WorkloadParameterType.StorageSize:
                if (!WorkloadPathPolicy.IsValidQuantity(defaultValue))
                    errs.Add(new(path, $"parameter '{name}' storageSize default '{defaultValue}' is not a valid Kubernetes quantity (e.g. 64Mi, 1Gi).", DeploymentValidationCodes.InvalidParameterDefault));
                break;
            case WorkloadParameterType.NetworkAttachment:
                if (!string.IsNullOrWhiteSpace(defaultValue))
                    errs.Add(new(
                        path,
                        $"parameter '{name}' may not declare a networkAttachment default '{defaultValue}': the network a workload attaches to is the operator's to name.",
                        DeploymentValidationCodes.InvalidNetworkAttachmentValue));
                break;
        }
    }

    private static UnsupportedPodPatch? ParseUnsupportedPodPatch(JsonElement wl, string parent, List<DeploymentValidationError> errs)
    {
        var path = $"{parent}.unsupportedPodPatch";
        if (!wl.TryGetProperty("unsupportedPodPatch", out var patchEl))
            return null;
        if (patchEl.ValueKind == JsonValueKind.Null)
            return null;

        if (patchEl.ValueKind != JsonValueKind.Array)
        {
            errs.Add(new(path, $"Field 'unsupportedPodPatch' must be a JSON array (RFC 6902) but was {patchEl.ValueKind}.", DeploymentValidationCodes.WrongType));
        }
        else
        {
            var i = 0;
            foreach (var op in patchEl.EnumerateArray())
            {
                var opPath = $"{path}[{i}]";
                if (op.ValueKind != JsonValueKind.Object)
                {
                    errs.Add(new(opPath, $"Each RFC 6902 operation must be a JSON object but was {op.ValueKind}.", DeploymentValidationCodes.InvalidJsonPatch));
                }
                else
                {
                    RejectUnknownKeys(op, opPath, JsonPatchOpKeys, errs);
                    var opName = op.TryGetProperty("op", out var opEl) && opEl.ValueKind == JsonValueKind.String ? opEl.GetString() : null;
                    if (opName is null || !JsonPatchOps.Contains(opName))
                        errs.Add(new($"{opPath}.op", $"RFC 6902 op '{opName}' is not one of add/remove/replace/move/copy/test.", DeploymentValidationCodes.InvalidJsonPatch));

                    if (!op.TryGetProperty("path", out var ptrEl) || ptrEl.ValueKind != JsonValueKind.String || !JsonPointerPattern.IsMatch(ptrEl.GetString() ?? ""))
                        errs.Add(new($"{opPath}.path", "RFC 6902 op requires a valid JSON Pointer 'path' (e.g. /spec/hostPID).", DeploymentValidationCodes.InvalidJsonPatch));

                    if (opName is "add" or "replace" or "test" && !op.TryGetProperty("value", out _))
                        errs.Add(new(opPath, $"RFC 6902 op '{opName}' requires a 'value'.", DeploymentValidationCodes.InvalidJsonPatch));

                    var fromPresent = op.TryGetProperty("from", out var fromEl) && fromEl.ValueKind != JsonValueKind.Null;
                    if (fromPresent && (fromEl.ValueKind != JsonValueKind.String || !JsonPointerPattern.IsMatch(fromEl.GetString() ?? "")))
                        errs.Add(new($"{opPath}.from", "RFC 6902 'from' must be a valid JSON Pointer (e.g. /spec/x).", DeploymentValidationCodes.InvalidJsonPatch));
                    if (opName is "move" or "copy" && !fromPresent)
                        errs.Add(new(opPath, $"RFC 6902 op '{opName}' requires a 'from' JSON Pointer.", DeploymentValidationCodes.InvalidJsonPatch));
                }
                i++;
            }
        }

        return new UnsupportedPodPatch
        {
            RawJson = patchEl.GetRawText(),
            Body = patchEl.Clone(),
        };
    }

    private static void ValidateWorkloadIdUniqueness(List<WorkloadManifest> workloads, List<DeploymentValidationError> errs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < workloads.Count; i++)
        {
            var id = workloads[i].Id;
            if (string.IsNullOrEmpty(id))
                continue;
            if (!SlugPattern.IsMatch(id))
                errs.Add(new($"deployment.workloads[{i}].id", $"workload id '{id}' must match ^[a-z0-9-]{{1,40}}$.", DeploymentValidationCodes.InvalidWorkloadId));
            if (!seen.Add(id))
                errs.Add(new($"deployment.workloads[{i}].id", $"Duplicate workload id '{id}' within the deployment.", DeploymentValidationCodes.DuplicateWorkloadId));
        }
    }

    private static void ValidateWorkloadSemantics(
        WorkloadManifest wl, JsonElement wlEl, string path,
        HashSet<string> siblingIds, HashSet<string> bootstrapKeys, List<DeploymentValidationError> errs)
    {
        if (wlEl.ValueKind != JsonValueKind.Object)
            return;

        if ((wl.Kind == WorkloadKind.PerNode || wl.Kind == WorkloadKind.ScanPerNode) && wl.Volumes.Count > 0)
        {
            errs.Add(new(
                $"{path}.volumes",
                "kind 'per-node' forbids volumes[] (a DaemonSet cannot share one RWO PVC across nodes; use privileges.nodeLocalState).",
                DeploymentValidationCodes.PerNodeVolumesForbidden));
        }

        if (wl.Privileges?.FsGroup is not null && wl.Volumes.Count == 0)
        {
            errs.Add(new(
                $"{path}.privileges.fsGroup",
                "privileges.fsGroup owns the volumes[] declared by the workload, and there are none.",
                DeploymentValidationCodes.FsGroupWithoutVolumes));
        }

        var reserved = new Dictionary<string, WorkloadParameterType>(StringComparer.Ordinal);
        foreach (var (name, type) in WorkloadReservedParameters.For(wl, provider: null))
            reserved.TryAdd(name, type);

        var declared = new HashSet<string>(StringComparer.Ordinal);
        var declaredByName = new Dictionary<string, (WorkloadParameter Param, int Index)>(StringComparer.Ordinal);
        for (var i = 0; i < wl.Parameters.Count; i++)
        {
            var p = wl.Parameters[i];
            if (string.IsNullOrEmpty(p.Name))
                continue;
            declared.Add(p.Name);
            declaredByName.TryAdd(p.Name, (p, i));

            if (reserved.TryGetValue(p.Name, out var reservedType))
                ValidateReservedOverride(p, reservedType, i, path, errs);

            if (string.Equals(p.Name, "dbusHostPath", StringComparison.Ordinal)
                && p.Default is { } dbusDefault && !WorkloadPathPolicy.IsAllowedDbusPath(dbusDefault))
            {
                errs.Add(new(
                    $"{path}.parameters[{i}].default",
                    $"dbusHostPath default '{dbusDefault}' must be under /run/** or /var/run/**, {WorkloadHostPathRefusal.Display} (a manifest cannot widen the mount).",
                    DeploymentValidationCodes.DisallowedPathPrefix));
            }
        }

        ValidateParameterSwitches(wl, path, errs);

        bool Resolves(string name) => declared.Contains(name) || reserved.ContainsKey(name);

        if (wl.Privileges is { } priv)
        {
            var seenDeviceParams = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < priv.Devices.Count; i++)
            {
                var param = priv.Devices[i].Parameter;
                if (string.IsNullOrEmpty(param))
                    continue;

                if (declaredByName.TryGetValue(param, out var decl))
                {
                    if (decl.Param.Type != WorkloadParameterType.DevicePath)
                        errs.Add(new(
                            $"{path}.privileges.devices[{i}].parameter",
                            $"device parameter '{param}' must be a declared parameter of type 'devicePath' (its value is constrained to /dev/**); it is declared as '{ParameterTypeName(decl.Param.Type)}'.",
                            DeploymentValidationCodes.DeviceParameterNotDevicePath));
                }
                else if (reserved.ContainsKey(param))
                {
                    errs.Add(new(
                        $"{path}.privileges.devices[{i}].parameter",
                        $"device parameter '{param}' is a reserved synthesized name and cannot back a host device; declare a 'devicePath' parameter instead.",
                        DeploymentValidationCodes.DeviceParameterNotDevicePath));
                }
                else
                {
                    errs.Add(new(
                        $"{path}.privileges.devices[{i}].parameter",
                        $"device references undeclared parameter '{param}'.",
                        DeploymentValidationCodes.UndeclaredParameterReference));
                }

                if (!seenDeviceParams.Add(param))
                    errs.Add(new(
                        $"{path}.privileges.devices",
                        $"Duplicate device parameter '{param}'.",
                        DeploymentValidationCodes.DuplicateDeviceParameter));
            }
        }

        if (wl.Privileges is { } hpPriv)
        {
            for (var i = 0; i < hpPriv.HostPaths.Count; i++)
            {
                var param = hpPriv.HostPaths[i].Parameter;
                if (string.IsNullOrEmpty(param))
                    continue;

                if (declaredByName.TryGetValue(param, out var decl))
                {
                    if (decl.Param.Type != WorkloadParameterType.HostPath)
                        errs.Add(new(
                            $"{path}.privileges.hostPaths[{i}].parameter",
                            $"hostPaths parameter '{param}' must be a declared parameter of type 'hostPath' (its value is constrained to /sys/**, /proc/**, /run/**, /var/run/**, /dev/serial, or /dev/dri); it is declared as '{ParameterTypeName(decl.Param.Type)}'.",
                            DeploymentValidationCodes.HostPathParameterNotHostPath));
                }
                else if (reserved.ContainsKey(param))
                {
                    errs.Add(new(
                        $"{path}.privileges.hostPaths[{i}].parameter",
                        $"hostPaths parameter '{param}' is a reserved synthesized name and cannot back a host mount; declare a 'hostPath' parameter instead.",
                        DeploymentValidationCodes.HostPathParameterNotHostPath));
                }
                else
                {
                    errs.Add(new(
                        $"{path}.privileges.hostPaths[{i}].parameter",
                        $"hostPaths references undeclared parameter '{param}'.",
                        DeploymentValidationCodes.UndeclaredParameterReference));
                }
            }
        }

        if (wl.Network is { } attNet)
        {
            var seenAttachmentParams = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < attNet.Attachments.Count; i++)
            {
                var param = attNet.Attachments[i].Parameter;
                if (string.IsNullOrEmpty(param))
                    continue;

                if (declaredByName.TryGetValue(param, out var decl))
                {
                    if (decl.Param.Type != WorkloadParameterType.NetworkAttachment)
                        errs.Add(new(
                            $"{path}.network.attachments[{i}].parameter",
                            $"network attachment parameter '{param}' must be a declared parameter of type 'networkAttachment'; it is declared as '{ParameterTypeName(decl.Param.Type)}'.",
                            DeploymentValidationCodes.NetworkAttachmentParameterType));
                }
                else if (reserved.ContainsKey(param))
                {
                    errs.Add(new(
                        $"{path}.network.attachments[{i}].parameter",
                        $"network attachment parameter '{param}' is a reserved synthesized name and cannot back an attachment; declare a 'networkAttachment' parameter instead.",
                        DeploymentValidationCodes.NetworkAttachmentParameterType));
                }
                else
                {
                    errs.Add(new(
                        $"{path}.network.attachments[{i}].parameter",
                        $"network attachment references undeclared parameter '{param}'.",
                        DeploymentValidationCodes.NetworkAttachmentParameterMissing));
                }

                if (!seenAttachmentParams.Add(param))
                    errs.Add(new(
                        $"{path}.network.attachments",
                        $"Duplicate network attachment parameter '{param}'.",
                        DeploymentValidationCodes.NetworkAttachmentDuplicateParameter));
            }
        }

        for (var i = 0; i < wl.ConfigMapMounts.Count; i++)
        {
            var param = wl.ConfigMapMounts[i].Parameter;
            if (string.IsNullOrEmpty(param))
                continue;

            if (declaredByName.TryGetValue(param, out var decl))
            {
                if (decl.Param.Type != WorkloadParameterType.Text)
                    errs.Add(new(
                        $"{path}.configMapMount[{i}].parameter",
                        $"configMapMount parameter '{param}' must be a declared parameter of type 'text' (it carries the operator-created ConfigMap's name); it is declared as '{ParameterTypeName(decl.Param.Type)}'.",
                        DeploymentValidationCodes.ConfigMapParameterNotText));
                else if (!string.IsNullOrWhiteSpace(decl.Param.Default))
                    errs.Add(new(
                        $"{path}.parameters[{decl.Index}].default",
                        $"configMapMount parameter '{param}' may not declare a default: the ConfigMap is one the operator creates and names.",
                        DeploymentValidationCodes.ConfigMapParameterDefault));
            }
            else if (reserved.ContainsKey(param))
            {
                errs.Add(new(
                    $"{path}.configMapMount[{i}].parameter",
                    $"configMapMount parameter '{param}' is a reserved synthesized name with fixed semantics and cannot carry a ConfigMap name; declare a 'text' parameter instead.",
                    DeploymentValidationCodes.ConfigMapParameterNotText));
            }
            else
            {
                errs.Add(new(
                    $"{path}.configMapMount[{i}].parameter",
                    $"configMapMount references undeclared parameter '{param}'.",
                    DeploymentValidationCodes.UndeclaredParameterReference));
            }
        }

        var multiDeviceSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var device in wl.Privileges?.Devices ?? [])
        {
            if (!string.IsNullOrEmpty(device.Parameter) && device.MaxCount > 1)
                multiDeviceSlots[device.Parameter] = device.MaxCount;
        }

        for (var i = 0; i < wl.Env.Count; i++)
        {
            var from = wl.Env[i].From;
            var fromPath = $"{path}.env[{i}].from";
            if (string.IsNullOrEmpty(from))
                continue;

            if (from.StartsWith("parameter:", StringComparison.Ordinal))
            {
                var name = from["parameter:".Length..];
                if (multiDeviceSlots.TryGetValue(name, out var maxCount))
                    errs.Add(new(
                        fromPath,
                        $"env cannot bind to 'parameter:{name}': it backs a device slot with maxCount {maxCount}, so the declared key binds no device — "
                        + $"the chosen paths live only in the synthesized '{name}[0]'…'{name}[{maxCount - 1}]' keys, which the renderer reads instead, and this variable would be empty. "
                        + "Declare a separate parameter for the value the workload needs, or set maxCount to 1.",
                        DeploymentValidationCodes.EnvBoundToMultiDeviceSlot));
                else if (!Resolves(name))
                    errs.Add(new(fromPath, $"env references undeclared parameter '{name}'.", DeploymentValidationCodes.UndeclaredParameterReference));
            }
            else if (from.StartsWith("bootstrap:", StringComparison.Ordinal))
            {
                ValidateBootstrapKey(from["bootstrap:".Length..], fromPath, siblingIds, bootstrapKeys, errs);
            }
            else if (from.StartsWith("literal:", StringComparison.Ordinal))
            {
            }
            else
            {
                errs.Add(new(fromPath, $"env.from '{from}' must start with 'parameter:', 'bootstrap:' or 'literal:'.", DeploymentValidationCodes.InvalidEnvSource));
            }
        }
    }

    private static void ValidateBootstrapKey(
        string key, string fromPath, HashSet<string> siblingIds, HashSet<string> bootstrapKeys, List<DeploymentValidationError> errs)
    {
        if (key.StartsWith("workloadHost:", StringComparison.Ordinal))
        {
            var sibling = key["workloadHost:".Length..];
            if (!siblingIds.Contains(sibling))
                errs.Add(new(fromPath, $"bootstrap:workloadHost:{sibling} references a workload not declared in this deployment.", DeploymentValidationCodes.UnknownBootstrapKey));
            return;
        }
        if (!bootstrapKeys.Contains(key))
        {
            errs.Add(new(
                fromPath,
                $"bootstrap key '{key}' is not one the library or a registered extension validator declares. Declared keys: "
                + $"{string.Join(", ", bootstrapKeys.Order(StringComparer.Ordinal))}, workloadHost:<id>.",
                DeploymentValidationCodes.UnknownBootstrapKey));
        }
    }

    private static void ValidateReservedOverride(
        WorkloadParameter p, WorkloadParameterType expectedType, int index, string path, List<DeploymentValidationError> errs)
    {

        var requiredIsAllowed = string.Equals(
            p.Name, WorkloadParameterResolver.NodeSelectorParameterName, StringComparison.Ordinal);
        var setsSwitchKey = p.Widget is not null || p.EnumLabels is not null || p.Reveals is not null;
        var violates = (p.Required && !requiredIsAllowed) || p.Type != expectedType || setsSwitchKey;
        if (violates)
        {
            errs.Add(new(
                $"{path}.parameters[{index}]",
                $"parameter '{p.Name}' reuses a reserved synthesized name; only 'default'/'hint' may be overridden (type/required/visibility, including 'widget', 'enumLabels' and 'reveals', are fixed).",
                DeploymentValidationCodes.ReservedParameterOverride));
        }

        if (string.Equals(p.Name, WorkloadParameterResolver.UserNamespaceParameterName, StringComparison.Ordinal)
            && p.Default is { } userNamespaceDefault
            && !(bool.TryParse(userNamespaceDefault, out var userNamespaceOn) && !userNamespaceOn))
        {
            errs.Add(new($"{path}.parameters[{index}]",
                $"parameter '{p.Name}' may not change its default: the default stays 'false'.",
                DeploymentValidationCodes.ReservedParameterOverride));
        }

        if (WorkloadReservedParameters.IsHostPortParameter(p.Name) && p.Default is not null)
        {
            errs.Add(new($"{path}.parameters[{index}]",
                $"parameter '{p.Name}' may not declare a default: publishing a port on the host's interfaces is the operator's choice.",
                DeploymentValidationCodes.ReservedParameterOverride));
        }

        if (p.Name.StartsWith("existingClaim.", StringComparison.Ordinal) && p.Default is not null)
        {
            errs.Add(new($"{path}.parameters[{index}]",
                $"parameter '{p.Name}' may not declare a default: a restore binding names an existing volume and only the operator may choose it.",
                DeploymentValidationCodes.ReservedParameterOverride));
        }
    }

    private static bool RefHasTag(string reference)
    {
        var lastSlash = reference.LastIndexOf('/');
        var lastSegment = lastSlash >= 0 ? reference[(lastSlash + 1)..] : reference;
        return lastSegment.Contains(':');
    }

    private static WorkloadKind? ParseKind(string? s) => s switch
    {
        "service" => WorkloadKind.Service,
        "single-node" => WorkloadKind.SingleNode,
        "per-node" => WorkloadKind.PerNode,
        "scan-per-node" => WorkloadKind.ScanPerNode,
        _ => null,
    };

    private static WorkloadCapability? ParseCapability(string? s) => s switch
    {
        "NET_ADMIN" => WorkloadCapability.NetAdmin,
        "NET_RAW" => WorkloadCapability.NetRaw,
        "SYS_ADMIN" => WorkloadCapability.SysAdmin,
        "NET_BIND_SERVICE" => WorkloadCapability.NetBindService,
        _ => null,
    };

    private static VolumeCriticality? ParseCriticality(string? s) => s switch
    {
        "cache" => VolumeCriticality.Cache,
        "recoverable" => VolumeCriticality.Recoverable,
        "critical" => VolumeCriticality.Critical,
        _ => null,
    };

    private static WorkloadHealthMode? ParseHealthMode(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : new WorkloadHealthMode(s);

    private static HostRequirementKind? ParseHostRequirementKind(string? s) => s switch
    {
        "dbus-service" => HostRequirementKind.DbusService,
        "kernel-module" => HostRequirementKind.KernelModule,
        "udev-serial" => HostRequirementKind.UdevSerial,
        "multicast" => HostRequirementKind.Multicast,
        _ => null,
    };

    private static string HostRequirementKindName(HostRequirementKind kind) => kind switch
    {
        HostRequirementKind.DbusService => "dbus-service",
        HostRequirementKind.KernelModule => "kernel-module",
        HostRequirementKind.UdevSerial => "udev-serial",
        HostRequirementKind.Multicast => "multicast",
        _ => kind.ToString(),
    };

    private static string ParameterTypeName(WorkloadParameterType type) => type switch
    {
        WorkloadParameterType.Text => "text",
        WorkloadParameterType.Integer => "integer",
        WorkloadParameterType.Boolean => "boolean",
        WorkloadParameterType.Password => "password",
        WorkloadParameterType.Enum => "enum",
        WorkloadParameterType.DevicePath => "devicePath",
        WorkloadParameterType.NodeSelector => "nodeSelector",
        WorkloadParameterType.NetworkInterface => "networkInterface",
        WorkloadParameterType.StorageSize => "storageSize",
        WorkloadParameterType.StorageClass => "storageClass",
        WorkloadParameterType.HostPath => "hostPath",
        WorkloadParameterType.NetworkAttachment => "networkAttachment",
        _ => type.ToString(),
    };

    private static WorkloadParameterType? ParseParameterType(string? s) => s switch
    {
        "text" => WorkloadParameterType.Text,
        "integer" => WorkloadParameterType.Integer,
        "boolean" => WorkloadParameterType.Boolean,
        "password" => WorkloadParameterType.Password,
        "enum" => WorkloadParameterType.Enum,
        "devicePath" => WorkloadParameterType.DevicePath,
        "nodeSelector" => WorkloadParameterType.NodeSelector,
        "networkInterface" => WorkloadParameterType.NetworkInterface,
        "storageSize" => WorkloadParameterType.StorageSize,
        "storageClass" => WorkloadParameterType.StorageClass,
        "hostPath" => WorkloadParameterType.HostPath,
        "networkAttachment" => WorkloadParameterType.NetworkAttachment,
        _ => null,
    };

    private static WorkloadDeviceKind? ParseDeviceKind(string? s) => s switch
    {
        "usb-serial" => WorkloadDeviceKind.UsbSerial,
        "gpu-render" => WorkloadDeviceKind.GpuRender,
        "accelerator" => WorkloadDeviceKind.Accelerator,
        _ => null,
    };

    private static WorkloadDeviceGroup? ParseDeviceGroup(string? s) => s switch
    {
        "auto" => WorkloadDeviceGroup.Auto,
        "none" => WorkloadDeviceGroup.None,
        _ => null,
    };

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    private static bool IsValidDeviceMaxCount(int maxCount) => maxCount is >= 1 and <= 8;

    private static bool IsTcpOrUdp(string protocol) =>
        string.Equals(protocol, "tcp", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase);

    private static string? ReadRequiredString(JsonElement obj, string field, string path, List<DeploymentValidationError> errs)
    {
        if (!obj.TryGetProperty(field, out var el))
        {
            errs.Add(new(path, $"Required field '{field}' is missing.", DeploymentValidationCodes.MissingField));
            return null;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            errs.Add(new(path, $"Field '{field}' must be a string but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
            return null;
        }
        var v = el.GetString();
        if (string.IsNullOrWhiteSpace(v))
        {
            errs.Add(new(path, $"Field '{field}' must not be empty.", DeploymentValidationCodes.MissingField));
            return null;
        }
        return RejectControlCharacters(v, field, path, errs);
    }

    private static string? RejectControlCharacters(
        string? value, string field, string path, List<DeploymentValidationError> errs)
    {
        if (value is null)
        {
            return null;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsControl(value[i]))
            {
                continue;
            }

            errs.Add(new(
                path,
                $"Field '{field}' contains a control character (U+{(int)value[i]:X4}) at position {i}.",
                DeploymentValidationCodes.ControlCharacterInString));
            return null;
        }

        return value;
    }

    private static string? ReadOptionalStringChecked(JsonElement obj, string field, string path, List<DeploymentValidationError> errs)
    {
        if (!obj.TryGetProperty(field, out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        if (el.ValueKind != JsonValueKind.String)
        {
            errs.Add(new(path, $"Field '{field}' must be a string but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
            return null;
        }
        return RejectControlCharacters(el.GetString(), field, path, errs);
    }

    private static string? NormalizeSection(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    private static WorkloadParameterWidget? ReadOptionalWidget(JsonElement p, string path, List<DeploymentValidationError> errs)
    {
        if (!p.TryGetProperty("widget", out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.String)
        {
            errs.Add(new(path, $"Field 'widget' must be a string but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
            return null;
        }
        var raw = el.GetString();
        WorkloadParameterWidget? widget = raw switch { "select" => WorkloadParameterWidget.Select, "radio" => WorkloadParameterWidget.Radio, _ => null };
        if (widget is null) errs.Add(new(path, $"Unknown value '{raw}' for 'widget'; expected 'select' or 'radio'.", DeploymentValidationCodes.UnknownEnumValue));
        return widget;
    }

    private static IReadOnlyDictionary<string, string>? ReadEnumLabels(JsonElement p, string path, List<DeploymentValidationError> errs)
    {
        if (!p.TryGetProperty("enumLabels", out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.Object)
        {
            errs.Add(new(path, $"Field 'enumLabels' must be a JSON object but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
            return null;
        }
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in el.EnumerateObject())
        {
            var entryPath = $"{path}.{entry.Name}";
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                errs.Add(new(entryPath, $"The label for '{entry.Name}' must be a non-empty string but was {entry.Value.ValueKind}.", DeploymentValidationCodes.WrongType));
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.Value.GetString()))
            {
                errs.Add(new(entryPath, $"The label for '{entry.Name}' must be a non-blank string.", DeploymentValidationCodes.WrongType));
                continue;
            }
            if (RejectControlCharacters(entry.Value.GetString(), "enumLabels", entryPath, errs) is { } label) labels[entry.Name] = label;
        }
        return labels;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? ReadReveals(JsonElement p, string path, List<DeploymentValidationError> errs)
    {
        if (!p.TryGetProperty("reveals", out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.Object)
        {
            errs.Add(new(path, $"Field 'reveals' must be a JSON object but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
            return null;
        }
        var reveals = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in el.EnumerateObject())
        {
            var entryPath = $"{path}.{entry.Name}";
            if (entry.Value.ValueKind != JsonValueKind.Array)
            {
                errs.Add(new(entryPath, $"Value for reveals key '{entry.Name}' must be an array but was {entry.Value.ValueKind}.", DeploymentValidationCodes.WrongType));
                continue;
            }
            var sections = new List<string>();
            var i = 0;
            foreach (var item in entry.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } section && !string.IsNullOrWhiteSpace(section))
                    sections.Add(NormalizeSection(section)!);
                else
                    errs.Add(new($"{entryPath}[{i}]", $"Each section in reveals key '{entry.Name}' must be a non-blank string but was {item.ValueKind}.", DeploymentValidationCodes.WrongType));
                i++;
            }
            reveals[entry.Name] = sections;
        }
        return reveals;
    }

    private static void ValidateSwitchKeys(string name, WorkloadParameterType type, IReadOnlyList<string>? enumValues,
        WorkloadParameterWidget? widget, IReadOnlyDictionary<string, string>? enumLabels,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? reveals, string pPath, List<DeploymentValidationError> errs)
    {
        if (type != WorkloadParameterType.Enum)
        {
            foreach (var (key, present) in new[] { ("widget", widget is not null), ("enumLabels", enumLabels is not null), ("reveals", reveals is not null) })
                if (present) errs.Add(new($"{pPath}.{key}", $"parameter '{name}' of type '{ParameterTypeName(type)}' may not declare '{key}'; it belongs to 'enum' parameters only.", DeploymentValidationCodes.SwitchKeyOnNonEnumParameter));
            return;
        }
        if (enumValues is not { Count: > 0 }) return;
        RejectKeysOutsideEnumValues(name, "enumLabels", enumLabels?.Keys, enumValues, pPath, errs);
        RejectKeysOutsideEnumValues(name, "reveals", reveals?.Keys, enumValues, pPath, errs);
    }

    private static void RejectKeysOutsideEnumValues(string name, string field, IEnumerable<string>? keys,
        IReadOnlyList<string> enumValues, string pPath, List<DeploymentValidationError> errs)
    {
        foreach (var key in keys ?? [])
            if (!enumValues.Contains(key, StringComparer.Ordinal))
                errs.Add(new($"{pPath}.{field}.{key}", $"parameter '{name}' {field} key '{key}' is not one of its enumValues.", DeploymentValidationCodes.SwitchKeyNotAnEnumValue));
    }

    private static void ValidateParameterSwitches(WorkloadManifest wl, string path, List<DeploymentValidationError> errs)
    {
        var sections = wl.Parameters.Select(p => p.Section).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var switchBySection = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < wl.Parameters.Count; i++)
        {
            var p = wl.Parameters[i];
            if (p.Reveals is null) continue;
            var revealsPath = $"{path}.parameters[{i}].reveals";
            foreach (var section in p.Reveals.Values.SelectMany(names => names).Distinct(StringComparer.Ordinal))
            {
                if (!sections.Contains(section)) errs.Add(new(revealsPath, $"parameter '{p.Name}' reveals section '{section}', which no parameter of this workload declares.", DeploymentValidationCodes.RevealedSectionNotDeclared));
                if (string.Equals(section, p.Section, StringComparison.Ordinal)) errs.Add(new(revealsPath, $"parameter '{p.Name}' reveals its own section '{section}', which would hide the switch itself.", DeploymentValidationCodes.SwitchRevealsOwnSection));
                if (!switchBySection.TryAdd(section, p.Name)) errs.Add(new(revealsPath, $"section '{section}' is revealed by both '{switchBySection[section]}' and '{p.Name}'; a section has one switch.", DeploymentValidationCodes.SectionRevealedByTwoSwitches));
            }
        }
        for (var i = 0; i < wl.Parameters.Count; i++)
        {
            var p = wl.Parameters[i];
            if (p.Type != WorkloadParameterType.Enum || p.Reveals is null || !p.Reveals.Values.Any(names => names.Count > 0) || p.Section is not { } section)
                continue;

            for (var otherIndex = 0; otherIndex < wl.Parameters.Count; otherIndex++)
            {
                if (otherIndex == i) continue;
                var other = wl.Parameters[otherIndex];
                if (other.Reveals?.Values.Any(names => names.Contains(section, StringComparer.Ordinal)) == true)
                {
                    errs.Add(new($"{path}.parameters[{i}].section",
                        $"switch '{p.Name}' sits in section '{section}', which is revealed by parameter '{other.Name}'; nested switches are not supported.",
                        DeploymentValidationCodes.SwitchInGatedSection));
                    break;
                }
            }
        }
        for (var i = 0; i < wl.Parameters.Count; i++)
        {
            var p = wl.Parameters[i];
            if (p.Required && p.Section is { } section && switchBySection.TryGetValue(section, out var switchName))
                errs.Add(new($"{path}.parameters[{i}].required", $"parameter '{p.Name}' is required but sits in section '{section}', which switch '{switchName}' can hide.", DeploymentValidationCodes.RequiredParameterInGatedSection));
        }
    }

    private readonly record struct DefaultRead(bool HasScalar, string? Value, JsonValueKind Kind, bool IsIntegerNumber);

    private static DefaultRead ReadDefaultValue(JsonElement obj, string path, List<DeploymentValidationError> errs)
    {
        if (!obj.TryGetProperty("default", out var el))
            return new DefaultRead(false, null, JsonValueKind.Undefined, false);

        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
                return new DefaultRead(false, null, JsonValueKind.Null, false);
            case JsonValueKind.String:
                return new DefaultRead(true, el.GetString(), JsonValueKind.String, false);
            case JsonValueKind.Number:
                return new DefaultRead(true, el.GetRawText(), JsonValueKind.Number, el.TryGetInt64(out _));
            case JsonValueKind.True:
                return new DefaultRead(true, "true", JsonValueKind.True, false);
            case JsonValueKind.False:
                return new DefaultRead(true, "false", JsonValueKind.False, false);
            default:
                errs.Add(new(path, $"parameter 'default' must be a string, integer, boolean, or null but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
                return new DefaultRead(false, null, el.ValueKind, false);
        }
    }

    private static int? ReadRequiredInt(JsonElement obj, string field, string path, List<DeploymentValidationError> errs)
    {
        if (!obj.TryGetProperty(field, out var el))
        {
            errs.Add(new(path, $"Required field '{field}' is missing.", DeploymentValidationCodes.MissingField));
            return null;
        }
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
        {
            errs.Add(new(path, $"Field '{field}' must be an integer but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
            return null;
        }
        return v;
    }

    private static bool ReadOptionalBoolChecked(JsonElement obj, string field, string path, List<DeploymentValidationError> errs)
    {
        if (!obj.TryGetProperty(field, out var el) || el.ValueKind == JsonValueKind.Null)
            return false;
        if (el.ValueKind == JsonValueKind.True)
            return true;
        if (el.ValueKind == JsonValueKind.False)
            return false;
        errs.Add(new(path, $"Field '{field}' must be a boolean but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
        return false;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string field, string path, List<DeploymentValidationError> errs)
    {
        var list = new List<string>();
        if (!obj.TryGetProperty(field, out var el))
            return list;
        if (el.ValueKind != JsonValueKind.Array)
        {
            errs.Add(new(path, $"Field '{field}' must be an array but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
            return list;
        }
        var i = 0;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
            {
                if (RejectControlCharacters(s, field, $"{path}[{i}]", errs) is { } checkedString)
                    list.Add(checkedString);
            }
            else
                errs.Add(new($"{path}[{i}]", $"Each entry of '{field}' must be a non-empty string but was {item.ValueKind}.", DeploymentValidationCodes.WrongType));
            i++;
        }
        return list;
    }

    private static TValue ReadRequiredOpen<TValue>(
        JsonElement obj, string field, string path, List<DeploymentValidationError> errs,
        Func<string?, TValue?> map, TValue fallback, out bool read) where TValue : struct
    {
        read = false;
        if (!obj.TryGetProperty(field, out var el))
        {
            errs.Add(new(path, $"Required field '{field}' is missing.", DeploymentValidationCodes.MissingField));
            return fallback;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            errs.Add(new(path, $"Field '{field}' must be a string but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
            return fallback;
        }
        var raw = el.GetString();
        if (map(raw) is { } parsed)
        {
            read = true;
            return parsed;
        }

        errs.Add(new(
            path,
            string.IsNullOrWhiteSpace(raw)
                ? $"Field '{field}' must not be empty."
                : $"Unknown value '{raw}' for '{field}'.",
            string.IsNullOrWhiteSpace(raw) ? DeploymentValidationCodes.MissingField : DeploymentValidationCodes.UnknownEnumValue));
        return fallback;
    }

    private static TEnum ReadRequiredEnum<TEnum>(
        JsonElement obj, string field, string path, List<DeploymentValidationError> errs,
        Func<string?, TEnum?> map, TEnum fallback) where TEnum : struct, Enum
    {
        if (!obj.TryGetProperty(field, out var el))
        {
            errs.Add(new(path, $"Required field '{field}' is missing.", DeploymentValidationCodes.MissingField));
            return fallback;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            errs.Add(new(path, $"Field '{field}' must be a string but was {el.ValueKind}.", DeploymentValidationCodes.WrongType));
            return fallback;
        }
        var raw = el.GetString();
        if (map(raw) is { } parsed)
            return parsed;
        errs.Add(new(path, $"Unknown value '{raw}' for '{field}'.", DeploymentValidationCodes.UnknownEnumValue));
        return fallback;
    }

    private static bool TryGetObject(
        JsonElement parent, string field, string path, bool required,
        List<DeploymentValidationError> errs, out JsonElement obj)
    {
        if (!parent.TryGetProperty(field, out obj))
        {
            if (required)
                errs.Add(new(path, $"Required field '{field}' is missing.", DeploymentValidationCodes.MissingField));
            return false;
        }
        if (obj.ValueKind == JsonValueKind.Null)
        {
            if (required)
                errs.Add(new(path, $"Required field '{field}' must not be null.", DeploymentValidationCodes.MissingField));
            return false;
        }
        if (obj.ValueKind != JsonValueKind.Object)
        {
            errs.Add(new(path, $"Field '{field}' must be a JSON object but was {obj.ValueKind}.", DeploymentValidationCodes.WrongType));
            return false;
        }
        return true;
    }

    private static void RejectUnknownKeys(JsonElement obj, string path, HashSet<string> allowed, List<DeploymentValidationError> errs)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return;
        foreach (var prop in obj.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name))
                errs.Add(new($"{path}.{prop.Name}", $"Unknown key '{prop.Name}' — the deployment contract is closed.", DeploymentValidationCodes.UnknownKey));
        }
    }
}
