namespace Reeve.Workloads.Abstractions.Contracts.Deployment;

/// <summary>One problem found while parsing the manifest <c>deployment</c> object, collected into a
/// list rather than thrown, so validation can extend without restructuring. <see cref="Code"/> is machine-readable (SCREAMING_SNAKE).</summary>
public sealed record DeploymentValidationError(string Path, string Message, string Code)
{
    public override string ToString() => $"{Path}: {Message} ({Code})";
}

/// <summary>Stable, append-only error codes emitted by the deployment-manifest parser, each
/// distinct so a UI/CI can dispatch on the specific rule without substring-matching the message.</summary>
public static class DeploymentValidationCodes
{
    /// <summary>The <c>deployment</c> value (or a nested object) was not a JSON object where one was required.</summary>
    public const string NotAnObject = "DEPLOYMENT_NOT_AN_OBJECT";

    /// <summary>A required field was absent.</summary>
    public const string MissingField = "DEPLOYMENT_MISSING_FIELD";

    /// <summary>A field was present but of the wrong JSON kind (e.g. string where an object was required).</summary>
    public const string WrongType = "DEPLOYMENT_WRONG_TYPE";

    /// <summary><c>schemaVersion</c> was not the single supported value (1).</summary>
    public const string UnsupportedSchemaVersion = "DEPLOYMENT_UNSUPPORTED_SCHEMA_VERSION";

    /// <summary>A closed-enum string was not one of the recognized members.</summary>
    public const string UnknownEnumValue = "DEPLOYMENT_UNKNOWN_ENUM_VALUE";

    /// <summary>The health object is not well-formed for its declared <c>mode</c> (polymorphism violated).</summary>
    public const string InvalidHealthShape = "DEPLOYMENT_INVALID_HEALTH_SHAPE";

    /// <summary>A workload <c>id</c> did not match <c>^[a-z0-9-]{1,40}$</c>.</summary>
    public const string InvalidWorkloadId = "DEPLOYMENT_INVALID_WORKLOAD_ID";

    /// <summary>Two workloads share the same <c>id</c> within one deployment.</summary>
    public const string DuplicateWorkloadId = "DEPLOYMENT_DUPLICATE_WORKLOAD_ID";

    /// <summary><c>image.ref</c> carries a tag (must be repo-only; the digest pins the image).</summary>
    public const string ImageRefHasTag = "DEPLOYMENT_IMAGE_REF_HAS_TAG";

    /// <summary>image.ref carries a character no image reference may contain. Unquoted in generated compose, so a
    /// newline here injects sibling keys into the operator's own file.</summary>
    public const string InvalidImageRef = "DEPLOYMENT_INVALID_IMAGE_REF";

    /// <summary>env[].name is not a C_IDENTIFIER — the grammar Kubernetes itself enforces on an env var name.</summary>
    public const string InvalidEnvName = "DEPLOYMENT_INVALID_ENV_NAME";

    /// <summary>A string field carries a control character. Raised for EVERY string the parser reads rather than
    /// for a named list of fields: the per-field rules are what left <c>mountPath</c> checked only for a leading
    /// '/', and a newline there opened a sibling key in the operator's rendered values file.</summary>
    public const string ControlCharacterInString = "DEPLOYMENT_CONTROL_CHARACTER";

    /// <summary><c>image.digest</c> did not match <c>^sha256:[0-9a-f]{64}$</c>.</summary>
    public const string InvalidImageDigest = "DEPLOYMENT_INVALID_IMAGE_DIGEST";

    /// <summary>An <c>image.platforms[]</c> entry was not a valid <c>os/arch</c> token.</summary>
    public const string InvalidPlatformToken = "DEPLOYMENT_INVALID_PLATFORM_TOKEN";

    /// <summary><c>kind: per-node</c> declared a non-empty <c>volumes[]</c> (a DaemonSet cannot share one RWO PVC).</summary>
    public const string PerNodeVolumesForbidden = "DEPLOYMENT_PER_NODE_VOLUMES_FORBIDDEN";

    /// <summary>A <c>volumes[].id</c> was not a valid slug.</summary>
    public const string InvalidVolumeId = "DEPLOYMENT_INVALID_VOLUME_ID";

    /// <summary>Two volumes in one workload share the same <c>id</c>.</summary>
    public const string DuplicateVolumeId = "DEPLOYMENT_DUPLICATE_VOLUME_ID";

    /// <summary>A <c>volumes[].mountPath</c> was not an absolute path (<c>/…</c>).</summary>
    public const string InvalidMountPath = "DEPLOYMENT_INVALID_MOUNT_PATH";

    /// <summary>A size / cpu / memory value was not a valid Kubernetes quantity.</summary>
    public const string InvalidQuantity = "DEPLOYMENT_INVALID_QUANTITY";

    /// <summary>A <c>volumes[].accessMode</c> was not <c>rwo</c>.</summary>
    public const string InvalidAccessMode = "DEPLOYMENT_INVALID_ACCESS_MODE";

    /// <summary><c>privileges.capabilities[]</c> contained a duplicate.</summary>
    public const string DuplicateCapability = "DEPLOYMENT_DUPLICATE_CAPABILITY";

    /// <summary>A path-typed parameter default violated its allowlisted prefix policy.</summary>
    public const string DisallowedPathPrefix = "DEPLOYMENT_DISALLOWED_PATH_PREFIX";

    /// <summary><c>nodeLocalState.subdir</c> was not a safe relative path (leading <c>/</c> or a <c>..</c> segment).</summary>
    public const string InvalidNodeLocalSubdir = "DEPLOYMENT_INVALID_NODE_LOCAL_SUBDIR";

    /// <summary><c>privileges.fsGroup</c> was outside 1–65534. Group 0 is root's, and 65535 is the unmapped id.</summary>
    public const string InvalidFsGroup = "DEPLOYMENT_INVALID_FS_GROUP";

    /// <summary><c>privileges.fsGroup</c> was declared on a workload with no <c>volumes[]</c> for it to own.</summary>
    public const string FsGroupWithoutVolumes = "DEPLOYMENT_FS_GROUP_WITHOUT_VOLUMES";

    /// <summary>A port number was outside 1–65535.</summary>
    public const string InvalidPort = "DEPLOYMENT_INVALID_PORT";

    /// <summary><c>health.heartbeatSeconds</c> was not &gt; 0.</summary>
    public const string InvalidHeartbeat = "DEPLOYMENT_INVALID_HEARTBEAT";

    /// <summary>Two <c>network.ports[]</c> entries share a <c>name</c>.</summary>
    public const string DuplicatePortName = "DEPLOYMENT_DUPLICATE_PORT_NAME";

    /// <summary>A <c>network.ports[].name</c> was not an IANA_SVC_NAME (the k8s port-name grammar), which
    /// the API server would otherwise reject at apply time, long after the manifest passed release CI.</summary>
    public const string InvalidPortName = "DEPLOYMENT_INVALID_PORT_NAME";

    /// <summary>A port/metrics <c>protocol</c> was not <c>tcp</c>/<c>udp</c>.</summary>
    public const string InvalidPortProtocol = "DEPLOYMENT_INVALID_PORT_PROTOCOL";

    /// <summary><c>observability.metrics.path</c> did not start with <c>/</c>.</summary>
    public const string InvalidMetricsPath = "DEPLOYMENT_INVALID_METRICS_PATH";

    /// <summary>An <c>env[].from</c> / <c>devices[].parameter</c> referenced a parameter neither declared nor reserved.</summary>
    public const string UndeclaredParameterReference = "DEPLOYMENT_UNDECLARED_PARAMETER_REFERENCE";

    /// <summary>An author-declared parameter reused a reserved name but tried to override more than default/hint.</summary>
    public const string ReservedParameterOverride = "DEPLOYMENT_RESERVED_PARAMETER_OVERRIDE";

    /// <summary>Two <c>parameters[]</c> entries share a <c>name</c>.</summary>
    public const string DuplicateParameterName = "DEPLOYMENT_DUPLICATE_PARAMETER_NAME";

    /// <summary>An <c>enum</c>-typed parameter supplied no (or empty) <c>enumValues</c>.</summary>
    public const string MissingEnumValues = "DEPLOYMENT_MISSING_ENUM_VALUES";

    /// <summary>A parameter <c>default</c> was not consistent with its declared type (bad int / enum default not in <c>enumValues</c>).</summary>
    public const string InvalidParameterDefault = "DEPLOYMENT_INVALID_PARAMETER_DEFAULT";

    /// <summary>An <c>env[].from</c> did not start with <c>parameter:</c>/<c>bootstrap:</c>/<c>literal:</c>.</summary>
    public const string InvalidEnvSource = "DEPLOYMENT_INVALID_ENV_SOURCE";

    /// <summary>An <c>env[].from</c> referenced a <c>bootstrap:</c> key neither the library nor a registered extension validator declares (or an unknown sibling workload host).</summary>
    public const string UnknownBootstrapKey = "DEPLOYMENT_UNKNOWN_BOOTSTRAP_KEY";

    /// <summary><c>unsupportedPodPatch</c> was not a well-formed RFC 6902 array (bad op, non-pointer path, missing value/from).</summary>
    public const string InvalidJsonPatch = "DEPLOYMENT_INVALID_JSON_PATCH";

    /// <summary>A <c>hostRequirements[]</c> entry that maps by name (<c>dbus-service</c>/<c>kernel-module</c>) omitted <c>name</c>.</summary>
    public const string MissingHostRequirementName = "DEPLOYMENT_MISSING_HOST_REQUIREMENT_NAME";

    /// <summary>An unknown key appeared inside a closed object (the <c>deployment</c> contract rejects unknown keys).</summary>
    public const string UnknownKey = "DEPLOYMENT_UNKNOWN_KEY";

    /// <summary>A <c>configMapMount[].mountPath</c> was not an absolute path (<c>/…</c>).</summary>
    public const string InvalidConfigMapMountPath = "DEPLOYMENT_INVALID_CONFIGMAP_MOUNT_PATH";

    /// <summary>Two <c>configMapMount[]</c> entries share the same <c>mountPath</c>.</summary>
    public const string DuplicateConfigMapMountPath = "DEPLOYMENT_DUPLICATE_CONFIGMAP_MOUNT_PATH";

    /// <summary>A <c>deployment</c> object was present but declared an empty/absent <c>workloads[]</c> array.</summary>
    public const string NoWorkloads = "DEPLOYMENT_NO_WORKLOADS";

    /// <summary>[SECURITY] A <c>privileges.devices[].parameter</c> resolved to a non-<c>devicePath</c>
    /// parameter, which would sidestep the <c>/dev/**</c> allowlist and let an arbitrary host path through.</summary>
    public const string DeviceParameterNotDevicePath = "DEPLOYMENT_DEVICE_PARAMETER_NOT_DEVICEPATH";

    /// <summary>A <c>configMapMount[].parameter</c> resolved to something other than a declared
    /// <c>text</c> parameter — the mount name must come from an operator-supplied text parameter.</summary>
    public const string ConfigMapParameterNotText = "DEPLOYMENT_CONFIGMAP_PARAMETER_NOT_TEXT";

    /// <summary>A <c>configMapMount</c> parameter declares a default. The ConfigMap is one the operator creates and
    /// names, so a release cannot point the mount at another ConfigMap through a changed default.</summary>
    public const string ConfigMapParameterDefault = "DEPLOYMENT_CONFIGMAP_PARAM_DEFAULT";

    /// <summary><c>image.component</c> was supplied together with a <c>ref</c>/<c>digest</c>/<c>tag</c>
    /// — the two forms are mutually exclusive (a component resolves the app's own image at render).</summary>
    public const string ImageComponentConflict = "DEPLOYMENT_IMAGE_COMPONENT_CONFLICT";

    /// <summary><c>image.component</c> was not a valid slug (<c>^[a-z0-9-]{1,40}$</c>).</summary>
    public const string InvalidImageComponent = "DEPLOYMENT_INVALID_IMAGE_COMPONENT";

    /// <summary>A <c>privileges.hostPaths[].mountPath</c> was not an absolute path (<c>/…</c>).</summary>
    public const string InvalidHostPathMountPath = "DEPLOYMENT_INVALID_HOST_PATH_MOUNT_PATH";

    /// <summary>Two <c>privileges.hostPaths[]</c> entries share the same <c>mountPath</c>.</summary>
    public const string DuplicateHostPathMountPath = "DEPLOYMENT_DUPLICATE_HOST_PATH_MOUNT_PATH";

    /// <summary>[SECURITY] A <c>privileges.hostPaths[].parameter</c> resolved to a non-<c>hostPath</c>
    /// parameter, which would sidestep the host-inspect root allowlist and let an arbitrary host directory through.</summary>
    public const string HostPathParameterNotHostPath = "DEPLOYMENT_HOST_PATH_PARAMETER_NOT_HOST_PATH";

    /// <summary>[SECURITY] A <c>network.attachments[].parameter</c> resolved to a non-<c>networkAttachment</c>
    /// parameter, which would bypass every type-keyed check and attach the pod to an arbitrary operator network.</summary>
    public const string NetworkAttachmentParameterType = "DEPLOYMENT_NETWORK_ATTACHMENT_PARAM_TYPE";

    /// <summary>A <c>network.attachments[].parameter</c> named an undeclared parameter. Own code,
    /// not <see cref="UndeclaredParameterReference"/> — the install wizard needs it to say WHICH control is missing.</summary>
    public const string NetworkAttachmentParameterMissing = "DEPLOYMENT_NETWORK_ATTACHMENT_PARAM_MISSING";

    /// <summary>Two <c>network.attachments[]</c> entries bind the SAME parameter — one value would
    /// back two distinct attachments, and only this rule (keyed on the slot list) catches it.</summary>
    public const string NetworkAttachmentDuplicateParameter = "DEPLOYMENT_NETWORK_ATTACHMENT_DUPLICATE_PARAM";

    /// <summary>A <c>networkAttachment</c> parameter declares a default. The network a workload attaches to is the
    /// operator's to name, so a release cannot move an install to another network through a changed default.</summary>
    public const string InvalidNetworkAttachmentValue = "DEPLOYMENT_INVALID_NETWORK_ATTACHMENT_VALUE";

    /// <summary>devices[].maxCount outside 1–8. Own code, not a generic out-of-range message, so a plugin
    /// author's release CI output names maxCount specifically, not just "something's wrong".</summary>
    public const string InvalidDeviceMaxCount = "DEPLOYMENT_INVALID_DEVICE_MAX_COUNT";

    /// <summary>devices[].deviceKind is not a member of the closed class vocabulary. Own code, not
    /// <see cref="UnknownEnumValue"/> — release CI output must point at deviceKind specifically.</summary>
    public const string InvalidDeviceKind = "DEPLOYMENT_INVALID_DEVICE_KIND";

    /// <summary>devices[].group is neither "none" nor "auto". Own code, not
    /// <see cref="UnknownEnumValue"/> — release CI output must point at group specifically.</summary>
    public const string InvalidDeviceGroup = "DEPLOYMENT_INVALID_DEVICE_GROUP";

    /// <summary>A declared parameter name contains punctuation reserved for synthesized keys ('[', ']', ':').</summary>
    public const string InvalidParameterName = "DEPLOYMENT_INVALID_PARAMETER_NAME";

    /// <summary>Two <c>privileges.devices[]</c> entries bind the SAME parameter — resolution would
    /// synthesize the same indexed keys twice with conflicting maxCounts; only this rule (keyed on the slot list) catches it.</summary>
    public const string DuplicateDeviceParameter = "DEPLOYMENT_DUPLICATE_DEVICE_PARAMETER";

    /// <summary>An <c>env[].from</c> bound to the declared key of a <c>maxCount &gt; 1</c> device slot, which
    /// renders EMPTY: above one device the chosen paths live only in the synthesized indexed keys.</summary>
    public const string EnvBoundToMultiDeviceSlot = "DEPLOYMENT_ENV_BOUND_TO_MULTI_DEVICE_SLOT";

    /// <summary>A field the manifest does not have was found: <c>protocol</c> or <c>apiSchema</c>, which belong in a host
    /// extension (<c>extensions.&lt;namespace&gt;</c>), or <c>health.adapterType</c>, which nothing reads.</summary>
    public const string EvictedManifestField = "DEPLOYMENT_EVICTED_MANIFEST_FIELD";

    /// <summary><c>extensions</c>, or one of its namespace values, was not a JSON object.</summary>
    public const string InvalidExtensionShape = "DEPLOYMENT_INVALID_EXTENSION_SHAPE";

    /// <summary>A host namespace a registered validator declares REQUIRED was absent from a workload.</summary>
    public const string MissingExtensionNamespace = "DEPLOYMENT_MISSING_EXTENSION_NAMESPACE";

    /// <summary><c>widget</c>, <c>enumLabels</c> or <c>reveals</c> on a parameter whose type is not <c>enum</c>.</summary>
    public const string SwitchKeyOnNonEnumParameter = "DEPLOYMENT_SWITCH_KEY_ON_NON_ENUM_PARAMETER";
    /// <summary>A key of <c>enumLabels</c> or <c>reveals</c> that is not one of the parameter's <c>enumValues</c>.</summary>
    public const string SwitchKeyNotAnEnumValue = "DEPLOYMENT_SWITCH_KEY_NOT_AN_ENUM_VALUE";
    /// <summary>A section named in <c>reveals</c> that no parameter of the same workload declares as its <c>section</c>.</summary>
    public const string RevealedSectionNotDeclared = "DEPLOYMENT_REVEALED_SECTION_NOT_DECLARED";
    /// <summary>A switch whose <c>reveals</c> names its own section, which would hide the switch itself.</summary>
    public const string SwitchRevealsOwnSection = "DEPLOYMENT_SWITCH_REVEALS_OWN_SECTION";
    /// <summary>A section named in the <c>reveals</c> of more than one parameter: a gated section has one switch.</summary>
    public const string SectionRevealedByTwoSwitches = "DEPLOYMENT_SECTION_REVEALED_BY_TWO_SWITCHES";
    /// <summary>A <c>required</c> parameter in a gated section. The reconciler holds an instance on a blank required
    /// value and cannot know whether that section is shown.</summary>
    public const string RequiredParameterInGatedSection = "DEPLOYMENT_REQUIRED_PARAMETER_IN_GATED_SECTION";
    /// <summary>A switch in a section another switch reveals; the host form shows the nested switch but not the sections it reveals.</summary>
    public const string SwitchInGatedSection = "DEPLOYMENT_SWITCH_IN_GATED_SECTION";
}
