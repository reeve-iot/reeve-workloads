using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Requirements;

public sealed class WorkloadRequirementsServiceTests
{
    private const string GroupId = "acme.zwave";
    private const string WorkloadId = "controller";
    private const string SourceKey = "repo-under-test";

    private readonly IWorkloadGroupManifestSource manifests = Substitute.For<IWorkloadGroupManifestSource>();
    private readonly IWorkloadOrchestratorSettingsService settings = Substitute.For<IWorkloadOrchestratorSettingsService>();
    private readonly IWorkloadProviderRegistry providers = Substitute.For<IWorkloadProviderRegistry>();
    private readonly IWorkloadInstanceRepository instances = Substitute.For<IWorkloadInstanceRepository>();
    private readonly IWorkloadReconcileInputsProvider inputs = Substitute.For<IWorkloadReconcileInputsProvider>();
    private readonly IWorkloadUpdatePlanner planner = Substitute.For<IWorkloadUpdatePlanner>();

    public WorkloadRequirementsServiceTests()
    {
        providers.Describe(Arg.Any<WorkloadProviderId>()).Returns(call => ShippedProviders.For(call.Arg<WorkloadProviderId>()));
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new WorkloadOrchestratorSettingsResponse
        {
            Settings = new WorkloadOrchestratorSettings(),
            EffectiveProvider = KubernetesProvider.Id,
            AutoDetected = false,
            Providers = [],
        });
    }

    [Fact]
    public async Task An_install_reads_what_the_resolved_workload_requires()
    {
        Publish(Manifest());

        var result = await Service().ForInstallAsync(InstallQuery(new() { ["serialPort"] = "/dev/ttyUSB0" }));

        result.Status.Should().Be(WorkloadRequirementsStatus.Found);
        var read = result.Read!;
        read.Before.Should().BeNull("nothing is deployed before an install");
        read.After[WorkloadId].DevicePaths.Should().BeEquivalentTo(["/dev/ttyUSB0"]);
        read.After[WorkloadId].Privileged.Should().BeTrue("a device bound on Kubernetes runs in a privileged container");
        read.After[WorkloadId].Should().BeEquivalentTo(WorkloadPrivilegeRequirements.FromWorkload(
            Manifest().Workloads[0],
            new Dictionary<string, string> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" },
            KubernetesProvider.Descriptor));
        read.Fingerprint.Should().Be(WorkloadPrivilegeRequirementsPolicy.Fingerprint(read.After));
        read.AddedWorkloadIds.Should().BeEmpty();
        read.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_install_with_no_provider_named_resolves_against_the_effective_one()
    {
        Publish(Manifest());

        await Service().ForInstallAsync(InstallQuery(new() { ["serialPort"] = "/dev/ttyUSB0" }));

        providers.Received().Describe(KubernetesProvider.Id);
    }

    [Fact]
    public async Task An_install_naming_a_provider_does_not_ask_the_settings()
    {
        Publish(Manifest());

        await Service().ForInstallAsync(
            InstallQuery(new() { ["serialPort"] = "/dev/ttyUSB0" }) with { Provider = WorkloadProviderId.Parse("docker") });

        await settings.DidNotReceive().GetAsync(Arg.Any<CancellationToken>());
        providers.Received().Describe(WorkloadProviderId.Parse("docker"));
    }

    [Fact]
    public async Task An_install_of_a_version_with_no_manifest_is_not_found()
    {
        manifests.GetAtVersionAsync(SourceKey, GroupId, "1.0.0", Arg.Any<CancellationToken>())
            .Returns(GroupManifestRead.Absent);

        var result = await Service().ForInstallAsync(InstallQuery([]));

        result.Status.Should().Be(WorkloadRequirementsStatus.NotFound);
        result.Read.Should().BeNull();
    }

    [Fact]
    public async Task An_install_of_a_manifest_this_build_refuses_is_invalid_not_absent()
    {
        manifests.GetAtVersionAsync(SourceKey, GroupId, "1.0.0", Arg.Any<CancellationToken>())
            .Returns(GroupManifestRead.Unreadable);

        (await Service().ForInstallAsync(InstallQuery([]))).Status.Should().Be(WorkloadRequirementsStatus.Invalid);
    }

    [Fact]
    public async Task An_install_value_the_resolver_refuses_is_invalid_with_its_reason()
    {
        Publish(Manifest());

        var result = await Service().ForInstallAsync(InstallQuery(new() { ["serialPort"] = "/etc/shadow" }));

        result.Status.Should().Be(WorkloadRequirementsStatus.Invalid);
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_bundled_install_reads_the_bundled_manifest()
    {
        manifests.GetAvailableAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(new AvailableGroupManifestRead { Bundled = Manifest() });

        var result = await Service().ForInstallAsync(
            InstallQuery(new() { ["serialPort"] = "/dev/ttyUSB0" }) with { Version = null });

        result.Status.Should().Be(WorkloadRequirementsStatus.Found);
        result.Read!.After[WorkloadId].DevicePaths.Should().BeEquivalentTo(["/dev/ttyUSB0"]);
        await manifests.DidNotReceiveWithAnyArgs().GetAtVersionAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_versionless_install_of_a_module_that_publishes_versions_is_not_found()
    {
        manifests.GetAvailableAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(new AvailableGroupManifestRead { HighestAvailable = Manifest(), Bundled = Manifest() });

        var result = await Service().ForInstallAsync(InstallQuery([]) with { Version = null });

        result.Status.Should().Be(WorkloadRequirementsStatus.NotFound);
        result.Message.Should().Contain("installed at one");
    }

    [Fact]
    public async Task A_bundled_install_whose_declaration_this_build_refuses_is_invalid()
    {
        manifests.GetAvailableAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(new AvailableGroupManifestRead { Refused = true });

        var result = await Service().ForInstallAsync(InstallQuery([]) with { Version = null });

        result.Status.Should().Be(WorkloadRequirementsStatus.Invalid);
        result.Message.Should().Contain("(bundled)");
    }

    [Fact]
    public async Task A_bundled_install_of_a_module_with_nothing_declared_is_not_found()
    {
        manifests.GetAvailableAsync(GroupId, Arg.Any<CancellationToken>()).Returns(AvailableGroupManifestRead.Nothing);

        (await Service().ForInstallAsync(InstallQuery([]) with { Version = null })).Status
            .Should().Be(WorkloadRequirementsStatus.NotFound);
    }

    [Fact]
    public async Task An_edit_that_widens_a_device_path_shows_the_escalation_and_moves_the_fingerprint()
    {
        var instance = Deployed(new() { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });

        var result = await Service().ForParameterEditAsync(
            instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB1" });

        var read = result.Read!;
        read.Before![WorkloadId].DevicePaths.Should().BeEquivalentTo(["/dev/ttyUSB0"]);
        read.After[WorkloadId].DevicePaths.Should().BeEquivalentTo(["/dev/ttyUSB1"]);
        read.Changes[WorkloadId].Security.Should().Be(RequirementChangeKind.Escalation);
        read.Fingerprint.Should().NotBe(WorkloadPrivilegeRequirementsPolicy.Fingerprint(read.Before));
    }

    [Fact]
    public async Task An_edit_that_changes_no_requirement_keeps_the_fingerprint()
    {
        var instance = Deployed(new() { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });

        var result = await Service().ForParameterEditAsync(
            instance.Id, new Dictionary<string, string?> { ["logLevel"] = "debug" });

        var read = result.Read!;
        read.Changes[WorkloadId].Security.Should().Be(RequirementChangeKind.Unchanged);
        read.Fingerprint.Should().Be(WorkloadPrivilegeRequirementsPolicy.Fingerprint(read.Before!));
    }

    [Fact]
    public async Task A_masked_value_is_read_as_the_stored_one_as_the_edit_reads_it()
    {
        var instance = Deployed(new() { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });

        var result = await Service().ForParameterEditAsync(
            instance.Id, new Dictionary<string, string?> { ["serialPort"] = WorkloadParameterMaskPolicy.MaskSentinel });

        result.Read!.After[WorkloadId].DevicePaths.Should().BeEquivalentTo(["/dev/ttyUSB0"]);
    }

    [Fact]
    public async Task An_edit_of_an_unknown_instance_is_not_found()
        => (await Service().ForParameterEditAsync(Guid.NewGuid(), new Dictionary<string, string?>()))
            .Status.Should().Be(WorkloadRequirementsStatus.NotFound);

    [Fact]
    public async Task An_edit_read_under_a_host_refusal_is_refused_by_name_and_not_not_found()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, DateTimeOffset.UnixEpoch);
        instances.GetByIdAsync(instance.Id, Arg.Any<CancellationToken>()).Returns(instance);
        inputs.GetInputsAsync(instance, Arg.Any<CancellationToken>())
            .Returns(WorkloadReconcileInputsResult.HostRefused("host says no"));

        var result = await Service().ForParameterEditAsync(instance.Id, new Dictionary<string, string?>());

        result.Status.Should().Be(WorkloadRequirementsStatus.HostRefused);
        result.Message.Should().Contain("host says no");
        result.Read.Should().BeNull();
    }

    [Fact]
    public async Task An_edit_of_a_workload_no_longer_declared_is_not_found()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, DateTimeOffset.UnixEpoch);
        instances.GetByIdAsync(instance.Id, Arg.Any<CancellationToken>()).Returns(instance);
        inputs.GetInputsAsync(instance, Arg.Any<CancellationToken>()).Returns(WorkloadReconcileInputsResult.NotDeclared);

        (await Service().ForParameterEditAsync(instance.Id, new Dictionary<string, string?>()))
            .Status.Should().Be(WorkloadRequirementsStatus.NotFound);
    }

    [Fact]
    public async Task An_edit_value_the_resolver_refuses_is_invalid()
    {
        var instance = Deployed(new() { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });

        (await Service().ForParameterEditAsync(instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/etc/shadow" }))
            .Status.Should().Be(WorkloadRequirementsStatus.Invalid);
    }

    [Fact]
    public async Task An_update_reads_before_and_after_from_the_plans_own_requirements()
    {
        var groupInstanceId = Guid.NewGuid();
        var previous = MinimalSpec(WorkloadId);
        var target = previous with { Privileges = new ResolvedPrivileges { Capabilities = ["NET_ADMIN"] } };
        planner.BuildPlanAsync(groupInstanceId, "2.0.0", null, null, null, Arg.Any<Reeve.Workloads.Core.Update.WorkloadUpdatePlanOrigin?>(), Arg.Any<CancellationToken>())
            .Returns(Plan(groupInstanceId,
                previous: new() { [WorkloadId] = previous },
                target: new() { [WorkloadId] = target, ["added"] = MinimalSpec("added") }));

        var result = await Service().ForUpdateAsync(groupInstanceId, "2.0.0");

        var read = result.Read!;
        read.Before![WorkloadId].Capabilities.Should().BeEmpty();
        read.After[WorkloadId].Capabilities.Should().BeEquivalentTo(["NET_ADMIN"]);
        read.Changes[WorkloadId].Security.Should().Be(RequirementChangeKind.Escalation);
        read.AddedWorkloadIds.Should().Equal("added");
        read.Changes.Should().NotContainKey("added", "a workload with nothing before has no change, only an addition");
        read.Fingerprint.Should().Be(WorkloadPrivilegeRequirementsPolicy.Fingerprint(read.After));
    }

    [Fact]
    public async Task An_update_read_plans_with_the_submitted_values_and_adoptions()
    {
        var groupInstanceId = Guid.NewGuid();
        var submitted = new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyACM1" };
        IReadOnlyCollection<string> adopt = ["zigbee2mqtt"];
        var spec = MinimalSpec(WorkloadId);
        var plan = Plan(groupInstanceId, previous: new() { [WorkloadId] = spec }, target: new() { [WorkloadId] = spec });
        planner.BuildPlanAsync(groupInstanceId, "2.0.0", null, submitted, adopt, Arg.Any<Reeve.Workloads.Core.Update.WorkloadUpdatePlanOrigin?>(), Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = await Service().ForUpdateAsync(groupInstanceId, "2.0.0", submitted, adopt);

        result.Status.Should().Be(WorkloadRequirementsStatus.Found);
        result.Read!.Fingerprint.Should().Be(plan.RequirementsFingerprint);
    }

    [Fact]
    public async Task An_update_value_the_resolver_refuses_is_invalid()
    {
        planner.BuildPlanAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyDictionary<string, string?>?>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<Reeve.Workloads.Core.Update.WorkloadUpdatePlanOrigin?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkloadParameterResolutionException("serialPort", "Parameter 'serialPort' is not a device path."));

        var result = await Service().ForUpdateAsync(Guid.NewGuid(), "2.0.0");

        result.Status.Should().Be(WorkloadRequirementsStatus.Invalid);
        result.Message.Should().Contain("serialPort");
    }

    [Fact]
    public async Task An_update_the_planner_has_nothing_to_plan_against_is_not_found()
    {
        planner.BuildPlanAsync(Arg.Any<Guid>(), Arg.Any<string>(), null, null, null, Arg.Any<Reeve.Workloads.Core.Update.WorkloadUpdatePlanOrigin?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("No module instance to plan an update for."));

        var result = await Service().ForUpdateAsync(Guid.NewGuid(), "2.0.0");

        result.Status.Should().Be(WorkloadRequirementsStatus.NotFound);
        result.Message.Should().Contain("No module instance");
    }

    [Fact]
    public async Task An_update_the_host_refused_answers_host_refused_with_its_reason()
    {
        planner.BuildPlanAsync(Arg.Any<Guid>(), Arg.Any<string>(), null, null, null, Arg.Any<Reeve.Workloads.Core.Update.WorkloadUpdatePlanOrigin?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkloadHostRefusedException("the broker is not provisioned"));

        var result = await Service().ForUpdateAsync(Guid.NewGuid(), "2.0.0");

        result.Status.Should().Be(WorkloadRequirementsStatus.HostRefused);
        result.Message.Should().Be("the broker is not provisioned");
    }

    private WorkloadRequirementsService Service() => new(manifests, settings, providers, instances, inputs, planner);

    private static WorkloadInstallRequirementsQuery InstallQuery(Dictionary<string, string?> parameters) => new()
    {
        GroupId = GroupId,
        SourceKey = SourceKey,
        Version = "1.0.0",
        Parameters = parameters,
    };

    private void Publish(DeploymentManifest manifest)
        => manifests.GetAtVersionAsync(SourceKey, GroupId, "1.0.0", Arg.Any<CancellationToken>())
            .Returns(new GroupManifestRead(manifest, Refused: false));

    private WorkloadInstance Deployed(Dictionary<string, string?> parameters)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, DateTimeOffset.UnixEpoch);
        instance.SetResolvedParameters(parameters, DateTimeOffset.UnixEpoch);
        instances.GetByIdAsync(instance.Id, Arg.Any<CancellationToken>()).Returns(instance);
        inputs.GetInputsAsync(instance, Arg.Any<CancellationToken>()).Returns(WorkloadReconcileInputsResult.Declared(new WorkloadReconcileInputs
        {
            WorkloadManifest = Manifest().Workloads[0],
            ResolvedParameters = parameters,
            Settings = new WorkloadOrchestratorSettings(),
            Bootstrap = new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "reeve-wl-secret" },
            EffectiveProvider = KubernetesProvider.Descriptor,
            Namer = DefaultWorkloadResourceNamer.Instance,
        }));
        return instance;
    }

    private static DeploymentManifest Manifest() => new()
    {
        SchemaVersion = 1,
        Workloads =
        [
            new WorkloadManifest
            {
                Id = WorkloadId,
                DisplayName = "Z-Wave controller",
                Image = new WorkloadImage { Ref = "ghcr.io/acme/zwave", Digest = "sha256:" + new string('a', 64) },
                Kind = WorkloadKind.SingleNode,
                Privileges = new WorkloadPrivileges { Devices = [new WorkloadDevice { Parameter = "serialPort" }] },
                Parameters =
                [
                    new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath, Required = true },
                    new WorkloadParameter { Name = "logLevel", Type = WorkloadParameterType.Text, Default = "info" },
                ],
                Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            },
        ],
    };

    private static WorkloadUpdatePlan Plan(
        Guid groupInstanceId,
        Dictionary<string, WorkloadDesiredSpec> previous,
        Dictionary<string, WorkloadDesiredSpec> target) => new()
    {
        GroupInstanceId = groupInstanceId,
        GroupId = GroupId,
        SourceKey = SourceKey,
        FromVersion = "1.0.0",
        ToVersion = "2.0.0",
        PreviousManifest = Manifest(),
        CandidateManifest = Manifest(),
        PreviousRequirements = WorkloadRequirementsFacts.FromSpecs(previous),
        CandidateRequirements = WorkloadRequirementsFacts.FromSpecs(target),
        RequirementsFingerprint = WorkloadRequirementsFacts.Fingerprint(WorkloadRequirementsFacts.FromSpecs(target)),
        PreviousResolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
        PreviousStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
        CandidateResolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
        CandidateStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(),
        Workloads = [],
        TargetSpecs = target,
        PreviousSpecs = previous,
        FromDigests = new Dictionary<string, string?>(),
        ToDigests = new Dictionary<string, string?>(),
    };

    private static WorkloadDesiredSpec MinimalSpec(string workloadId) => new()
    {
        GroupInstanceId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
        GroupId = "acme.metrics",
        WorkloadId = workloadId,
        InstanceId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
        Inst8 = "66666666",
        Provider = Reeve.Workloads.Providers.Manual.ManualProvider.Id,
        ResourceName = $"reeve-{workloadId}-66666666",
        SecretName = $"reeve-{workloadId}-66666666-secret",
        Labels = new Dictionary<string, string>(),
        Image = "registry.example.com/acme/metrics@sha256:0badf00d",
        Kind = WorkloadKind.Service,
        Health = new WorkloadHealth { Mode = WorkloadHealthMode.Http, Http = new WorkloadHttpProbe { Port = 80 } },
        SpecHash = "0000",
    };
}
