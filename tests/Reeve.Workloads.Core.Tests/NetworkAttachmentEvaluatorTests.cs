using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Core.Network;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class NetworkAttachmentEvaluatorTests
{
    private const string AttachmentName = "iot-macvlan";

    public static TheoryData<string> Providers => new() { DockerProvider.Id.Value, KubernetesProvider.Id.Value };

    [Theory(DisplayName = "an existing attachment passes with only the exists and eligibility checks")]
    [MemberData(nameof(Providers))]
    public async Task An_existing_attachment_passes_with_only_the_exists_and_eligibility_checks(string provider)
    {
        var evaluation = await BuildEvaluator().EvaluateAsync(Request(provider, exists: true));

        evaluation.Checks.Select(c => c.Id).Should().BeSubsetOf(NetworkAttachmentCheckIds.All);
        evaluation.HasBlockingError.Should().BeFalse();
        var exists = evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists);
        exists.Passed.Should().BeTrue();
        exists.Message.Should().Contain(AttachmentName).And.Contain("present on the")
            .And.Contain(Descriptor(provider).Location);
        evaluation.Checks.Select(c => c.Message).Should().AllSatisfy(m =>
            m.Should().NotContainEquivalentOf("route")
                .And.NotContainEquivalentOf("IPv6")
                .And.NotContainEquivalentOf("selectable"));
    }

    [Theory(DisplayName = "a missing attachment blocks with the provider's missing description")]
    [MemberData(nameof(Providers))]
    public async Task A_missing_attachment_blocks_with_the_providers_missing_description(string provider)
    {
        var source = Sources().Single(s => s.Provider.Value == provider);

        var evaluation = await BuildEvaluator().EvaluateAsync(Request(provider, exists: false));

        var exists = evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists);
        exists.Passed.Should().BeFalse();
        exists.Severity.Should().Be(PreflightSeverity.Error);
        exists.Message.Should().Be(source.DescribeMissing(AttachmentName)).And.Contain(AttachmentName);
        evaluation.HasBlockingError.Should().BeTrue();
    }

    [Theory(DisplayName = "an unavailable-reason that is blank or only none-in-namespace in other casing reads as missing")]
    [InlineData("  ")]
    [InlineData("")]
    [InlineData(" None-In-Namespace ")]
    public async Task A_blank_or_recased_none_in_namespace_reason_reads_as_missing(string reason)
    {
        var source = Sources().Single(s => s.Provider == KubernetesProvider.Id);

        var evaluation = await BuildEvaluator().EvaluateAsync(
            Request(KubernetesProvider.Id.Value, exists: false) with { EnumerationUnavailableReason = reason });

        evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists).Message
            .Should().Be(source.DescribeMissing(AttachmentName));
    }

    [Fact(DisplayName = "an unavailable enumeration is reported before the no-names error")]
    public async Task An_unavailable_enumeration_is_reported_before_the_no_names_error()
    {
        var evaluation = await BuildEvaluator().EvaluateAsync(
            Request(KubernetesProvider.Id.Value, exists: false) with
            {
                Attachments = [],
                EnumerationUnavailableReason = "forbidden",
            });

        var exists = evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists);
        exists.Message.Should().Contain("forbidden").And.NotContain("names no attachment");
    }

    [Fact(DisplayName = "with two attachments only the missing one is described")]
    public async Task With_two_attachments_only_the_missing_one_is_described()
    {
        var evaluation = await BuildEvaluator().EvaluateAsync(
            Request(DockerProvider.Id.Value, exists: true) with
            {
                Attachments =
                [
                    new NetworkAttachmentObservation { Name = "present-net", Exists = true },
                    new NetworkAttachmentObservation { Name = "absent-net", Exists = false },
                ],
            });

        var exists = evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists);
        exists.Passed.Should().BeFalse();
        exists.Message.Should().Contain("absent-net").And.NotContain("present-net");
        evaluation.HasBlockingError.Should().BeTrue();
    }

    [Fact(DisplayName = "with two missing attachments both are described")]
    public async Task With_two_missing_attachments_both_are_described()
    {
        var evaluation = await BuildEvaluator().EvaluateAsync(
            Request(DockerProvider.Id.Value, exists: false) with
            {
                Attachments =
                [
                    new NetworkAttachmentObservation { Name = "absent-a", Exists = false },
                    new NetworkAttachmentObservation { Name = "absent-b", Exists = false },
                ],
            });

        var source = Sources().Single(x => x.Provider == DockerProvider.Id);
        evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists).Message
            .Should().Be(source.DescribeMissing("absent-a") + " " + source.DescribeMissing("absent-b"));
    }

    [Fact(DisplayName = "two present attachments pass in the plural")]
    public async Task Two_present_attachments_pass_in_the_plural()
    {
        var evaluation = await BuildEvaluator().EvaluateAsync(
            Request(DockerProvider.Id.Value, exists: true) with
            {
                Attachments =
                [
                    new NetworkAttachmentObservation { Name = "net-a", Exists = true },
                    new NetworkAttachmentObservation { Name = "net-b", Exists = true },
                ],
            });

        evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists).Message
            .Should().Contain("net-a").And.Contain("net-b").And.Contain(" are present on the");
        evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists).Message
            .Should().Be("'net-a', 'net-b' are present on the docker host provider.");
    }

    [Theory(DisplayName = "a provider that does not schedule across nodes emits no node-eligibility check")]
    [InlineData(WorkloadKind.PerNode)]
    [InlineData(WorkloadKind.Service)]
    public async Task A_provider_that_does_not_schedule_across_nodes_emits_no_node_eligibility_check(WorkloadKind kind)
    {
        var evaluation = await BuildEvaluator().EvaluateAsync(
            Request(DockerProvider.Id.Value, exists: true) with { Kind = kind, NodeEligibilitySelector = null });

        evaluation.Checks.Select(c => c.Id).Should().NotContain(NetworkAttachmentCheckIds.NodeEligibility);
        evaluation.HasBlockingError.Should().BeFalse();
    }

    [Theory(DisplayName = "an unavailable enumeration blocks naming its reason and none-in-namespace reads as missing")]
    [MemberData(nameof(Providers))]
    public async Task An_unavailable_enumeration_blocks_naming_its_reason_and_none_in_namespace_reads_as_missing(
        string provider)
    {
        var unavailable = await BuildEvaluator().EvaluateAsync(
            Request(provider, exists: false) with { EnumerationUnavailableReason = "forbidden" });
        var unavailableCheck = unavailable.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists);
        unavailableCheck.Severity.Should().Be(PreflightSeverity.Error);
        unavailableCheck.Passed.Should().BeFalse();
        unavailableCheck.Message.Should().Contain("forbidden").And.Contain(AttachmentName);
        unavailable.HasBlockingError.Should().BeTrue();

        var emptyNamespace = await BuildEvaluator().EvaluateAsync(
            Request(provider, exists: false) with
            {
                EnumerationUnavailableReason = WorkloadNetworkAttachmentUnavailableReasons.NoneInNamespace,
            });
        var emptyCheck = emptyNamespace.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists);
        emptyCheck.Severity.Should().Be(PreflightSeverity.Error);
        emptyCheck.Message.Should().NotContain(WorkloadNetworkAttachmentUnavailableReasons.NoneInNamespace);
        emptyCheck.Message.Should().Be(
            Sources().Single(s => s.Provider.Value == provider).DescribeMissing(AttachmentName));
    }

    [Fact(DisplayName = "an operatorNetwork request naming no attachment blocks")]
    public async Task An_operatorNetwork_request_naming_no_attachment_blocks()
    {
        var evaluation = await BuildEvaluator().EvaluateAsync(
            Request(DockerProvider.Id.Value, exists: true) with { Attachments = [] });

        var exists = evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists);
        exists.Severity.Should().Be(PreflightSeverity.Error);
        exists.Passed.Should().BeFalse();
        exists.Message.Should().Contain("names no attachment");
        evaluation.HasBlockingError.Should().BeTrue();
    }

    [Fact(DisplayName = "a provider with no source reports info and never blocks")]
    public async Task A_provider_with_no_source_reports_info_and_never_blocks()
    {
        var evaluation = await BuildEvaluator().EvaluateAsync(
            new NetworkAttachmentEvaluationRequest
            {
                Provider = ManualProvider.Id,
                Mode = WorkloadNetworkMode.OperatorNetwork,
                Kind = WorkloadKind.Service,
                Attachments = [new NetworkAttachmentObservation { Name = AttachmentName, Exists = false }],
            });

        var exists = evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.Exists);
        exists.Severity.Should().Be(PreflightSeverity.Info);
        exists.Passed.Should().BeTrue();
        exists.Message.Should().NotContainEquivalentOf("route").And.NotContainEquivalentOf("IPv6");
        evaluation.HasBlockingError.Should().BeFalse();
    }

    [Theory(DisplayName = "a fan-out kind without a selector blocks and a service only warns")]
    [InlineData(WorkloadKind.PerNode, true)]
    [InlineData(WorkloadKind.ScanPerNode, true)]
    [InlineData(WorkloadKind.Service, false)]
    [InlineData(WorkloadKind.SingleNode, false)]
    public async Task A_fan_out_kind_without_a_selector_blocks_and_a_service_only_warns(
        WorkloadKind kind,
        bool expectBlocking)
    {
        var evaluation = await BuildEvaluator().EvaluateAsync(
            Request(KubernetesProvider.Id.Value, exists: true) with { Kind = kind, NodeEligibilitySelector = null });

        var check = evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.NodeEligibility);
        check.Passed.Should().BeFalse();
        check.Severity.Should().Be(expectBlocking ? PreflightSeverity.Error : PreflightSeverity.Warning);
        evaluation.HasBlockingError.Should().Be(expectBlocking);
    }

    [Fact(DisplayName = "a resolved node-eligibility selector clears the check on every kind")]
    public async Task A_resolved_node_eligibility_selector_clears_the_check_on_every_kind()
    {
        var evaluation = await BuildEvaluator().EvaluateAsync(
            Request(KubernetesProvider.Id.Value, exists: true) with
            {
                Kind = WorkloadKind.PerNode,
                NodeEligibilitySelector = new Dictionary<string, string> { ["reeve-iot.dev/iot-vlan"] = "true" },
            });

        evaluation.Checks.Single(c => c.Id == NetworkAttachmentCheckIds.NodeEligibility).Passed.Should().BeTrue();
        evaluation.HasBlockingError.Should().BeFalse();
    }

    [Fact(DisplayName = "a non-operatorNetwork workload is not evaluated at all")]
    public async Task A_non_operatorNetwork_workload_is_not_evaluated_at_all()
    {
        var evaluation = await BuildEvaluator().EvaluateAsync(
            Request(KubernetesProvider.Id.Value, exists: true) with { Mode = WorkloadNetworkMode.Cluster });

        evaluation.Should().BeSameAs(NetworkAttachmentEvaluation.NotEvaluated);
        evaluation.Checks.Should().BeEmpty();
    }

    [Fact(DisplayName = "the family owns exactly two ids")]
    public void The_family_owns_exactly_two_ids()
    {
        NetworkAttachmentCheckIds.All.Should().BeEquivalentTo(
            [NetworkAttachmentCheckIds.Exists, NetworkAttachmentCheckIds.NodeEligibility]);
    }

    [Theory(DisplayName = "an attachment listed identically after its network changed evaluates identically")]
    [MemberData(nameof(Providers))]
    public async Task An_attachment_listed_identically_after_its_network_changed_evaluates_identically(string provider)
    {
        var listedBefore = new WorkloadNetworkAttachmentDto { Name = AttachmentName };
        var listedAfterTheNetworkChanged = new WorkloadNetworkAttachmentDto { Name = AttachmentName };

        var before = await BuildEvaluator().EvaluateAsync(RequestFromListing(provider, listedBefore));
        var after = await BuildEvaluator().EvaluateAsync(RequestFromListing(provider, listedAfterTheNetworkChanged));

        after.Checks.Should().BeEquivalentTo(before.Checks, o => o.WithStrictOrdering());
        after.HasBlockingError.Should().Be(before.HasBlockingError);
    }

    private static NetworkAttachmentEvaluationRequest RequestFromListing(string provider, WorkloadNetworkAttachmentDto listed)
        => Request(provider, exists: true) with
        {
            Attachments = [new NetworkAttachmentObservation { Name = listed.Name, Exists = true }],
        };

    private static NetworkAttachmentEvaluationRequest Request(string provider, bool exists)
        => new()
        {
            Provider = WorkloadProviderId.Parse(provider),
            Mode = WorkloadNetworkMode.OperatorNetwork,
            Kind = WorkloadKind.Service,
            NodeEligibilitySelector = new Dictionary<string, string> { ["reeve-iot.dev/iot-vlan"] = "true" },
            Attachments = [new NetworkAttachmentObservation { Name = AttachmentName, Exists = exists }],
        };

    private static INetworkAttachmentSource[] Sources() =>
    [
        new KubernetesNetworkAttachmentSource(null!, null!, null!),
        new DockerNetworkAttachmentSource(null!, null!),
    ];

    private static WorkloadProviderDescriptor Descriptor(string provider)
        => provider == DockerProvider.Id.Value ? DockerProvider.Descriptor : KubernetesProvider.Descriptor;

    private static NetworkAttachmentEvaluator BuildEvaluator()
        => new(new DescriptorRegistry(), Sources());

    private sealed class DescriptorRegistry : IWorkloadProviderRegistry
    {
        public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders => [];

        public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors => [];

        public IWorkloadProvider? TryResolve(WorkloadProviderId provider) => null;

        public IWorkloadProvider Resolve(WorkloadProviderId provider) => throw new WorkloadProviderNotRegisteredException(provider);

        public WorkloadProviderDescriptor Describe(WorkloadProviderId provider)
            => provider == DockerProvider.Id ? DockerProvider.Descriptor
                : provider == KubernetesProvider.Id ? KubernetesProvider.Descriptor
                : new WorkloadProviderDescriptor { Id = provider };
    }
}
