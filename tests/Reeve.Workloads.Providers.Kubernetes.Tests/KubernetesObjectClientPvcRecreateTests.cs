using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubernetesObjectClientPvcRecreateTests
{
    private const string Namespace = "reeve";
    private const string ClaimName = "reeve-wl-matter-data-11111111";
    private const string InstanceLabel = "reeve-iot.dev/workload-instance";
    private const string InstanceId = "11111111-1111-1111-1111-111111111111";

    private static KubernetesObjectClient NewClient() => new(NullLogger<KubernetesObjectClient>.Instance);

    private static HttpOperationResponse<T> Ok<T>(T body) =>
        new() { Body = body, Response = new HttpResponseMessage(HttpStatusCode.OK) };

    private static HttpOperationException Http(HttpStatusCode code) =>
        new("http error") { Response = new HttpResponseMessageWrapper(new HttpResponseMessage(code), "{}") };

    private static V1PersistentVolumeClaim Desired(string? storageClass) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = ClaimName,
            Labels = new Dictionary<string, string> { [InstanceLabel] = InstanceId },
        },
        Spec = new V1PersistentVolumeClaimSpec
        {
            StorageClassName = storageClass,
            Resources = new V1VolumeResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new("1Gi") },
            },
        },
    };

    private static V1PersistentVolumeClaim Live(string? storageClass, string phase, string? volumeName = null) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = ClaimName,
            Uid = "uid-1",
            ResourceVersion = "42",
            Labels = new Dictionary<string, string> { [InstanceLabel] = InstanceId },
        },
        Spec = new V1PersistentVolumeClaimSpec
        {
            StorageClassName = storageClass,
            VolumeName = volumeName,
            Resources = new V1VolumeResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new("1Gi") },
            },
        },
        Status = new V1PersistentVolumeClaimStatus { Phase = phase },
    };

    private static V1Pod PodMounting(string name, string claim) => new()
    {
        Metadata = new V1ObjectMeta { Name = name },
        Spec = new V1PodSpec
        {
            Containers = new List<V1Container>(),
            Volumes = new List<V1Volume>
            {
                new() { Name = "data", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = claim } },
            },
        },
    };

    private sealed class Harness
    {
        public ICoreV1Operations Core { get; } = Substitute.For<ICoreV1Operations>();

        public IKubernetes Client { get; }

        public List<V1DeleteOptions?> ClaimDeletes { get; } = [];

        public List<string> PodDeletes { get; } = [];

        public List<string?> PodListSelectors { get; } = [];

        public int CreateCount { get; set; }

        public Harness(V1PersistentVolumeClaim? live, Exception? deleteFault = null, params V1Pod[] pods)
        {
            if (live is null)
            {
                Core.ReadNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                        Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                    .Returns<Task<HttpOperationResponse<V1PersistentVolumeClaim>>>(_ => throw Http(HttpStatusCode.NotFound));
            }
            else
            {
                Core.ReadNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                        Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(Ok(live)));
            }

            Core.DeleteNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                    Arg.Any<string>(), Namespace, Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<bool?>(),
                    Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    ClaimDeletes.Add(ci.ArgAt<V1DeleteOptions>(2));
                    return deleteFault is null
                        ? Task.FromResult(Ok(live!))
                        : Task.FromException<HttpOperationResponse<V1PersistentVolumeClaim>>(deleteFault);
                });

            Core.ListNamespacedPodWithHttpMessagesAsync(
                    Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                    Arg.Any<bool?>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    PodListSelectors.Add(ci.ArgAt<string>(4));
                    return Task.FromResult(Ok(new V1PodList { Items = new List<V1Pod>(pods) }));
                });

            Core.DeleteNamespacedPodWithHttpMessagesAsync(
                    Arg.Any<string>(), Namespace, Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<bool?>(),
                    Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    PodDeletes.Add(ci.ArgAt<string>(0));
                    return Task.FromException<HttpOperationResponse<V1Pod>>(Http(HttpStatusCode.Forbidden));
                });

            Core.CreateNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                    Arg.Any<V1PersistentVolumeClaim>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    CreateCount++;
                    return Task.FromResult(Ok(ci.ArgAt<V1PersistentVolumeClaim>(0)));
                });

            Client = Substitute.For<IKubernetes>();
            Client.CoreV1.Returns(Core);
        }
    }

    [Fact]
    public async Task An_unbound_claim_of_another_class_is_deleted_its_pods_are_left_alone_and_the_apply_retries()
    {
        var harness = new Harness(
            Live("old-class", "Pending"),
            deleteFault: null,
            PodMounting("pod-a", ClaimName),
            PodMounting("pod-other", "some-other-claim"));

        var act = async () => await NewClient().EnsurePvcAsync(
            harness.Client, Namespace, Desired("local-path"), isAdoptedClaim: false, CancellationToken.None);

        await act.Should().ThrowAsync<KubernetesPvcTerminatingException>(
            "the claim is gone only once its deletion completes, so the apply retries next pass and creates it then");
        harness.ClaimDeletes.Should().ContainSingle();
        harness.ClaimDeletes[0]!.Preconditions.Should().BeEquivalentTo(
            new V1Preconditions { Uid = "uid-1", ResourceVersion = "42" },
            "the delete is conditional on the version read as unbound, so a claim that bound meanwhile is kept");
        harness.PodListSelectors.Should().BeEmpty(
            "pvc-protection ignores an unscheduled pod, so the claim goes without the pod being deleted");
        harness.PodDeletes.Should().BeEmpty("the host may not delete pods");
        harness.CreateCount.Should().Be(0, "the name stays taken until the deletion completes");
    }

    [Fact]
    public async Task A_claim_still_terminating_after_the_delete_is_not_created_again_until_it_is_gone()
    {
        var terminating = Live("old-class", "Pending");
        terminating.Metadata.DeletionTimestamp = DateTime.UtcNow;
        var harness = new Harness(terminating, deleteFault: null, PodMounting("pod-a", ClaimName));

        var act = async () => await NewClient().EnsurePvcAsync(
            harness.Client, Namespace, Desired("local-path"), isAdoptedClaim: false, CancellationToken.None);

        await act.Should().ThrowAsync<KubernetesPvcTerminatingException>();
        harness.ClaimDeletes.Should().BeEmpty("a claim already being deleted is only waited for");
        harness.CreateCount.Should().Be(0);
    }

    [Fact]
    public async Task A_claim_deleted_by_someone_else_before_the_delete_still_retries_the_apply()
    {
        var harness = new Harness(Live("old-class", "Pending"), Http(HttpStatusCode.NotFound), PodMounting("pod-a", ClaimName));

        var act = async () => await NewClient().EnsurePvcAsync(
            harness.Client, Namespace, Desired("local-path"), isAdoptedClaim: false, CancellationToken.None);

        await act.Should().ThrowAsync<KubernetesPvcTerminatingException>("the next pass creates the claim");
        harness.PodDeletes.Should().BeEmpty();
        harness.CreateCount.Should().Be(0);
    }

    [Theory]
    [InlineData("Bound", "pv-1")]
    [InlineData("Lost", "pv-1")]
    [InlineData("Pending", "pv-1")]
    public async Task A_bound_claim_is_never_deleted_whatever_its_class(string phase, string volumeName)
    {
        var harness = new Harness(Live("old-class", phase, volumeName), deleteFault: null, PodMounting("pod-a", ClaimName));

        await NewClient().EnsurePvcAsync(
            harness.Client, Namespace, Desired("local-path"), isAdoptedClaim: false, CancellationToken.None);

        harness.ClaimDeletes.Should().BeEmpty("a bound claim holds data");
        harness.PodDeletes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_adopted_claim_is_never_deleted()
    {
        var harness = new Harness(Live("old-class", "Pending"), deleteFault: null, PodMounting("pod-a", ClaimName));

        await NewClient().EnsurePvcAsync(
            harness.Client, Namespace, Desired("local-path"), isAdoptedClaim: true, CancellationToken.None);

        harness.ClaimDeletes.Should().BeEmpty("a restore's retained claim is reused, never replaced");
    }

    [Fact]
    public async Task A_claim_that_changed_since_the_read_is_kept_and_its_pods_are_left_running()
    {
        var harness = new Harness(Live("old-class", "Pending"), Http(HttpStatusCode.Conflict), PodMounting("pod-a", ClaimName));

        var act = async () => await NewClient().EnsurePvcAsync(
            harness.Client, Namespace, Desired("local-path"), isAdoptedClaim: false, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed after it was read as unbound*");
        harness.PodDeletes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_claim_is_created_with_the_current_class()
    {
        var harness = new Harness(live: null);

        await NewClient().EnsurePvcAsync(
            harness.Client, Namespace, Desired("local-path"), isAdoptedClaim: false, CancellationToken.None);

        harness.CreateCount.Should().Be(1);
        await harness.Core.Received(1).CreateNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
            Arg.Is<V1PersistentVolumeClaim>(c => c.Spec.StorageClassName == "local-path"), Namespace, Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
    }

    private static IKubernetes ClientReadingOneDeployment(Func<V1PersistentVolumeClaimList> claims)
    {
        var deployment = new V1Deployment
        {
            Metadata = new V1ObjectMeta
            {
                Name = "reeve-wl-matter-11111111",
                Labels = new Dictionary<string, string> { [InstanceLabel] = InstanceId },
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        Containers = new List<V1Container>(),
                        Volumes = new List<V1Volume>
                        {
                            new() { Name = "data", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = ClaimName } },
                            new() { Name = "cache", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = "present-claim" } },
                        },
                    },
                },
            },
            Status = new V1DeploymentStatus { ReadyReplicas = 0 },
        };

        var apps = Substitute.For<IAppsV1Operations>();
        apps.ListNamespacedDeploymentWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok(new V1DeploymentList { Items = new List<V1Deployment> { deployment } })));
        apps.ListNamespacedDaemonSetWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Ok(new V1DaemonSetList { Items = new List<V1DaemonSet>() })));

        var core = Substitute.For<ICoreV1Operations>();
        core.ListNamespacedPersistentVolumeClaimWithHttpMessagesAsync(
                Namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Ok(claims())));

        var client = Substitute.For<IKubernetes>();
        client.AppsV1.Returns(apps);
        client.CoreV1.Returns(core);
        return client;
    }

    private static readonly IReadOnlyDictionary<string, string> Selector =
        new Dictionary<string, string> { [InstanceLabel] = InstanceId };

    [Fact]
    public async Task The_workload_read_names_the_mounted_claims_that_do_not_exist()
    {
        var client = ClientReadingOneDeployment(() => new V1PersistentVolumeClaimList
        {
            Items = new List<V1PersistentVolumeClaim>
            {
                new() { Metadata = new V1ObjectMeta { Name = "present-claim", DeletionTimestamp = DateTime.UtcNow } },
            },
        });

        var raw = await NewClient().ReadWorkloadAsync(client, Namespace, Selector, CancellationToken.None);

        raw!.MissingClaims.Should().Equal(new[] { ClaimName },
            "a Terminating claim is still held by its pod, so only the absent one is missing");
        KubernetesStatusMapper.Map(raw).MissingVolumes.Should().Equal(ClaimName);
    }

    [Fact]
    public async Task An_unreadable_claim_list_reports_nothing_missing()
    {
        var client = ClientReadingOneDeployment(() => throw Http(HttpStatusCode.Forbidden));

        var raw = await NewClient().ReadWorkloadAsync(client, Namespace, Selector, CancellationToken.None);

        raw!.Found.Should().BeTrue();
        raw.MissingClaims.Should().BeEmpty("a forbidden list is not evidence that a volume was deleted");
    }

    [Fact]
    public async Task The_fleet_read_names_the_missing_claims_per_instance()
    {
        var client = ClientReadingOneDeployment(() => new V1PersistentVolumeClaimList
        {
            Items = new List<V1PersistentVolumeClaim> { new() { Metadata = new V1ObjectMeta { Name = "present-claim" } } },
        });

        var raws = await NewClient().ReadWorkloadsAsync(client, Namespace, [Guid.Parse(InstanceId)], CancellationToken.None);

        raws[Guid.Parse(InstanceId)].MissingClaims.Should().Equal(ClaimName);
    }
}
