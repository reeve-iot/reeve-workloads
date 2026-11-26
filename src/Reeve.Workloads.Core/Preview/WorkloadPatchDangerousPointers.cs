using System.Text.Json;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Core.Preview;

/// <summary>Scans a verbatim <c>unsupportedPodPatch</c> body (an RFC 6902 op array) for dangerous JSON pointers
/// and annotates each with Reeve's own words — the backend home of what the host's dialog highlights red. Matches by SEGMENT SHAPE so any container list at any index qualifies. BEST-EFFORT ONLY: <c>KubernetesPatchRevalidator</c> is the enforcement authority. It fail-closes on unknown VOLUME SOURCES, but every pod and container field it guards it guards by name, so a field neither it nor this scan names is both unhighlighted and unenforced — which is how nodeName and the AppArmor surface stayed reachable.</summary>
public static class WorkloadPatchDangerousPointers
{
    private const string ExplainPrivileged = "runs as root on the node";
    private const string ExplainHostPath = "mounts a host directory";
    private const string ExplainHostPid = "shares the host process namespace";
    private const string ExplainHostIpc = "shares the host IPC namespace";
    private const string ExplainHostNetwork = "shares the host network stack";
    private const string ExplainServiceAccount = "runs as a cluster identity";
    private const string ExplainCapabilitiesAdd = "grants extra Linux capabilities";
    private const string ExplainAllowPrivilegeEscalation = "lets the process gain more privileges than it started with";
    private const string ExplainRunAsUserRoot = "runs the container as UID 0 (root)";
    private const string ExplainAutomountToken = "mounts a service-account token (cluster identity)";
    private const string ExplainShareProcessNamespace = "shares the process namespace across containers";
    private const string ExplainHostPort = "binds a host port";
    private const string ExplainRemoveBaseline = "removes a hardened security baseline (reverts to the insecure default)";
    private const string ExplainRunAsRootAllowed = "stops enforcing non-root, so the container may run as root";
    private const string ExplainSeccompUnconfined = "widens the syscall surface (seccomp Unconfined)";
    private const string ExplainWeakenCapabilitiesDrop = "weakens the drop-ALL capability baseline (regains the default capability set)";
    private const string ExplainServiceAccountToken = "injects a cluster identity via a service-account-token volume source";

    /// <summary>Parses <paramref name="patchBody"/> (an RFC 6902 array as raw JSON) and returns a Reeve-authored
    /// annotation for every op whose <c>path</c> touches a dangerous location. Robust: a malformed body / non-array / an op with no string <c>path</c> simply yields no annotation for that entry — the verbatim body is still shown, this only drives the red highlights.</summary>
    public static IReadOnlyList<DangerousPointerAnnotation> Annotate(string? patchBody)
    {
        if (string.IsNullOrWhiteSpace(patchBody))
        {
            return [];
        }

        JsonElement root;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(patchBody);
        }
        catch (JsonException)
        {
            return [];
        }

        using (doc)
        {
            root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var annotations = new List<DangerousPointerAnnotation>();
            var seen = new HashSet<(string Pointer, string Explanation)>();

            foreach (var op in root.EnumerateArray())
            {
                if (op.ValueKind != JsonValueKind.Object
                    || !op.TryGetProperty("path", out var pathEl)
                    || pathEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var pointer = pathEl.GetString();
                if (string.IsNullOrEmpty(pointer))
                {
                    continue;
                }

                var pathExplanation = Classify(pointer);
                if (pathExplanation is not null && seen.Add((pointer, pathExplanation)))
                {
                    annotations.Add(new DangerousPointerAnnotation { JsonPointer = pointer, Explanation = pathExplanation });
                }

                if (IsAddOrReplace(op) && op.TryGetProperty("value", out var valueEl))
                {
                    foreach (var valueExplanation in ScanValueForDangers(valueEl))
                    {
                        if (seen.Add((pointer, valueExplanation)))
                        {
                            annotations.Add(new DangerousPointerAnnotation { JsonPointer = pointer, Explanation = valueExplanation });
                        }
                    }

                    var leafExplanation = ClassifyLeafValue(pointer, valueEl, IsReplace(op));
                    if (leafExplanation is not null && seen.Add((pointer, leafExplanation)))
                    {
                        annotations.Add(new DangerousPointerAnnotation { JsonPointer = pointer, Explanation = leafExplanation });
                    }
                }

                if (IsRemove(op))
                {
                    var removeExplanation = ClassifyBaselineRemoval(pointer);
                    if (removeExplanation is not null && seen.Add((pointer, removeExplanation)))
                    {
                        annotations.Add(new DangerousPointerAnnotation { JsonPointer = pointer, Explanation = removeExplanation });
                    }
                }
            }

            return annotations;
        }
    }

    private static string? Classify(string pointer)
    {
        var segments = DecodeSegments(pointer);
        if (segments.Count == 0)
        {
            return null;
        }

        if (IsPodLevel(segments, "hostPID")) return ExplainHostPid;
        if (IsPodLevel(segments, "hostIPC")) return ExplainHostIpc;
        if (IsPodLevel(segments, "hostNetwork")) return ExplainHostNetwork;
        if (IsPodLevel(segments, "serviceAccountName")) return ExplainServiceAccount;

        if (EndsWith(segments, "securityContext", "privileged")) return ExplainPrivileged;

        if (EndsWith(segments, "securityContext", "allowPrivilegeEscalation")) return ExplainAllowPrivilegeEscalation;

        if (EndsWith(segments, "securityContext", "capabilities", "add")) return ExplainCapabilitiesAdd;

        if (EndsWith(segments, "securityContext", "runAsUser")) return ExplainRunAsUserRoot;

        if (segments.Contains("hostPath")) return ExplainHostPath;

        return null;
    }

    private static string? ClassifyLeafValue(string pointer, JsonElement value, bool isReplace)
    {
        var segments = DecodeSegments(pointer);
        if (segments.Count == 0)
        {
            return null;
        }

        if (IsPodLevel(segments, "shareProcessNamespace") && IsTrueish(value)) return ExplainShareProcessNamespace;
        if (IsPodLevel(segments, "automountServiceAccountToken") && IsTrueish(value)) return ExplainAutomountToken;

        if ((IsPodLevel(segments, "serviceAccountName") || IsPodLevel(segments, "serviceAccount"))
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(value.GetString()))
        {
            return ExplainServiceAccount;
        }

        if (segments.Count >= 3
            && string.Equals(segments[^1], "hostPort", StringComparison.Ordinal)
            && string.Equals(segments[^3], "ports", StringComparison.Ordinal)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var port)
            && port != 0)
        {
            return ExplainHostPort;
        }

        if (EndsWith(segments, "securityContext", "runAsNonRoot") && value.ValueKind == JsonValueKind.False)
        {
            return ExplainRunAsRootAllowed;
        }

        if (EndsWith(segments, "securityContext", "seccompProfile", "type")
            && value.ValueKind == JsonValueKind.String
            && !IsConfiningSeccompType(value.GetString()))
        {
            return ExplainSeccompUnconfined;
        }

        if (EndsWith(segments, "securityContext", "capabilities", "drop")
            && value.ValueKind == JsonValueKind.Array
            && !ArrayContainsAll(value))
        {
            return ExplainWeakenCapabilitiesDrop;
        }

        if (isReplace
            && IsIndex(segments[^1])
            && EndsWithBefore(segments, 1, "securityContext", "capabilities", "drop")
            && !IsAllScalar(value))
        {
            return ExplainWeakenCapabilitiesDrop;
        }

        return null;
    }

    private static bool IsAllScalar(JsonElement value)
        => value.ValueKind == JsonValueKind.String
           && string.Equals(value.GetString(), "ALL", StringComparison.Ordinal);

    private static bool IsAddOrReplace(JsonElement op)
    {
        if (!op.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var opName = opEl.GetString();
        return string.Equals(opName, "add", StringComparison.Ordinal)
            || string.Equals(opName, "replace", StringComparison.Ordinal);
    }

    private static bool IsReplace(JsonElement op)
        => op.TryGetProperty("op", out var opEl)
           && opEl.ValueKind == JsonValueKind.String
           && string.Equals(opEl.GetString(), "replace", StringComparison.Ordinal);

    private static bool IsRemove(JsonElement op)
        => op.TryGetProperty("op", out var opEl)
           && opEl.ValueKind == JsonValueKind.String
           && string.Equals(opEl.GetString(), "remove", StringComparison.Ordinal);

    private static string? ClassifyBaselineRemoval(string pointer)
    {
        var segments = DecodeSegments(pointer);
        if (segments.Count == 0)
        {
            return null;
        }

        if (EndsWith(segments, "securityContext")) return ExplainRemoveBaseline;

        if (EndsWith(segments, "securityContext", "capabilities")) return ExplainRemoveBaseline;
        if (EndsWith(segments, "securityContext", "capabilities", "drop")) return ExplainRemoveBaseline;
        if (IsIndex(segments[^1]) && EndsWithBefore(segments, 1, "securityContext", "capabilities", "drop"))
        {
            return ExplainRemoveBaseline;
        }

        if (EndsWith(segments, "securityContext", "allowPrivilegeEscalation")) return ExplainRemoveBaseline;
        if (EndsWith(segments, "securityContext", "runAsNonRoot")) return ExplainRemoveBaseline;
        if (EndsWith(segments, "securityContext", "seccompProfile")) return ExplainRemoveBaseline;

        return null;
    }

    private static IReadOnlyList<string> ScanValueForDangers(JsonElement value)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ScanValueRecursive(value, found, seen);
        return found;
    }

    private static void ScanValueRecursive(JsonElement node, List<string> found, HashSet<string> seen)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                {
                    var explanation = ClassifyValueKey(prop.Name, prop.Value);
                    if (explanation is not null && seen.Add(explanation))
                    {
                        found.Add(explanation);
                    }

                    ScanValueRecursive(prop.Value, found, seen);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    ScanValueRecursive(item, found, seen);
                }
                break;
        }
    }

    private static string? ClassifyValueKey(string key, JsonElement value)
    {
        switch (key)
        {
            case "hostNetwork" when IsTrueish(value):
                return ExplainHostNetwork;
            case "hostPID" when IsTrueish(value):
                return ExplainHostPid;
            case "hostIPC" when IsTrueish(value):
                return ExplainHostIpc;
            case "privileged" when IsTrueish(value):
                return ExplainPrivileged;
            case "allowPrivilegeEscalation" when IsTrueish(value):
                return ExplainAllowPrivilegeEscalation;
            case "automountServiceAccountToken" when IsTrueish(value):
                return ExplainAutomountToken;
            case "shareProcessNamespace" when IsTrueish(value):
                return ExplainShareProcessNamespace;
            case "serviceAccountName" when value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()):
            case "serviceAccount" when value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()):
                return ExplainServiceAccount;
            case "runAsUser":
                return ExplainRunAsUserRoot;
            case "hostPath":
                return ExplainHostPath;
            case "hostPort" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port) && port != 0:
                return ExplainHostPort;
            case "capabilities" when HasNonEmptyCapabilitiesAdd(value):
                return ExplainCapabilitiesAdd;
            case "runAsNonRoot" when value.ValueKind == JsonValueKind.False:
                return ExplainRunAsRootAllowed;
            case "seccompProfile" when HasNonConfiningSeccompType(value):
                return ExplainSeccompUnconfined;
            case "serviceAccountToken":
                return ExplainServiceAccountToken;
            default:
                return null;
        }
    }

    private static bool IsTrueish(JsonElement value)
        => value.ValueKind != JsonValueKind.False;

    private static bool HasNonEmptyCapabilitiesAdd(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object
            || !capabilities.TryGetProperty("add", out var addEl)
            || addEl.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return addEl.GetArrayLength() > 0;
    }

    private const string SeccompRuntimeDefault = "RuntimeDefault";
    private const string SeccompLocalhost = "Localhost";

    private static bool IsConfiningSeccompType(string? type)
        => string.Equals(type, SeccompRuntimeDefault, StringComparison.Ordinal)
           || string.Equals(type, SeccompLocalhost, StringComparison.Ordinal);

    private static bool HasNonConfiningSeccompType(JsonElement seccompProfile)
    {
        if (seccompProfile.ValueKind != JsonValueKind.Object
            || !seccompProfile.TryGetProperty("type", out var typeEl)
            || typeEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return !IsConfiningSeccompType(typeEl.GetString());
    }

    private static bool ArrayContainsAll(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), "ALL", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIndex(string segment)
        => segment == "-" || (segment.Length > 0 && segment.All(char.IsAsciiDigit));

    private static bool IsPodLevel(IReadOnlyList<string> segments, string field)
    {
        if (segments.Count == 2
            && string.Equals(segments[0], "spec", StringComparison.Ordinal)
            && string.Equals(segments[1], field, StringComparison.Ordinal))
        {
            return true;
        }

        return segments.Count == 4
            && string.Equals(segments[0], "spec", StringComparison.Ordinal)
            && string.Equals(segments[1], "template", StringComparison.Ordinal)
            && string.Equals(segments[2], "spec", StringComparison.Ordinal)
            && string.Equals(segments[3], field, StringComparison.Ordinal);
    }

    private static bool EndsWith(IReadOnlyList<string> segments, params string[] tail)
    {
        if (segments.Count < tail.Length)
        {
            return false;
        }

        var offset = segments.Count - tail.Length;
        for (var i = 0; i < tail.Length; i++)
        {
            if (!string.Equals(segments[offset + i], tail[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EndsWithBefore(IReadOnlyList<string> segments, int skipTrailing, params string[] tail)
    {
        if (segments.Count < skipTrailing)
        {
            return false;
        }

        return EndsWith(segments.Take(segments.Count - skipTrailing).ToList(), tail);
    }

    private static List<string> DecodeSegments(string pointer)
    {
        var raw = pointer.Split('/');
        var segments = new List<string>(raw.Length);
        for (var i = 1; i < raw.Length; i++)
        {
            segments.Add(raw[i].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }

        return segments;
    }
}
