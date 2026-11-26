using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.DeploymentFixtures;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadParameterReferenceResolverTests
{
    [Fact]
    public async Task ReferenceValuesResolveForRenderingAndRemainReferencesForHashing()
    {
        var resolver = Substitute.For<IWorkloadParameterReferenceResolver>();
        resolver.ResolveAsync("ref:secrets:app#password", Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Success("resolved-secret"));
        var workload = DeploymentManifestBuilder.Workload(
            parameters: [DeploymentManifestBuilder.Param("dbPassword", required: true, type: WorkloadParameterType.Password)]);
        var inputs = new Dictionary<string, string?> { ["dbPassword"] = "ref:secrets:app#password" };

        var result = await WorkloadParameterReferenceResolutionHelper.ResolveAsync(
            workload, inputs, KubernetesProvider.Descriptor, resolver, cancellationToken: CancellationToken.None);

        result.Values["dbPassword"].Should().Be("resolved-secret");
        result.ReferencesForHash["dbPassword"].Should().Be("ref:secrets:app#password");
        result.UnpinnedReferences.Should().ContainKey("dbPassword");
    }

    [Fact]
    public async Task Resolver_pin_metadata_excludes_pinned_reference_from_rollout_guard()
    {
        var resolver = Substitute.For<IWorkloadParameterReferenceResolver>();
        resolver.ResolveAsync("ref:secrets:app#password@v7", Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Success("resolved-secret", IsVersionPinned: true));
        var workload = DeploymentManifestBuilder.Workload(
            parameters: [DeploymentManifestBuilder.Param("dbPassword", required: true, type: WorkloadParameterType.Password)]);

        var result = await WorkloadParameterReferenceResolutionHelper.ResolveAsync(
            workload, new Dictionary<string, string?> { ["dbPassword"] = "ref:secrets:app#password@v7" },
            KubernetesProvider.Descriptor, resolver, cancellationToken: CancellationToken.None);

        result.ReferencesForHash.Should().ContainKey("dbPassword");
        result.UnpinnedReferences.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolverFailureNamesTheReferenceAndReason()
    {
        var resolver = Substitute.For<IWorkloadParameterReferenceResolver>();
        resolver.ResolveAsync("ref:missing", Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Failure(WorkloadReferenceFailureKind.NotFound, "secret not found"));
        var workload = DeploymentManifestBuilder.Workload(
            parameters: [DeploymentManifestBuilder.Param("dbPassword", required: true, type: WorkloadParameterType.Password)]);

        Func<Task> act = async () =>
        {
            _ = await WorkloadParameterReferenceResolutionHelper.ResolveAsync(
                workload, new Dictionary<string, string?> { ["dbPassword"] = "ref:missing" },
                KubernetesProvider.Descriptor, resolver, cancellationToken: CancellationToken.None);
        };

        var error = await act.Should().ThrowAsync<WorkloadParameterReferenceResolutionException>();
        error.Which.Reference.Should().Be("ref:missing");
        error.Which.Message.Should().Contain("NotFound").And.Contain("secret not found");
    }

    [Fact]
    public async Task LiteralPasswordIsNotSentToResolver()
    {
        var resolver = Substitute.For<IWorkloadParameterReferenceResolver>();
        var workload = DeploymentManifestBuilder.Workload(
            parameters: [DeploymentManifestBuilder.Param("dbPassword", required: true, type: WorkloadParameterType.Password)]);
        var result = await WorkloadParameterReferenceResolutionHelper.ResolveAsync(
            workload, new Dictionary<string, string?> { ["dbPassword"] = "literal" },
            KubernetesProvider.Descriptor, resolver, cancellationToken: CancellationToken.None);

        result.Values["dbPassword"].Should().Be("literal");
        result.ReferencesForHash.Should().BeEmpty();
        await resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankSuccessDoesNotResolveToAnEmptyPassword(string resolvedValue)
    {
        var resolver = Substitute.For<IWorkloadParameterReferenceResolver>();
        resolver.ResolveAsync("ref:blank", Arg.Any<CancellationToken>())
            .Returns(new WorkloadReferenceResolution.Success(resolvedValue));
        var workload = DeploymentManifestBuilder.Workload(
            parameters: [DeploymentManifestBuilder.Param("dbPassword", required: true, type: WorkloadParameterType.Password)]);

        var act = async () => await WorkloadParameterReferenceResolutionHelper.ResolveAsync(
            workload, new Dictionary<string, string?> { ["dbPassword"] = "ref:blank" },
            KubernetesProvider.Descriptor, resolver, cancellationToken: CancellationToken.None);

        var error = await act.Should().ThrowAsync<WorkloadParameterReferenceResolutionException>();
        error.Which.FailureKind.Should().Be(WorkloadReferenceFailureKind.NotFound);
    }
}
