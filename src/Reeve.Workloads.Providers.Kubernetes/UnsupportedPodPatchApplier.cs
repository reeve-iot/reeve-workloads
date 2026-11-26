using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Models;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

namespace Reeve.Workloads.Providers.Kubernetes;

internal sealed class UnsupportedPodPatchApplier
{
    public RenderedKubernetesObjects Apply(RenderedKubernetesObjects rendered, string patchJson)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentNullException.ThrowIfNull(patchJson);

        var operations = Parse(patchJson);

        if (rendered.Deployment is { } deployment)
        {
            var patched = ApplyTo(deployment, operations);
            return rendered with { Deployment = patched };
        }

        if (rendered.DaemonSet is { } daemonSet)
        {
            var patched = ApplyTo(daemonSet, operations);
            return rendered with { DaemonSet = patched };
        }

        throw new ArgumentException(
            "RenderedKubernetesObjects has neither a Deployment nor a DaemonSet to patch.", nameof(rendered));
    }

    private sealed record PatchOperation(string Op, string Path, string? From, JsonNode? Value, JsonPatchDocument Document);

    private static JsonPatchDocument SingleOperation(JsonObject operation)
        => JsonSerializer.Deserialize<JsonPatchDocument>(new JsonArray(operation).ToJsonString())
            ?? throw new InvalidOperationException("unsupportedPodPatch deserialized to null — expected an RFC 6902 array.");

    private static IReadOnlyList<PatchOperation> Parse(string patchJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(patchJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"unsupportedPodPatch is not a valid RFC 6902 JSON Patch: {ex.Message}", ex);
        }

        if (root is null)
        {
            throw new InvalidOperationException("unsupportedPodPatch deserialized to null — expected an RFC 6902 array.");
        }

        if (root is not JsonArray array)
        {
            throw Invalid("expected an array of operations");
        }

        var operations = new List<PatchOperation>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            operations.Add(ParseOperation(array[i], i));
        }

        return operations;
    }

    private static PatchOperation ParseOperation(JsonNode? node, int index)
    {
        if (node is not JsonObject operation)
        {
            throw Invalid($"operation {index} is not an object");
        }

        var op = RequiredString(operation, "op", index);
        if (op is not ("add" or "remove" or "replace" or "move" or "copy" or "test"))
        {
            throw Invalid($"operation {index} has an unknown op '{op}'");
        }

        var path = RequiredPointer(operation, "path", index);

        JsonNode? value = null;
        if (op is "add" or "replace" or "test")
        {
            if (!operation.TryGetPropertyValue("value", out value))
            {
                throw Invalid($"operation {index} ('{op}') has no 'value'");
            }
        }

        var from = op is "move" or "copy" ? RequiredPointer(operation, "from", index) : null;

        JsonPatchDocument document;
        try
        {
            document = SingleOperation((JsonObject)operation.DeepClone());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"unsupportedPodPatch is not a valid RFC 6902 JSON Patch: {ex.Message}", ex);
        }

        return new PatchOperation(op, path, from, value?.DeepClone(), document);
    }

    private static string RequiredString(JsonObject operation, string member, int index)
    {
        if (!operation.TryGetPropertyValue(member, out var value) || value is null)
        {
            throw Invalid($"operation {index} has no '{member}'");
        }

        if (value.GetValueKind() != JsonValueKind.String)
        {
            throw Invalid($"operation {index} has a '{member}' that is not a string");
        }

        return value.GetValue<string>();
    }

    private static string RequiredPointer(JsonObject operation, string member, int index)
    {
        var pointer = RequiredString(operation, member, index);
        var valid = pointer.Length == 0 || pointer[0] == '/';
        for (var i = 0; valid && i < pointer.Length; i++)
        {
            if (pointer[i] == '~')
            {
                valid = i + 1 < pointer.Length && pointer[i + 1] is '0' or '1';
            }
        }

        if (!valid)
        {
            throw Invalid($"operation {index} has a '{member}' that is not a JSON Pointer: '{pointer}'");
        }

        return pointer;
    }

    private static InvalidOperationException Invalid(string reason)
        => new($"unsupportedPodPatch is not a valid RFC 6902 JSON Patch: {reason}.");

    private static InvalidOperationException NotApplied(string reason)
        => new($"unsupportedPodPatch could not be applied: {reason}");

    private static T ApplyTo<T>(T source, IReadOnlyList<PatchOperation> operations) where T : class
    {
        var json = KubernetesJson.Serialize(source);
        var target = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("The pod object serialized to null.");

        foreach (var operation in operations)
        {
            target = ApplyOne(target, operation);
        }

        return KubernetesJson.Deserialize<T>(target.ToJsonString())
            ?? throw new InvalidOperationException("The patched pod object deserialized to null.");
    }

    private static JsonNode ApplyOne(JsonNode target, PatchOperation operation)
    {
        CheckPointer(target, operation.Path, operation.Op, isFrom: false);
        if (operation.From is { } from)
        {
            CheckPointer(target, from, operation.Op, isFrom: true);
            if (operation.Op == "move" && operation.Path.StartsWith(from + "/", StringComparison.Ordinal))
            {
                throw NotApplied($"'{from}' cannot be moved into its own child '{operation.Path}'.");
            }
        }

        if (operation.Path.Length == 0)
        {
            return ApplyToWholeDocument(target, operation);
        }

        var document = operation.Document;
        if (operation.Op == "copy")
        {
            if (!TryResolve(target, operation.From!, out var value))
            {
                throw NotApplied($"the 'from' location '{operation.From}' does not exist.");
            }

            document = SingleOperation(new JsonObject
            {
                ["op"] = "add",
                ["path"] = operation.Path,
                ["value"] = value?.DeepClone(),
            });
        }

        try
        {
            document.ApplyTo(target);
        }
        catch (Exception ex)
        {
            throw NotApplied(ex.Message);
        }

        return target;
    }

    private static JsonNode ApplyToWholeDocument(JsonNode target, PatchOperation operation)
    {
        switch (operation.Op)
        {
            case "add" or "replace":
                return operation.Value?.DeepClone() ?? throw NotApplied("the pod object cannot be replaced with null.");
            case "test":
                return JsonNode.DeepEquals(target, operation.Value)
                    ? target
                    : throw NotApplied("the pod object is not equal to the test value.");
            default:
                throw NotApplied($"'{operation.Op}' cannot target the whole pod object.");
        }
    }

    private static void CheckPointer(JsonNode target, string pointer, string op, bool isFrom)
    {
        if (pointer.Length == 0)
        {
            return;
        }

        var tokens = pointer[1..].Split('/');
        JsonNode? node = target;
        for (var i = 0; i < tokens.Length && node is not null; i++)
        {
            var token = tokens[i].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            var last = i == tokens.Length - 1;
            switch (node)
            {
                case JsonObject obj:
                    if (token.Length == 0)
                    {
                        throw NotApplied($"'{pointer}' has an empty member name, which is not supported.");
                    }

                    node = obj.TryGetPropertyValue(token, out var child) ? child : null;
                    break;
                case JsonArray arr:
                    if (token == "-")
                    {
                        if (!last || isFrom || op == "test")
                        {
                            throw NotApplied($"'{pointer}' uses '-', which names no element to {op}.");
                        }

                        node = null;
                        break;
                    }

                    if (!IsCanonicalIndex(token))
                    {
                        throw NotApplied($"'{pointer}' has '{token}' where an array index belongs.");
                    }

                    node = int.TryParse(token, out var index) && index < arr.Count ? arr[index] : null;
                    break;
                default:
                    node = null;
                    break;
            }
        }
    }

    private static bool TryResolve(JsonNode target, string pointer, out JsonNode? value)
    {
        value = target;
        if (pointer.Length == 0)
        {
            return true;
        }

        foreach (var raw in pointer[1..].Split('/'))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            switch (value)
            {
                case JsonObject obj when obj.TryGetPropertyValue(token, out var child):
                    value = child;
                    break;
                case JsonArray arr when int.TryParse(token, out var index) && index < arr.Count:
                    value = arr[index];
                    break;
                default:
                    value = null;
                    return false;
            }
        }

        return true;
    }

    private static bool IsCanonicalIndex(string token)
    {
        if (token.Length == 0 || (token.Length > 1 && token[0] == '0'))
        {
            return false;
        }

        foreach (var c in token)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
