using FluentAssertions;
using Reeve.Workloads.Core.Preview;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadPatchDangerousPointersTests
{
    private static System.Collections.Generic.IReadOnlyList<string> ExplanationsAt(string patch, string pointer)
        => WorkloadPatchDangerousPointers.Annotate(patch)
            .Where(a => a.JsonPointer == pointer)
            .Select(a => a.Explanation)
            .ToList();

    [Fact]
    public void ValueScan_flags_deprecated_serviceAccount_alias()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec", "value": { "serviceAccount": "admin" } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec")
            .Should().Contain(e => e.Contains("cluster identity"),
                "the deprecated serviceAccount alias runs as a cluster identity, same as serviceAccountName");
    }

    [Fact]
    public void ValueScan_ignores_empty_serviceAccount_alias()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec", "value": { "serviceAccount": "" } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec")
            .Should().BeEmpty("an empty serviceAccount alias grants no cluster identity");
    }

    [Fact]
    public void ValueScan_flags_automountServiceAccountToken_true()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec", "value": { "automountServiceAccountToken": true } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec")
            .Should().Contain(e => e.Contains("service-account token") || e.Contains("cluster identity"),
                "mounting the service-account token is un-consented");
    }

    [Fact]
    public void ValueScan_ignores_automountServiceAccountToken_false()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec", "value": { "automountServiceAccountToken": false } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec")
            .Should().BeEmpty("an explicit false is benign (respect boolean quieting)");
    }

    [Fact]
    public void ValueScan_flags_shareProcessNamespace_true()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec", "value": { "shareProcessNamespace": true } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec")
            .Should().Contain(e => e.Contains("process namespace"),
                "a co-container could read the app container's token env");
    }

    [Fact]
    public void ValueScan_ignores_shareProcessNamespace_false()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec", "value": { "shareProcessNamespace": false } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec")
            .Should().BeEmpty("an explicit false is benign (respect boolean quieting)");
    }

    [Fact]
    public void ValueScan_flags_container_hostPort()
    {
        const string patch = """
            [
              { "op": "add", "path": "/spec/template/spec/containers/-",
                "value": { "name": "sneaky", "ports": [ { "containerPort": 8080, "hostPort": 9000 } ] } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/-")
            .Should().Contain(e => e.Contains("host port"),
                "a hostPort binding is an un-consented host surface");
    }

    [Fact]
    public void ValueScan_ignores_container_hostPort_zero()
    {
        const string patch = """
            [
              { "op": "add", "path": "/spec/template/spec/containers/-",
                "value": { "name": "quiet", "ports": [ { "containerPort": 8080, "hostPort": 0 } ] } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/-")
            .Should().BeEmpty("hostPort=0 is not a real host-port binding (respect zero quieting)");
    }

    [Fact]
    public void Remove_of_capabilities_drop_is_flagged()
    {
        const string patch = """
            [
              { "op": "remove", "path": "/spec/template/spec/containers/0/securityContext/capabilities/drop" }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext/capabilities/drop")
            .Should().NotBeEmpty("removing the drop-ALL baseline reverts to the default capability set");
    }

    [Fact]
    public void Remove_of_container_securityContext_is_flagged()
    {
        const string patch = """
            [
              { "op": "remove", "path": "/spec/template/spec/containers/0/securityContext" }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext")
            .Should().NotBeEmpty("removing the whole container securityContext reverts to the insecure default");
    }

    [Fact]
    public void Remove_of_benign_path_is_not_flagged()
    {
        const string patch = """
            [
              { "op": "remove", "path": "/spec/template/spec/containers/0/resources/limits/cpu" }
            ]
            """;

        WorkloadPatchDangerousPointers.Annotate(patch)
            .Should().BeEmpty("a remove of a non-baseline path is benign");
    }

    [Fact]
    public void PathClassifier_flags_direct_shareProcessNamespace_true()
    {
        const string patch = """
            [
              { "op": "add", "path": "/spec/template/spec/shareProcessNamespace", "value": true }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/shareProcessNamespace")
            .Should().Contain(e => e.Contains("process namespace"),
                "a direct-pointer op targeting shareProcessNamespace must be highlighted, not only a whole-spec inject");
    }

    [Fact]
    public void PathClassifier_flags_direct_shareProcessNamespace_true_on_bare_pod()
    {
        const string patch = """
            [
              { "op": "add", "path": "/spec/shareProcessNamespace", "value": true }
            ]
            """;

        ExplanationsAt(patch, "/spec/shareProcessNamespace")
            .Should().Contain(e => e.Contains("process namespace"));
    }

    [Fact]
    public void PathClassifier_ignores_direct_shareProcessNamespace_false()
    {
        const string patch = """
            [
              { "op": "add", "path": "/spec/template/spec/shareProcessNamespace", "value": false }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/shareProcessNamespace")
            .Should().BeEmpty("an explicit false is benign (respect boolean quieting)");
    }

    [Fact]
    public void PathClassifier_flags_direct_automountServiceAccountToken_true()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/automountServiceAccountToken", "value": true }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/automountServiceAccountToken")
            .Should().Contain(e => e.Contains("service-account token") || e.Contains("cluster identity"),
                "a direct-pointer op targeting automountServiceAccountToken must be highlighted");
    }

    [Fact]
    public void PathClassifier_ignores_direct_automountServiceAccountToken_false()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/automountServiceAccountToken", "value": false }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/automountServiceAccountToken")
            .Should().BeEmpty("an explicit false is benign (respect boolean quieting)");
    }

    [Fact]
    public void PathClassifier_flags_direct_serviceAccount_alias()
    {
        const string patch = """
            [
              { "op": "add", "path": "/spec/template/spec/serviceAccount", "value": "admin" }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/serviceAccount")
            .Should().Contain(e => e.Contains("cluster identity"),
                "the deprecated serviceAccount alias runs as a cluster identity, same as serviceAccountName");
    }

    [Fact]
    public void PathClassifier_ignores_direct_serviceAccount_alias_empty()
    {
        const string patch = """
            [
              { "op": "add", "path": "/spec/template/spec/serviceAccount", "value": "" }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/serviceAccount")
            .Should().BeEmpty("an empty serviceAccount alias grants no cluster identity");
    }

    [Fact]
    public void PathClassifier_flags_direct_container_hostPort_nonzero()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/ports/0/hostPort", "value": 9000 }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/ports/0/hostPort")
            .Should().Contain(e => e.Contains("host port"),
                "a direct-pointer op setting a non-zero hostPort must be highlighted");
    }

    [Fact]
    public void PathClassifier_ignores_direct_container_hostPort_zero()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/ports/0/hostPort", "value": 0 }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/ports/0/hostPort")
            .Should().BeEmpty("hostPort=0 is not a real host-port binding");
    }

    [Fact]
    public void ValueScan_flags_runAsNonRoot_false_set()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/securityContext",
                "value": { "runAsNonRoot": false } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext")
            .Should().Contain(e => e.Contains("root"),
                "setting runAsNonRoot:false stops enforcing non-root — as dangerous as removing it");
    }

    [Fact]
    public void PathClassifier_flags_direct_runAsNonRoot_false_set()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/securityContext/runAsNonRoot",
                "value": false }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext/runAsNonRoot")
            .Should().Contain(e => e.Contains("root"),
                "setting runAsNonRoot:false directly stops enforcing non-root");
    }

    [Fact]
    public void PathClassifier_ignores_direct_runAsNonRoot_true_set()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/securityContext/runAsNonRoot",
                "value": true }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext/runAsNonRoot")
            .Should().BeEmpty("runAsNonRoot:true is the hardened baseline value, not a danger");
    }

    [Fact]
    public void ValueScan_flags_seccompProfile_unconfined_set()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/securityContext",
                "value": { "seccompProfile": { "type": "Unconfined" } } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext")
            .Should().Contain(e => e.Contains("syscall") || e.Contains("Unconfined") || e.Contains("seccomp"),
                "setting seccompProfile.type Unconfined restores the full syscall surface");
    }

    [Fact]
    public void ValueScan_ignores_seccompProfile_runtimedefault_set()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/securityContext",
                "value": { "seccompProfile": { "type": "RuntimeDefault" } } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext")
            .Should().BeEmpty("RuntimeDefault is the confining baseline type, not a danger");
    }

    [Fact]
    public void ValueScan_ignores_seccompProfile_localhost_set()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/securityContext",
                "value": { "seccompProfile": { "type": "Localhost", "localhostProfile": "p.json" } } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext")
            .Should().BeEmpty("Localhost is a confining baseline type, not a danger");
    }

    [Fact]
    public void ValueScan_flags_capabilities_drop_weakened_to_empty()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/securityContext/capabilities/drop",
                "value": [] }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext/capabilities/drop")
            .Should().NotBeEmpty("emptying capabilities.drop loses the drop-ALL baseline (restores the default caps)");
    }

    [Fact]
    public void ValueScan_flags_capabilities_drop_weakened_dropping_first_element()
    {
        const string patch = """
            [
              { "op": "remove", "path": "/spec/template/spec/containers/0/securityContext/capabilities/drop/0" }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext/capabilities/drop/0")
            .Should().NotBeEmpty("removing an element of capabilities.drop can drop the ALL baseline");
    }

    [Fact]
    public void ValueScan_flags_capabilities_drop_weakened_to_non_all()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/securityContext/capabilities/drop",
                "value": [ "NET_RAW" ] }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext/capabilities/drop")
            .Should().NotBeEmpty("a drop set that no longer drops ALL loses the baseline");
    }

    [Fact]
    public void ValueScan_flags_capabilities_drop_index_replaced_with_non_all_scalar()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/securityContext/capabilities/drop/0",
                "value": "NET_RAW" }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext/capabilities/drop/0")
            .Should().NotBeEmpty("replacing the ALL drop element with a narrower capability loses the drop-ALL baseline");
    }

    [Fact]
    public void ValueScan_ignores_capabilities_drop_index_set_to_all()
    {
        const string patch = """
            [
              { "op": "replace", "path": "/spec/template/spec/containers/0/securityContext/capabilities/drop/0",
                "value": "ALL" }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext/capabilities/drop/0")
            .Should().BeEmpty("a drop element set to ALL still drops ALL — not a danger");
    }

    [Fact]
    public void ValueScan_ignores_capabilities_drop_index_added_with_non_all_scalar()
    {
        const string patch = """
            [
              { "op": "add", "path": "/spec/template/spec/containers/0/securityContext/capabilities/drop/-",
                "value": "NET_RAW" }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/containers/0/securityContext/capabilities/drop/-")
            .Should().BeEmpty("an add inserts an element and preserves the ALL baseline — not a danger");
    }

    [Fact]
    public void ValueScan_flags_projected_serviceAccountToken_volume()
    {
        const string patch = """
            [
              { "op": "add", "path": "/spec/template/spec/volumes/-",
                "value": { "name": "kube-api", "projected": { "sources": [ { "serviceAccountToken": { "path": "token" } } ] } } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/volumes/-")
            .Should().Contain(e => e.Contains("service-account token") || e.Contains("cluster identity"),
                "a projected serviceAccountToken source injects a cluster identity (bypasses the automount gate)");
    }

    [Fact]
    public void ValueScan_ignores_benign_projected_configmap_volume()
    {
        const string patch = """
            [
              { "op": "add", "path": "/spec/template/spec/volumes/-",
                "value": { "name": "cfg", "projected": { "sources": [ { "configMap": { "name": "app-config" } } ] } } }
            ]
            """;

        ExplanationsAt(patch, "/spec/template/spec/volumes/-")
            .Should().BeEmpty("a benign projected configMap source is not a token surface");
    }
}
