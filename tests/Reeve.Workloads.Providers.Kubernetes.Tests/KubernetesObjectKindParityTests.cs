using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesObjectKindParityTests
{
    private static async Task<bool> SsaAcceptsAsync(IKubernetesObjectClient client, KubernetesObjectKind kind)
    {
        var outcome = await client.ServerSideApplyAsync(
            Substitute.For<IKubernetes>(),
            @namespace: "ns",
            kind: kind,
            name: "obj",
            manifestJson: "{}",
            fieldManager: "reeve-workloads",
            force: false,
            ct: CancellationToken.None);

        return !string.Equals(outcome.Error, $"unknown object kind {kind}", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_object_client_SSA_accepts_exactly_the_same_kinds_as_the_real_client()
    {
        var real = new KubernetesObjectClient(NullLogger<KubernetesObjectClient>.Instance);
        var fake = new KubernetesWorkloadProviderTests.FakeObjectClient();

        var realAccepts = new List<KubernetesObjectKind>();
        var fakeAccepts = new List<KubernetesObjectKind>();
        foreach (var kind in Enum.GetValues<KubernetesObjectKind>())
        {
            if (await SsaAcceptsAsync(real, kind))
            {
                realAccepts.Add(kind);
            }

            if (await SsaAcceptsAsync(fake, kind))
            {
                fakeAccepts.Add(kind);
            }
        }

        realAccepts.Should().Contain(
            KubernetesObjectKind.Job,
            "regression anchor: the real client's ServerSideApplyAsync must SSA-apply Job");
        realAccepts.Should().NotContain(
            KubernetesObjectKind.PersistentVolumeClaim,
            "PersistentVolumeClaim is applied via EnsurePvcAsync, never SSA — the real client's default arm rejects it");

        var fakeMorePermissive = fakeAccepts.Except(realAccepts).ToList();
        var fakeLessPermissive = realAccepts.Except(fakeAccepts).ToList();

        fakeMorePermissive.Should().BeEmpty(
            "the FakeObjectClient must NOT SSA-accept any kind the real KubernetesObjectClient rejects — a fake more " +
            "permissive than the real client masks a MISSING real `case` and ships the gap green (exactly how a missing " +
            "Job arm once shipped). If this fails for a NEW kind, the real ServerSideApplyAsync switch is missing its arm.");
        fakeLessPermissive.Should().BeEmpty(
            "the FakeObjectClient must NOT reject a kind the real KubernetesObjectClient SSA-accepts — an under-" +
            "permissive fake would let a genuinely-broken provider path pass. Teach the fake the same kind.");
    }
}
