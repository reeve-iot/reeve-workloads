using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using FluentAssertions;
using k8s;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class UnsupportedPodPatchApplierRfc6902Tests
{
    private const string Container = "/spec/template/spec/containers/0";
    private const string Absent = "<absent>";

    private static readonly KubernetesRenderContext Ctx = new() { Namespace = "reeve" };
    private static readonly KubernetesWorkloadRenderer Renderer = new();
    private static readonly UnsupportedPodPatchApplier Applier = new();

    public static TheoryData<string, string, string, string> Applied() => new()
    {
        {
            "~1 and ~0 decode in a new member name",
            """
            [{"op":"add","path":"/spec/template/metadata/annotations","value":{}},
             {"op":"add","path":"/spec/template/metadata/annotations/k8s.v1.cni.cncf.io~1networks","value":"a"},
             {"op":"add","path":"/spec/template/metadata/annotations/til~0de","value":"b"}]
            """,
            "/spec/template/metadata/annotations",
            """{"k8s.v1.cni.cncf.io/networks":"a","til~de":"b"}"""
        },
        {
            "'-' appends, an index inserts, the length appends",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["b"]},
             {"op":"add","path":"{{Container}}/args/-","value":"c"},
             {"op":"add","path":"{{Container}}/args/0","value":"a"},
             {"op":"add","path":"{{Container}}/args/3","value":"d"}]
            """,
            $"{Container}/args",
            """["a","b","c","d"]"""
        },
        {
            "remove an array element",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["a","b","c"]},
             {"op":"remove","path":"{{Container}}/args/1"}]
            """,
            $"{Container}/args",
            """["a","c"]"""
        },
        {
            "replace an array element",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["a","b"]},
             {"op":"replace","path":"{{Container}}/args/0","value":"z"}]
            """,
            $"{Container}/args",
            """["z","b"]"""
        },
        { "add over an existing member replaces it", """[{"op":"add","path":"/spec/replicas","value":3}]""", "/spec/replicas", "3" },
        { "replace a member", """[{"op":"replace","path":"/spec/replicas","value":2}]""", "/spec/replicas", "2" },
        { "an explicit null value", """[{"op":"add","path":"/spec/template/spec/hostname","value":null}]""", "/spec/template/spec/hostname", Absent },
        {
            "move a member: the target",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["x"]},
             {"op":"move","from":"{{Container}}/args","path":"{{Container}}/command"}]
            """,
            $"{Container}/command",
            """["x"]"""
        },
        {
            "move a member: the source is gone",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["x"]},
             {"op":"move","from":"{{Container}}/args","path":"{{Container}}/command"}]
            """,
            $"{Container}/args",
            Absent
        },
        {
            "move an element to the end",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["x","y"]},
             {"op":"move","from":"{{Container}}/args/0","path":"{{Container}}/args/-"}]
            """,
            $"{Container}/args",
            """["y","x"]"""
        },
        {
            "copy a member: the source stays",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["x"]},
             {"op":"copy","from":"{{Container}}/args","path":"{{Container}}/command"}]
            """,
            $"{Container}/args",
            """["x"]"""
        },
        {
            "copy a member: the target",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["x"]},
             {"op":"copy","from":"{{Container}}/args","path":"{{Container}}/command"}]
            """,
            $"{Container}/command",
            """["x"]"""
        },
        {
            "copy a string",
            $$"""[{"op":"copy","from":"{{Container}}/image","path":"/spec/template/metadata/annotations/image"}]""",
            "/spec/template/metadata/annotations/image",
            "\"ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111\""
        },
        { "copy a number", """[{"op":"copy","from":"/spec/replicas","path":"/spec/revisionHistoryLimit"}]""", "/spec/revisionHistoryLimit", "1" },
        {
            "copy an element into the array",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["a","b"]},
             {"op":"copy","from":"{{Container}}/args/1","path":"{{Container}}/args/0"}]
            """,
            $"{Container}/args",
            """["b","a","b"]"""
        },
        {
            "copy an element to the end",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["a","b"]},
             {"op":"copy","from":"{{Container}}/args/0","path":"{{Container}}/args/-"}]
            """,
            $"{Container}/args",
            """["a","b","a"]"""
        },
        {
            "move an element out of the array",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["a","b","c"]},
             {"op":"move","from":"{{Container}}/args/1","path":"/spec/template/metadata/annotations/moved"},
             {"op":"test","path":"/spec/template/metadata/annotations/moved","value":"b"}]
            """,
            $"{Container}/args",
            """["a","c"]"""
        },
        {
            "test then add",
            $$"""
            [{"op":"test","path":"/spec/replicas","value":1},
             {"op":"add","path":"{{Container}}/args","value":["after-test"]}]
            """,
            $"{Container}/args",
            """["after-test"]"""
        },
        {
            "test compares numbers by value",
            $$"""
            [{"op":"test","path":"/spec/replicas","value":1.0},
             {"op":"add","path":"{{Container}}/args","value":["after-test"]}]
            """,
            $"{Container}/args",
            """["after-test"]"""
        },
        {
            "test compares objects whatever their member order",
            """
            [{"op":"add","path":"/spec/template/spec/nodeSelector","value":{"a":"1","b":"2"}},
             {"op":"test","path":"/spec/template/spec/nodeSelector","value":{"b":"2","a":"1"}},
             {"op":"replace","path":"/spec/replicas","value":5}]
            """,
            "/spec/replicas",
            "5"
        },
        {
            "'-' names the last element for remove",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["a","b","c"]},
             {"op":"remove","path":"{{Container}}/args/-"}]
            """,
            $"{Container}/args",
            """["a","b"]"""
        },
        {
            "'-' names the last element for replace",
            $$"""
            [{"op":"add","path":"{{Container}}/args","value":["a","b","c"]},
             {"op":"replace","path":"{{Container}}/args/-","value":"z"}]
            """,
            $"{Container}/args",
            """["a","b","z"]"""
        },
        {
            "~1 and ~0 decode for replace, test and remove",
            """
            [{"op":"add","path":"/spec/template/metadata/annotations","value":{"a/b":"x","c~d":"y","keep":"k"}},
             {"op":"replace","path":"/spec/template/metadata/annotations/a~1b","value":"x2"},
             {"op":"test","path":"/spec/template/metadata/annotations/a~1b","value":"x2"},
             {"op":"remove","path":"/spec/template/metadata/annotations/c~0d"}]
            """,
            "/spec/template/metadata/annotations",
            """{"a/b":"x2","keep":"k"}"""
        },
        {
            "replace and test the whole document",
            """
            [{"op":"replace","path":"","value":{"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"swapped"},"spec":{"replicas":9}}},
             {"op":"test","path":"","value":{"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"swapped"},"spec":{"replicas":9}}}]
            """,
            "/metadata/name",
            "\"swapped\""
        },
        {
            "add the whole document",
            """[{"op":"add","path":"","value":{"apiVersion":"apps/v1","kind":"Deployment","metadata":{"name":"added"}}}]""",
            "/metadata/name",
            "\"added\""
        },
        { "unknown members of an operation are ignored", """[{"op":"replace","path":"/spec/replicas","value":4,"comment":"why"}]""", "/spec/replicas", "4" },
    };

    [Theory]
    [MemberData(nameof(Applied))]
    public void An_applied_patch_leaves_exactly_this_at_the_pointer(string why, string patch, string pointer, string expected)
    {
        var patched = Applier.Apply(Render(), patch);

        var actual = At(JsonNode.Parse(KubernetesJson.Serialize(patched.Deployment)), pointer);

        if (expected == Absent)
        {
            actual.Should().Be(Absent, why);
        }
        else
        {
            JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected)).Should()
                .BeTrue("{0}: the patched {1} must be {2}, was {3}", why, pointer, expected, actual);
        }
    }

    [Fact]
    public void An_empty_patch_leaves_the_object_as_rendered()
    {
        var rendered = Render();

        var patched = Applier.Apply(rendered, "[]");

        KubernetesJson.Serialize(patched.Deployment).Should().Be(KubernetesJson.Serialize(rendered.Deployment));
    }

    [Fact]
    public void A_patch_does_not_touch_the_rendered_input()
    {
        var rendered = Render();
        var before = KubernetesJson.Serialize(rendered.Deployment);

        Applier.Apply(rendered, """[{"op":"replace","path":"/spec/replicas","value":7}]""");

        KubernetesJson.Serialize(rendered.Deployment).Should().Be(before);
    }

    public static TheoryData<string, string> Refused() => new()
    {
        { "replace a missing member", """[{"op":"replace","path":"/spec/template/spec/nope","value":1}]""" },
        { "remove a missing member", """[{"op":"remove","path":"/spec/template/spec/nope"}]""" },
        { "add under a missing parent", """[{"op":"add","path":"/spec/template/spec/nope/deeper","value":1}]""" },
        { "move from a missing member", $$"""[{"op":"move","from":"{{Container}}/nope","path":"{{Container}}/args"}]""" },
        { "copy from a missing member", $$"""[{"op":"copy","from":"{{Container}}/nope","path":"{{Container}}/args"}]""" },
        { "test a missing member", """[{"op":"test","path":"/spec/template/spec/nope","value":1}]""" },
        { "add past the end of an array", $$"""[{"op":"add","path":"{{Container}}/args","value":["a"]},{"op":"add","path":"{{Container}}/args/2","value":"x"}]""" },
        { "remove past the end of an array", $$"""[{"op":"add","path":"{{Container}}/args","value":["a"]},{"op":"remove","path":"{{Container}}/args/1"}]""" },
        { "replace past the end of an array", $$"""[{"op":"add","path":"{{Container}}/args","value":["a"]},{"op":"replace","path":"{{Container}}/args/1","value":"x"}]""" },
        { "add at a non-numeric index", $$"""[{"op":"add","path":"{{Container}}/args","value":["a"]},{"op":"add","path":"{{Container}}/args/x","value":"b"}]""" },
        { "remove at a leading-zero index", $$"""[{"op":"add","path":"{{Container}}/args","value":["a","b"]},{"op":"remove","path":"{{Container}}/args/01"}]""" },
        { "add at a signed index", $$"""[{"op":"add","path":"{{Container}}/args","value":["a","b"]},{"op":"remove","path":"{{Container}}/args/+1"}]""" },
        { "remove at an index with a space", $$"""[{"op":"add","path":"{{Container}}/args","value":["a","b"]},{"op":"remove","path":"{{Container}}/args/ 1"}]""" },
        { "test at '-'", $$"""[{"op":"add","path":"{{Container}}/args","value":["a"]},{"op":"test","path":"{{Container}}/args/-","value":"a"}]""" },
        { "copy from '-'", $$"""[{"op":"add","path":"{{Container}}/args","value":["a"]},{"op":"copy","from":"{{Container}}/args/-","path":"{{Container}}/command"}]""" },
        { "move from '-'", $$"""[{"op":"add","path":"{{Container}}/args","value":["a"]},{"op":"move","from":"{{Container}}/args/-","path":"{{Container}}/command"}]""" },
        { "'-' before the last token", $$"""[{"op":"add","path":"{{Container}}/args","value":[["a"]]},{"op":"add","path":"{{Container}}/args/-/0","value":"b"}]""" },
        { "empty last member name", """[{"op":"add","path":"/spec/template/spec/","value":{}}]""" },
        { "empty inner member name", """[{"op":"replace","path":"/spec//replicas","value":2}]""" },
        { "remove an empty member name", """[{"op":"remove","path":"/spec/template/"}]""" },
        { "remove the document", """[{"op":"remove","path":""}]""" },
        { "move over the document", """[{"op":"move","from":"/spec","path":""}]""" },
        { "copy over the document", """[{"op":"copy","from":"/spec","path":""}]""" },
        { "test the document against something else", """[{"op":"test","path":"","value":{}}]""" },
        { "replace the document with null", """[{"op":"replace","path":"","value":null}]""" },
        { "test a different number", """[{"op":"test","path":"/spec/replicas","value":2}]""" },
        { "test a number against a string", """[{"op":"test","path":"/spec/replicas","value":"1"}]""" },
        { "a failing op after good ones", $$"""[{"op":"add","path":"{{Container}}/args","value":["a"]},{"op":"remove","path":"/spec/template/spec/nope"}]""" },
        { "move into its own child", $$"""[{"op":"add","path":"{{Container}}/args","value":["a"]},{"op":"move","from":"/spec/template/spec","path":"/spec/template/spec/containers/0/x"}]""" },
        { "unknown op", """[{"op":"bogus","path":"/spec/replicas","value":1}]""" },
        { "missing op", """[{"path":"/spec/replicas","value":1}]""" },
        { "missing path", """[{"op":"add","value":1}]""" },
        { "add without value", """[{"op":"add","path":"/spec/replicas"}]""" },
        { "replace without value", """[{"op":"replace","path":"/spec/replicas"}]""" },
        { "test without value", """[{"op":"test","path":"/spec/replicas"}]""" },
        { "move without from", """[{"op":"move","path":"/spec/replicas"}]""" },
        { "copy without from", """[{"op":"copy","path":"/spec/replicas"}]""" },
        { "path without a leading slash", """[{"op":"add","path":"spec/replicas","value":1}]""" },
        { "path not a string", """[{"op":"add","path":1,"value":1}]""" },
        { "op not a string", """[{"op":1,"path":"/spec/replicas","value":1}]""" },
        { "op in upper case", """[{"op":"ADD","path":"/spec/replicas","value":1}]""" },
        { "element not an object", """[1]""" },
        { "document not an array", """{"op":"add","path":"/spec/replicas","value":1}""" },
        { "not JSON", "not-json" },
        { "empty", "" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_patch_that_cannot_apply_is_refused_as_invalid_operation(string why, string patch)
    {
        var act = () => Applier.Apply(Render(), patch);

        act.Should().Throw<InvalidOperationException>(why).WithMessage("unsupportedPodPatch*");
    }

    [Fact]
    public void A_null_patch_document_is_refused()
    {
        var act = () => Applier.Apply(Render(), "null");

        act.Should().Throw<InvalidOperationException>().WithMessage("unsupportedPodPatch*");
    }

    private static RenderedKubernetesObjects Render()
    {
        var instanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var spec = new WorkloadDesiredSpec
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = instanceId,
            Inst8 = "11111111",
            Provider = KubernetesProvider.Id,
            ResourceName = "reeve-wl-com-reeve-matter-matter-11111111",
            SecretName = "reeve-wl-com-reeve-matter-matter-11111111",
            Labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
            {
                InstanceId = instanceId,
                InstallationId = "install-1",
                SpecHash = "abc123def456",
            }),
            Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Platforms = new List<string>(),
            Kind = WorkloadKind.SingleNode,
            NodeSelector = new Dictionary<string, string>(),
            Volumes = new List<ResolvedVolume>(),
            ConfigMapMounts = new List<ResolvedConfigMapMount>(),
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Env = new List<ResolvedEnvVar>(),
            SpecHash = "abc123def456",
        };
        return Renderer.Render(spec, Ctx);
    }

    private static string At(JsonNode? root, string pointer)
    {
        var node = root;
        foreach (var raw in pointer.Split('/')[1..])
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsonObject obj when obj.TryGetPropertyValue(token, out var child) => child,
                JsonArray arr when int.TryParse(token, out var i) && i < arr.Count => arr[i],
                _ => null,
            };
            if (node is null) return Absent;
        }

        return node?.ToJsonString() ?? Absent;
    }
}
