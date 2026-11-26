using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Reeve.Workloads.Domain;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Groups;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;
using NSubstitute;

namespace Reeve.Workloads.Core.Tests.Groups;


public sealed class GroupManifestUpdatePlannerTests
{
    private const string GroupId = "acme.zwave";
    private const string WorkloadId = "controller";
    private const string From = "1.0.0";
    private const string To = "1.1.0";
    private static readonly DateTimeOffset At = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid SourceId = new(FakeManifests.SourceKey);

    [Fact]
    public async Task The_plan_carries_the_requirements_of_its_rendered_specs_and_their_fingerprint()
    {
        var plan = await Planner(Manifests()).BuildPlanAsync(InstanceId, To, intentId: null);

        plan.CandidateRequirements.Keys.Should().Equal(WorkloadId);
        plan.CandidateRequirements[WorkloadId].Should().BeEquivalentTo(
            WorkloadPrivilegeRequirements.FromDesiredSpec(plan.TargetSpecs[WorkloadId]));
        plan.PreviousRequirements[WorkloadId].Should().BeEquivalentTo(
            WorkloadPrivilegeRequirements.FromDesiredSpec(plan.PreviousSpecs[WorkloadId]));
        plan.RequirementsFingerprint.Should().Be(WorkloadPrivilegeRequirementsPolicy.Fingerprint(plan.CandidateRequirements));
    }

    [Fact]
    public async Task Module_update_target_hash_for_pinned_reference_matches_the_literal_baseline()
    {
        const string reference = "ref:vault:db#password";
        var manifest = new DeploymentManifest
        {
            SchemaVersion = 1,
            Workloads = [Workload(WorkloadId) with
            {
                Parameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
                Env = [new WorkloadEnv { Name = "DB_PASSWORD", From = "parameter:dbPassword" }],
            }],
        };
        var manifests = Manifests(manifest);
        var pinned = WorkloadInstance.Create(Guid.NewGuid(), InstanceId, GroupId, WorkloadId, At);
        pinned.SetResolvedParameters(new Dictionary<string, string?> { ["dbPassword"] = reference }, At);
        new UnpinnedReferenceRolloutGuard(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider())
            .PinHashToLiteral(pinned, "dbPassword", reference, "same-secret", At);
        var literal = WorkloadInstance.Create(pinned.Id, InstanceId, GroupId, WorkloadId, At);
        var secrets = new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider());
        literal.SetResolvedParameters(
            secrets.Protect(manifest.Workloads[0], new Dictionary<string, string?> { ["dbPassword"] = "same-secret" }), At);

        var pinnedPlan = await Planner(manifests, Instances(pinned), parameterReferenceResolver: new FixedReferenceResolver(), secrets: secrets)
            .BuildPlanAsync(InstanceId, To, intentId: null);
        var literalPlan = await Planner(manifests, Instances(literal), secrets: secrets)
            .BuildPlanAsync(InstanceId, To, intentId: null);

        pinnedPlan.TargetSpecs[WorkloadId].SpecHash.Should().Be(literalPlan.TargetSpecs[WorkloadId].SpecHash,
            "the update target and reconcile must hash the same literal-equivalent input while the exact marker is valid");
        pinnedPlan.PreviousSpecs[WorkloadId].SpecHash.Should().Be(literalPlan.PreviousSpecs[WorkloadId].SpecHash,
            "rollback renders the previous stored binding through the same projection");
    }

    [Fact]
    public async Task A_submitted_device_path_moves_the_candidate_requirements_and_the_fingerprint()
    {
        var instances = OneInstance();
        var unchanged = await Planner(Manifests(DeviceManifest()), instances: instances).BuildPlanAsync(InstanceId, To, intentId: null);
        var edited = await Planner(Manifests(DeviceManifest()), instances: instances).BuildPlanAsync(
            InstanceId, To, intentId: null, Submitted(new() { ["serialPort"] = "/dev/ttyACM1" }));

        edited.CandidateRequirements[WorkloadId].DevicePaths.Should().Contain("/dev/ttyACM1");
        edited.PreviousRequirements[WorkloadId].Should().BeEquivalentTo(unchanged.PreviousRequirements[WorkloadId],
            "the previous side is the running version with the stored values");
        edited.RequirementsFingerprint.Should().NotBe(unchanged.RequirementsFingerprint);
    }

    [Fact]
    public async Task A_workload_the_candidate_adds_has_no_previous_requirements()
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(Manifest(), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(TwoWorkloadManifest(), Refused: false);

        var plan = await Planner(manifests).BuildPlanAsync(InstanceId, To, intentId: null);

        plan.CandidateRequirements.Keys.Should().BeEquivalentTo([WorkloadId, "ui"]);
        plan.PreviousRequirements.Keys.Should().Equal(WorkloadId);
    }

    [Fact]
    public async Task A_workload_the_candidate_adds_renders_the_same_identity_on_every_build_for_one_intent()
    {
        var instances = OneInstance();

        var first = await Planner(AddingManifests(), instances: instances).BuildPlanAsync(InstanceId, To, intentId: IntentId);
        var second = await Planner(AddingManifests(), instances: instances).BuildPlanAsync(InstanceId, To, intentId: IntentId);

        var added = first.Workloads.Single(item => item.WorkloadId == "ui");
        var again = second.Workloads.Single(item => item.WorkloadId == "ui");
        again.Reference.Should().Be(added.Reference);
        again.TargetSpec.SpecHash.Should().Be(added.TargetSpec.SpecHash);
        second.TargetSpecs["ui"].InstanceId.Should().Be(first.TargetSpecs["ui"].InstanceId);
    }

    [Theory]
    [InlineData("35bf992d-c9e9-c616-612e-7696a6cecc1b", "66ac907e-dc00-86fa-be29-241b23600131")]
    [InlineData("cd613e30-d8f1-6adf-91b7-584a2265b1f5", "c49ebb8e-e512-83e0-a2d8-a53beaca0b91")]
    public async Task A_workload_the_candidate_adds_takes_the_id_derived_from_the_intent_and_its_own_id(
        string intentId, string expected)
    {
        var plan = await Planner(AddingManifests()).BuildPlanAsync(InstanceId, To, intentId: new Guid(intentId));

        var derived = plan.TargetSpecs["ui"].InstanceId;
        derived.Should().Be(new Guid(expected));
        derived.Version.Should().Be(8);
        plan.Workloads.Single(item => item.WorkloadId == "ui").Reference.InstanceId.Should().Be(derived);
    }

    [Fact]
    public async Task A_workload_with_a_row_keeps_the_rows_id_in_a_plan_for_an_intent()
    {
        var instances = OneInstance();

        var plan = await Planner(AddingManifests(), instances: instances).BuildPlanAsync(InstanceId, To, intentId: IntentId);

        plan.TargetSpecs[WorkloadId].InstanceId.Should().Be(instances.Instances.Single().Id);
    }

    [Fact]
    public async Task Each_intent_names_each_added_workload_afresh()
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(Manifest(), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(
            new DeploymentManifest { SchemaVersion = 1, Workloads = [Workload(WorkloadId), Workload("ui"), Workload("api")] },
            Refused: false);
        var instances = OneInstance();

        var attempt = await Planner(manifests, instances: instances).BuildPlanAsync(InstanceId, To, intentId: IntentId);
        var retry = await Planner(manifests, instances: instances).BuildPlanAsync(InstanceId, To, intentId: Guid.NewGuid());

        new[]
        {
            attempt.TargetSpecs["ui"].InstanceId, attempt.TargetSpecs["api"].InstanceId,
            retry.TargetSpecs["ui"].InstanceId, retry.TargetSpecs["api"].InstanceId,
        }.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Without_an_intent_two_builds_give_an_added_workload_different_ids()
    {
        var instances = OneInstance();
        GroupManifestUpdatePlanner Concrete() => (GroupManifestUpdatePlanner)Planner(AddingManifests(), instances: instances);

        var first = await Concrete().BuildPlanAsync(InstanceId, To);
        var second = await Concrete().BuildPlanAsync(InstanceId, To);

        second.TargetSpecs["ui"].InstanceId.Should().NotBe(first.TargetSpecs["ui"].InstanceId);
    }

    [Fact]
    public async Task The_source_and_the_from_version_come_off_the_module_row()
    {
        var plan = await Planner(Manifests()).BuildPlanAsync(InstanceId, To, intentId: null);

        plan.SourceKey.Should().Be(FakeManifests.SourceKey);
        plan.FromVersion.Should().Be(From);
        plan.ToVersion.Should().Be(To);
    }

    [Fact]
    public async Task Told_where_the_update_started_the_previous_side_comes_from_there_not_from_the_row()
    {
        var manifests = Manifests();
        IWorkloadUpdatePlanner planner = Planner(manifests, modules: Row(To));

        var plan = await planner.BuildPlanAsync(InstanceId, To, intentId: null, origin: new WorkloadUpdatePlanOrigin(From));

        plan.FromVersion.Should().Be(From);
        manifests.Calls.Should().Contain($"GetAtVersion({FakeManifests.SourceKey}:{GroupId}@{From})");
        AppVersion(plan.Workloads[0].PreviousSpec!).Should().Be(From);
    }

    [Fact]
    public async Task No_module_instance_refuses_the_plan()
    {
        await FluentActions.Awaiting(() => Planner(Manifests(), instances: new FakeInstances()).BuildPlanAsync(InstanceId, To, intentId: null))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task No_module_row_refuses_the_plan()
    {
        var manifests = new FakeManifestsUnderAnyKey(Manifest());

        await FluentActions.Awaiting(() => new GroupManifestUpdatePlanner(
                    manifests,
                    new InMemoryWorkloadGroupRepository(),
                    OneInstance(),
                    new DefaultSettings(),
                    new NoIdentity(),
                    new WorkloadProviderRegistry([]),
                    new WorkloadResourceNamerRegistry([]),
                    new FakeHostBootstrapSource(),
                    Options.Create(new ProductImageOptions()),
                    NullLogger<GroupManifestUpdatePlanner>.Instance,
                    [new RecordingRolloutCertifier(RecordingRolloutCertifier.SessionMode)],
                    new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider()), new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver())
                .BuildPlanAsync(InstanceId, To))
            .Should().ThrowAsync<InvalidOperationException>(
                "a module nobody adopted has no from-version, and guessing one would roll back to the wrong manifest")
            .WithMessage("*no module row*");
    }

    [Fact]
    public async Task Both_versions_are_read_from_the_source_the_row_names()
    {
        var manifests = Manifests();

        await Planner(manifests).BuildPlanAsync(InstanceId, To, intentId: null);

        manifests.Calls.Should().Contain([
            $"GetAtVersion({FakeManifests.SourceKey}:{GroupId}@{To})",
            $"GetAtVersion({FakeManifests.SourceKey}:{GroupId}@{From})",
        ]);
    }

    [Fact]
    public async Task An_unresolvable_candidate_manifest_refuses_the_plan()
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(Manifest(), Refused: false);

        await FluentActions.Awaiting(() => Planner(manifests).BuildPlanAsync(InstanceId, To, intentId: null))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Renders_a_target_and_a_rollback_spec_from_the_two_version_reads()
    {
        var plan = await Planner(Manifests()).BuildPlanAsync(InstanceId, To, intentId: null);

        var item = plan.Workloads.Should().ContainSingle().Subject;
        item.WorkloadId.Should().Be(WorkloadId);
        item.TargetSpec.Should().NotBeNull();
        item.PreviousSpec.Should().NotBeNull("the FROM version declares this workload, so a rollback has a target");
        plan.TargetSpecs.Should().ContainKey(WorkloadId);
        plan.PreviousSpecs.Should().ContainKey(WorkloadId);
    }

    [Fact]
    public async Task Each_side_deploys_the_version_it_is_the_side_of()
    {
        var plan = await Planner(Manifests()).BuildPlanAsync(InstanceId, To, intentId: null);

        AppVersion(plan.Workloads[0].TargetSpec).Should().Be(To);
        AppVersion(plan.Workloads[0].PreviousSpec!).Should().Be(From);
    }

    [Fact]
    public async Task The_target_spec_is_the_one_the_reconcile_pass_would_converge_on()
    {
        var manifests = Manifests();
        var only = Instance(WorkloadId);
        var instances = new FakeInstances();
        instances.Instances.Add(only);

        var plan = await Planner(manifests, instances: instances).BuildPlanAsync(InstanceId, To, intentId: null);

        var installed = new InMemoryWorkloadGroupRepository();
        manifests.Install(installed, GroupId, new InstalledGroupManifestRead
        {
            InstalledVersion = To,
            AtInstalledVersion = Manifest(),
            HighestAvailable = Manifest(),
        });
        var answer = await new GroupManifestReconcileInputsProvider(
                manifests,
                installed,
                new DefaultSettings(),
                new NoIdentity(),
                new WorkloadProviderRegistry([]),
                new WorkloadResourceNamerRegistry([]),
                new FakeHostBootstrapSource(),
                Options.Create(new ProductImageOptions()),
                NullLogger<GroupManifestReconcileInputsProvider>.Instance,
                new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider()),
                new NoWorkloadParameterReferenceResolver(),
                NoBoundInstances())
            .GetInputsAsync(only);
        var inputs = answer.Inputs;

        var reconciled = WorkloadDesiredSpecFactory.Compute(
            inputs!.WorkloadManifest,
            inputs.Settings,
            only.GetResolvedParameters(),
            inputs.Bootstrap,
            inputs.EffectiveProvider,
            DefaultWorkloadResourceNamer.Instance,
            only.GroupInstanceId,
            only.GroupId,
            only.Id,
            inputs.InstallationId,
            inputs.ProductImages);

        plan.Workloads[0].TargetSpec.SpecHash.Should().Be(reconciled.SpecHash,
            "an update whose target differs from what the drift pass renders churns on every in-place update, "
            + "and the two are only equal because ONE function composes the bootstrap for both.");
    }

    private static FakeManifests PerSideManifests()
    {
        var previous = Workload(WorkloadId, WorkloadHealthMode.Http) with
        {
            Extensions = WorkloadExtensions.None,
            Env = [new WorkloadEnv { Name = "BROKER", From = "bootstrap:mqttBrokerUrl" }],
        };
        var candidate = Workload(WorkloadId, WorkloadHealthMode.Container) with
        {
            Extensions = ReeveExtension,
            Env = [new WorkloadEnv { Name = "GATEWAY", From = "bootstrap:gatewayUrl" }],
        };
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(
            new DeploymentManifest { SchemaVersion = 1, Workloads = [previous] }, Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(
            new DeploymentManifest { SchemaVersion = 1, Workloads = [candidate, Workload("ui")] }, Refused: false);
        return manifests;
    }

    [Fact]
    public async Task Each_side_asks_the_host_about_its_own_workload_under_the_bound_providers_secret()
    {
        var host = new FakeHostBootstrapSource();
        var instances = OneInstance();
        var row = instances.Instances[0];
        row.SetProvider(Reeve.Workloads.Providers.Docker.DockerProvider.Id, At);
        var namers = new IWorkloadResourceNamer[]
        {
            new TaggedResourceNamer(Reeve.Workloads.Providers.Docker.DockerProvider.Id, "bound"),
            new TaggedResourceNamer(Reeve.Workloads.Providers.Manual.ManualProvider.Id, "effective"),
        };

        var plan = await Planner(PerSideManifests(), instances: instances, host: host, namers: namers)
            .BuildPlanAsync(InstanceId, To, intentId: IntentId);

        var kept = host.Requests.Where(r => r.WorkloadName == WorkloadId).ToList();
        kept.Should().HaveCount(2, "a workload with a previous side is asked once per side");
        kept.Should().OnlyContain(r => r.InstanceId == row.Id
            && r.Provider == Reeve.Workloads.Providers.Docker.DockerProvider.Id
            && r.Audience == WorkloadBootstrapAudience.Runtime
            && r.SecretName == $"bound-secret-{GroupId}-{WorkloadId}-{row.Inst8}",
            "an update renders a deployed workload for the provider it is bound to, whatever auto-detect answers now");
        plan.TargetSpecs[WorkloadId].Provider.Should().Be(Reeve.Workloads.Providers.Docker.DockerProvider.Id);

        var target = kept.Single(r => r.HealthMode == WorkloadHealthMode.Container);
        target.DeclaredKeys.Should().BeEquivalentTo("gatewayUrl");
        target.Extensions.Should().BeSameAs(ReeveExtension);

        var previous = kept.Single(r => r.HealthMode == WorkloadHealthMode.Http);
        previous.DeclaredKeys.Should().BeEquivalentTo("mqttBrokerUrl");
        previous.Extensions.Should().BeSameAs(WorkloadExtensions.None);

        var addedId = GroupManifestUpdatePlanner.AddedWorkloadInstanceId(IntentId, "ui");
        var added = host.Requests.Where(r => r.WorkloadName == "ui").Should().ContainSingle(
            "a workload the candidate adds has no previous side to ask about").Subject;
        added.InstanceId.Should().Be(addedId);
        added.Provider.Should().Be(Reeve.Workloads.Providers.Docker.DockerProvider.Id);
        added.SecretName.Should().Be($"bound-secret-{GroupId}-ui-{WorkloadInstance.ComputeInst8(addedId)}",
            "a workload the update adds joins the provider its group is bound to, not a passing auto-detect answer");
        plan.TargetSpecs["ui"].Provider.Should().Be(Reeve.Workloads.Providers.Docker.DockerProvider.Id);
        host.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_workload_an_update_adds_to_a_group_with_no_bound_row_takes_the_effective_provider()
    {
        var host = new FakeHostBootstrapSource();
        var namers = new IWorkloadResourceNamer[]
        {
            new TaggedResourceNamer(Reeve.Workloads.Providers.Docker.DockerProvider.Id, "decoy"),
            new TaggedResourceNamer(Reeve.Workloads.Providers.Manual.ManualProvider.Id, "effective"),
        };

        var plan = await Planner(PerSideManifests(), host: host, namers: namers)
            .BuildPlanAsync(InstanceId, To, intentId: IntentId);

        var addedId = GroupManifestUpdatePlanner.AddedWorkloadInstanceId(IntentId, "ui");
        host.Requests.Single(r => r.WorkloadName == "ui").SecretName
            .Should().Be($"effective-secret-{GroupId}-ui-{WorkloadInstance.ComputeInst8(addedId)}");
        plan.TargetSpecs["ui"].Provider.Should().Be(Reeve.Workloads.Providers.Manual.ManualProvider.Id);
    }

    [Fact]
    public async Task An_update_refuses_to_add_a_workload_to_a_group_bound_to_different_providers()
    {
        var instances = OneInstance();
        instances.Instances[0].SetProvider(Reeve.Workloads.Providers.Docker.DockerProvider.Id, At);
        var other = Instance("other");
        other.SetProvider(KubernetesProvider.Id, At);
        instances.Instances.Add(other);

        await FluentActions.Awaiting(() => Planner(PerSideManifests(), instances: instances)
                .BuildPlanAsync(InstanceId, To, intentId: IntentId))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*different providers*");
    }

    [Fact]
    public async Task A_refusal_on_the_previous_side_leaves_no_plan_and_says_the_host_refused()
    {
        var host = new FakeHostBootstrapSource
        {
            Refusal = "the previous side is refused",
            RefuseWhen = request => request.HealthMode == WorkloadHealthMode.Http,
        };

        var build = () => Planner(PerSideManifests(), host: host).BuildPlanAsync(InstanceId, To, intentId: IntentId);

        (await build.Should().ThrowAsync<WorkloadHostRefusedException>())
            .Which.Reason.Should().Be("the previous side is refused");
        host.Requests.Should().Contain(r => r.HealthMode == WorkloadHealthMode.Container,
            "the target side was asked and answered first");
    }

    [Fact]
    public async Task A_refusal_on_the_target_side_leaves_no_plan_and_says_the_host_refused()
    {
        var host = new FakeHostBootstrapSource
        {
            Refusal = "the target side is refused",
            RefuseWhen = request => request.HealthMode == WorkloadHealthMode.Container,
        };

        var build = () => Planner(PerSideManifests(), host: host).BuildPlanAsync(InstanceId, To, intentId: IntentId);

        (await build.Should().ThrowAsync<WorkloadHostRefusedException>())
            .Which.Reason.Should().Be("the target side is refused");
    }

    [Fact]
    public async Task A_missing_candidate_manifest_is_not_taken_for_a_host_refusal()
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(Manifest(), Refused: false);

        var thrown = await FluentActions.Awaiting(() => Planner(manifests).BuildPlanAsync(InstanceId, To, intentId: null))
            .Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().NotBeOfType<WorkloadHostRefusedException>();
    }

    [Fact]
    public async Task An_adopted_install_with_no_previous_version_reads_as_unchanged()
    {
        var plan = await Planner(Manifests(), modules: Row(installedVersion: null)).BuildPlanAsync(InstanceId, To, intentId: null);

        plan.FromVersion.Should().BeNull();
        plan.PreviousUnreadable.Should().BeFalse("absent is nothing to compare, not a declaration refused");
        plan.Workloads[0].PreviousSpec.Should().BeNull("there is no prior version to roll back to");
        plan.PreviousManifest.Workloads.Single().Extensions.ByNamespace.Should().ContainKey("reeve",
            "nothing to compare stands in the candidate WHOLE, so the axes read unchanged rather than parking");
    }

    [Fact]
    public async Task A_REFUSED_previous_manifest_leaves_the_host_axes_undeclared_rather_than_the_candidates()
    {
        var manifests = Manifests();
        manifests.AtVersion[From] = GroupManifestRead.Unreadable;

        var plan = await Planner(manifests).BuildPlanAsync(InstanceId, To, intentId: null);

        plan.PreviousUnreadable.Should().BeTrue();
        plan.PreviousManifest.Workloads.Single().Extensions.ByNamespace.Should().BeEmpty(
            "a refused declaration must not let the candidate's own axes stand in as the FROM side");
        plan.CandidateManifest.Workloads.Single().Extensions.ByNamespace.Should().ContainKey("reeve",
            "the positive control: the candidate side keeps its declaration either way");
    }

    [Fact]
    public async Task A_workload_in_a_mode_the_certifier_requires_is_certified_on_host_evidence()
    {
        var plan = await Planner(Manifests(Manifest(mode: RecordingRolloutCertifier.SessionMode)))
            .BuildPlanAsync(InstanceId, To, intentId: null);

        plan.Workloads[0].Certification.Should().Be(WorkloadHealthGateCertification.HostEvidence);
    }

    [Fact]
    public async Task An_http_health_workload_is_certified_by_provider_readiness()
    {
        var plan = await Planner(Manifests(Manifest(mode: WorkloadHealthMode.Http))).BuildPlanAsync(InstanceId, To, intentId: null);

        plan.Workloads[0].Certification.Should().Be(WorkloadHealthGateCertification.ProviderReady);
    }

    [Fact]
    public async Task A_scan_workload_is_certified_on_the_applied_spec_whatever_it_declares()
    {
        var manifest = Manifest(mode: RecordingRolloutCertifier.SessionMode, kind: WorkloadKind.ScanPerNode);

        var plan = await Planner(Manifests(manifest)).BuildPlanAsync(InstanceId, To, intentId: null);

        plan.Workloads[0].Certification.Should().Be(WorkloadHealthGateCertification.AppliedSpec);
    }

    [Fact]
    public async Task Each_side_is_certified_the_way_its_own_manifest_declares()
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(
            Manifest(mode: WorkloadHealthMode.Http), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(
            Manifest(mode: RecordingRolloutCertifier.SessionMode), Refused: false);

        var plan = await Planner(manifests).BuildPlanAsync(InstanceId, To, intentId: null);

        plan.Workloads[0].Certification.Should().Be(WorkloadHealthGateCertification.HostEvidence);
        plan.Workloads[0].PreviousCertification.Should().Be(WorkloadHealthGateCertification.ProviderReady);
    }

    [Theory]
    [InlineData(WorkloadInstanceState.ExternallyManaged)]
    [InlineData(WorkloadInstanceState.Switching)]
    public async Task Excludes_a_frozen_workload_from_the_plan_while_its_sibling_still_updates(
        WorkloadInstanceState frozenState)
    {
        var instances = new FakeInstances();
        instances.Instances.Add(Freeze(Instance(WorkloadId), frozenState));
        instances.Instances.Add(Instance("ui"));

        var plan = await Planner(Manifests(TwoWorkloadManifest()), instances: instances)
            .BuildPlanAsync(InstanceId, To, intentId: null);

        plan.Workloads.Select(item => item.WorkloadId).Should().Equal(
            ["ui"],
            "the {0} workload is EXCLUDED from the apply plan; only its non-frozen sibling updates",
            frozenState.ToString());
        plan.CandidateManifest.Workloads.Select(workload => workload.Id).Should().BeEquivalentTo([WorkloadId, "ui"],
            "only the per-workload APPLY slice is withheld — the diff still reasons over every declaration");
        plan.CandidateRequirements.Keys.Should().BeEquivalentTo([WorkloadId, "ui"],
            "the host judges every workload the update renders, frozen ones included");
    }

    [Fact]
    public async Task Keeps_a_live_workload_of_a_provider_that_materializes_nothing_in_the_plan()
    {
        var manual = Substitute.For<IWorkloadProvider>();
        var manualId = WorkloadProviderId.Parse("hand");
        manual.Descriptor.Returns(new WorkloadProviderDescriptor { Id = manualId, Materializes = false });
        var live = Freeze(Instance(WorkloadId), WorkloadInstanceState.ExternallyManaged);
        live.SetProvider(manualId, At);

        var plan = await Planner(Manifests(), Instances(live), providers: [manual])
            .BuildPlanAsync(InstanceId, To, intentId: null);

        plan.Workloads.Select(item => item.WorkloadId).Should().Equal(
            [WorkloadId],
            "a connected manual workload is observed, not disowned: the operator-redeploy park, the gate and "
            + "commit-params still need it in the plan");
    }

    [Fact]
    public async Task A_version_declaring_again_a_workload_still_tearing_down_is_refused_with_the_cause()
    {
        var instances = new FakeInstances();
        var tearingDown = Instance("ui");
        tearingDown.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, "Teardown failed: boom", At);
        instances.Instances.AddRange([Instance(WorkloadId), tearingDown]);

        var build = () => Planner(Manifests(TwoWorkloadManifest()), instances: instances)
            .BuildPlanAsync(InstanceId, To, intentId: null);

        (await build.Should().ThrowAsync<InvalidOperationException>(
                "no row would be created for it and the update would apply onto a row being torn down"))
            .Which.Message.Should().Contain("'ui'").And.Contain("still being torn down").And.Contain("boom");
    }

    [Fact]
    public async Task A_group_being_uninstalled_is_not_refused_as_a_redeclared_workload()
    {
        var instances = new FakeInstances();
        foreach (var row in new[] { Instance(WorkloadId), Instance("ui") })
        {
            row.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, At);
            instances.Instances.Add(row);
        }

        var build = () => Planner(Manifests(TwoWorkloadManifest()), instances: instances)
            .BuildPlanAsync(InstanceId, To, intentId: null);

        await build.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Yields_an_empty_plan_when_every_workload_is_frozen()
    {
        var instances = new FakeInstances();
        instances.Instances.Add(Freeze(Instance(WorkloadId), WorkloadInstanceState.ExternallyManaged));
        instances.Instances.Add(Freeze(Instance("ui"), WorkloadInstanceState.Switching));

        var plan = await Planner(Manifests(TwoWorkloadManifest()), instances: instances)
            .BuildPlanAsync(InstanceId, To, intentId: null);

        plan.Workloads.Should().BeEmpty("every workload is frozen — the orchestrator drives no provider op");
        plan.ToVersion.Should().Be(To, "the plan still describes the version bump so the module update proceeds");
    }

    [Fact]
    public async Task Submitted_parameters_drive_the_candidate_side_while_previous_keeps_the_stored_value()
    {
        var plan = await Planner(Manifests(ParameterManifest())).BuildPlanAsync(
            InstanceId, To, intentId: null, Submitted(new() { ["serialPort"] = "/dev/ttyACM1" }));

        plan.CandidateResolvedParameters[WorkloadId]["serialPort"].Should().Be("/dev/ttyACM1");
        plan.PreviousResolvedParameters[WorkloadId]["serialPort"].Should().Be("/dev/ttyUSB0");
    }

    [Fact]
    public async Task Submitted_parameters_change_the_rendered_target_spec_and_not_the_rollback_one()
    {
        var instances = OneInstance();
        var unchanged = await Planner(Manifests(ParameterManifest()), instances: instances)
            .BuildPlanAsync(InstanceId, To, intentId: null);
        var edited = await Planner(Manifests(ParameterManifest()), instances: instances).BuildPlanAsync(
            InstanceId, To, intentId: null, Submitted(new() { ["serialPort"] = "/dev/ttyACM1" }));

        edited.Workloads[0].TargetSpec.SpecHash.Should().NotBe(unchanged.Workloads[0].TargetSpec.SpecHash,
            "the submitted value must reach the spec the provider applies, not just the map that commits");
        edited.Workloads[0].PreviousSpec!.SpecHash.Should().Be(unchanged.Workloads[0].PreviousSpec!.SpecHash,
            "the rollback target is the OLD version with the OLD values — an edit must not rewrite it");
    }

    [Fact]
    public async Task Without_submitted_parameters_the_candidate_side_equals_the_stored_one()
    {
        var plan = await Planner(Manifests(ParameterManifest())).BuildPlanAsync(InstanceId, To, intentId: null);

        plan.CandidateResolvedParameters[WorkloadId].Should()
            .BeEquivalentTo(plan.PreviousResolvedParameters[WorkloadId]);
    }

    [Fact]
    public async Task A_masked_submitted_value_retains_the_stored_one()
    {
        var plan = await Planner(Manifests(ParameterManifest())).BuildPlanAsync(
            InstanceId, To, intentId: null, Submitted(new() { ["serialPort"] = WorkloadParameterMaskPolicy.MaskSentinel }));

        plan.CandidateResolvedParameters[WorkloadId]["serialPort"].Should().Be("/dev/ttyUSB0");
    }

    [Fact]
    public async Task A_submitted_restore_binding_that_differs_from_the_stored_one_is_logged_as_ignored()
    {
        var workload = Workload(WorkloadId) with
        {
            Volumes = [new WorkloadVolume
            {
                Id = "data", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Recoverable,
            }],
        };
        var logger = new Reeve.Workloads.Core.Tests.Hosting.RecordingLogger<GroupManifestUpdatePlanner>();

        var plan = await Planner(
                Manifests(new DeploymentManifest { SchemaVersion = 1, Workloads = [workload] }), logger: logger)
            .BuildPlanAsync(InstanceId, To, intentId: null, Submitted(new() { ["existingClaim.data"] = "other-volume" }));

        plan.CandidateResolvedParameters[WorkloadId]["existingClaim.data"].Should().BeNull();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Text.Contains("existingClaim.data"));
    }

    [Fact]
    public async Task A_submitted_blank_over_a_stored_restore_binding_is_logged_as_ignored()
    {
        var workload = Workload(WorkloadId) with
        {
            Volumes = [new WorkloadVolume
            {
                Id = "data", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Recoverable,
            }],
        };
        var row = WorkloadInstance.Create(Guid.NewGuid(), InstanceId, GroupId, WorkloadId, At);
        row.SetResolvedParameters(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["existingClaim.data"] = "restored-claim" }, At);
        var logger = new Reeve.Workloads.Core.Tests.Hosting.RecordingLogger<GroupManifestUpdatePlanner>();

        var plan = await Planner(
                Manifests(new DeploymentManifest { SchemaVersion = 1, Workloads = [workload] }),
                instances: Instances(row), logger: logger)
            .BuildPlanAsync(InstanceId, To, intentId: null, Submitted(new() { ["existingClaim.data"] = "" }));

        plan.CandidateResolvedParameters[WorkloadId]["existingClaim.data"].Should().Be("restored-claim");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Text.Contains("existingClaim.data"),
            "an attempted unbind is ignored as much as an attempted rebind");
    }

    [Fact]
    public void Merge_keeps_the_stored_restore_binding_over_a_submitted_one()
    {
        var workload = new WorkloadManifest
        {
            Id = WorkloadId,
            DisplayName = WorkloadId,
            Image = new WorkloadImage { Component = "controller" },
            Kind = WorkloadKind.Service,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Volumes =
            [
                new WorkloadVolume
                {
                    Id = "data", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Recoverable,
                },
                new WorkloadVolume
                {
                    Id = "cache", MountPath = "/cache", Size = "1Gi", Criticality = VolumeCriticality.Recoverable,
                },
            ],
        };
        var stored = new Dictionary<string, string?> { ["existingClaim.data"] = "old-claim" };
        var submitted = new Dictionary<string, string?>
        {
            ["existingClaim.data"] = "another-plugins-volume",
            ["existingClaim.cache"] = "reeve_postgres-data",
        };

        var merged = WorkloadUpdateCandidateParameters.Merge(workload, stored, submitted, ambiguousKeys: null);

        merged["existingClaim.data"].Should().Be("old-claim",
            "a restore binding is install-time only, where it passes the retained-volume ownership check; an update never rebinds it");
        merged.Should().NotContainKey("existingClaim.cache");
    }

    [Fact]
    public void Merge_drops_an_ambiguous_existing_claim_key()
    {
        var workload = new WorkloadManifest
        {
            Id = WorkloadId,
            DisplayName = WorkloadId,
            Image = new WorkloadImage { Component = "controller" },
            Kind = WorkloadKind.Service,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Volumes = [new WorkloadVolume
            {
                Id = "data", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Recoverable,
            }],
        };
        var stored = new Dictionary<string, string?> { ["existingClaim.data"] = "old-claim" };
        var submitted = new Dictionary<string, string?> { ["existingClaim.data"] = "new-claim" };

        var merged = WorkloadUpdateCandidateParameters.Merge(
            workload, stored, submitted, new HashSet<string>(StringComparer.Ordinal) { "existingClaim.data" });

        merged.Should().BeSameAs(stored,
            "the install/update routing contract discards an ambiguous existingClaim key before parameter resolution");
    }

    [Fact]
    public async Task An_update_commits_the_operators_values_and_never_an_author_default()
    {
        var plan = await Planner(Manifests(ConfigManifest(withDefault: "app-config", mounted: false)))
            .BuildPlanAsync(InstanceId, To, intentId: null, Submitted(new() { ["serialPort"] = "/dev/ttyACM1" }));

        plan.CandidateResolvedParameters[WorkloadId]["cfg"].Should().Be("app-config", "the candidate deploys with it");
        plan.CandidateStoredParameters[WorkloadId].Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyACM1",
            ["logLevel"] = "info",
        }, "only the stored and the submitted values are the operator's; the default stays the author's");
    }

    [Fact]
    public async Task A_password_the_candidate_types_as_text_commits_its_value_not_the_stored_ciphertext()
    {
        var secrets = new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider());
        DeploymentManifest Typed(WorkloadParameterType type) => new()
        {
            SchemaVersion = 1,
            Workloads = [Workload(WorkloadId) with
            {
                Parameters = [new WorkloadParameter { Name = "token", Type = type }],
                Env = [new WorkloadEnv { Name = "TOKEN", From = "parameter:token" }],
            }],
        };
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(Typed(WorkloadParameterType.Password), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(Typed(WorkloadParameterType.Text), Refused: false);
        var row = WorkloadInstance.Create(Guid.NewGuid(), InstanceId, GroupId, WorkloadId, At);
        var stored = secrets.Protect(Typed(WorkloadParameterType.Password).Workloads[0],
            new Dictionary<string, string?> { ["token"] = "s3cret" });
        stored["token"].Should().StartWith(WorkloadParameterSecretCodec.Prefix, "the test must start from ciphertext");
        row.SetResolvedParameters(stored, At);

        var plan = await Planner(manifests, Instances(row), secrets: secrets).BuildPlanAsync(InstanceId, To, intentId: null);

        plan.CandidateStoredParameters[WorkloadId]["token"].Should().Be("s3cret",
            "a text parameter is never decrypted again, so committing the ciphertext would hand it to the workload");
    }

    [Fact]
    public async Task A_default_committed_by_one_update_does_not_feed_a_mount_the_next_version_binds_to_it()
    {
        var first = await Planner(Manifests(ConfigManifest(withDefault: "app-config", mounted: false)))
            .BuildPlanAsync(InstanceId, To, intentId: null);
        var row = Instance(WorkloadId);
        row.SetResolvedParameters(first.CandidateStoredParameters[WorkloadId], At);
        var manifests = new FakeManifests();
        manifests.AtVersion[To] = new GroupManifestRead(ConfigManifest(withDefault: "app-config", mounted: false), Refused: false);
        manifests.AtVersion["1.2.0"] = new GroupManifestRead(ConfigManifest(withDefault: null, mounted: true), Refused: false);

        var next = await Planner(manifests, Instances(row), modules: Row(To))
            .BuildPlanAsync(InstanceId, "1.2.0", intentId: null);

        next.Workloads.Single().TargetSpec.ConfigMapMounts.Should().BeEmpty(
            "version N's default for 'cfg' was never the operator's, so N+1 mounting 'cfg' gets no ConfigMap until "
            + "the operator names one");
    }

    [Fact]
    public async Task A_blank_the_operator_left_over_an_author_default_does_not_publish_a_host_port_a_later_version_binds()
    {
        var docker = Substitute.For<IWorkloadProvider>();
        docker.Descriptor.Returns(Reeve.Workloads.Providers.Docker.DockerProvider.Descriptor);
        DeploymentManifest Version(bool publishes) => new()
        {
            SchemaVersion = 1,
            Workloads = [Workload(WorkloadId) with
            {
                Network = publishes
                    ? new WorkloadNetwork { Ports = [new WorkloadPort { Name = "api", Port = 80, Protocol = "TCP" }] }
                    : null,
                Parameters = [new WorkloadParameter
                {
                    Name = "port.api.hostPort", Type = WorkloadParameterType.Integer, Default = publishes ? null : "8080",
                }],
            }],
        };
        var row = Instance(WorkloadId);
        row.SetProvider(Reeve.Workloads.Providers.Docker.DockerProvider.Id, At);
        row.SetResolvedParameters(new Dictionary<string, string?> { ["port.api.hostPort"] = "" }, At);
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(Version(publishes: false), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(Version(publishes: false), Refused: false);
        manifests.AtVersion["1.2.0"] = new GroupManifestRead(Version(publishes: true), Refused: false);

        var first = await Planner(manifests, Instances(row), providers: [docker])
            .BuildPlanAsync(InstanceId, To, intentId: null);
        first.CandidateResolvedParameters[WorkloadId]["port.api.hostPort"].Should().Be("8080",
            "the resolver swaps the blank for the author default, which is why the commit must not read the resolver");
        first.CandidateStoredParameters[WorkloadId]["port.api.hostPort"].Should().BeEmpty("the operator's blank stays blank");
        row.SetResolvedParameters(first.CandidateStoredParameters[WorkloadId], At);
        var next = await Planner(manifests, Instances(row), modules: Row(To), providers: [docker])
            .BuildPlanAsync(InstanceId, "1.2.0", intentId: null);

        next.Workloads.Single().TargetSpec.Network!.Ports.Single().HostPort.Should().BeNull(
            "version N's 8080 was never the operator's, so N+1 publishes no host port until the operator names one");
    }

    [Fact]
    public async Task A_text_parameter_the_candidate_types_as_a_password_commits_the_operators_value_for_protection()
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(TokenManifest(WorkloadParameterType.Text), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(TokenManifest(WorkloadParameterType.Password), Refused: false);
        var row = WorkloadInstance.Create(Guid.NewGuid(), InstanceId, GroupId, WorkloadId, At);
        row.SetResolvedParameters(new Dictionary<string, string?> { ["token"] = "s3cret" }, At);

        var plan = await Planner(manifests, Instances(row)).BuildPlanAsync(InstanceId, To, intentId: null);

        plan.CandidateStoredParameters[WorkloadId]["token"].Should().Be("s3cret",
            "commit-params protects it under the candidate's schema, which now types it as a password");
    }

    [Fact]
    public async Task A_password_reference_is_committed_as_the_reference()
    {
        const string reference = "ref:vault:db#password";
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(TokenManifest(WorkloadParameterType.Password), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(TokenManifest(WorkloadParameterType.Password), Refused: false);
        var row = WorkloadInstance.Create(Guid.NewGuid(), InstanceId, GroupId, WorkloadId, At);
        row.SetResolvedParameters(new Dictionary<string, string?> { ["token"] = reference }, At);

        var plan = await Planner(manifests, Instances(row), parameterReferenceResolver: new FixedReferenceResolver())
            .BuildPlanAsync(InstanceId, To, intentId: null);

        plan.CandidateResolvedParameters[WorkloadId]["token"].Should().Be("same-secret");
        plan.CandidateStoredParameters[WorkloadId]["token"].Should().Be(reference, "the secret store's answer is never stored");
    }

    private static DeploymentManifest TokenManifest(WorkloadParameterType type) => new()
    {
        SchemaVersion = 1,
        Workloads = [Workload(WorkloadId) with
        {
            Parameters = [new WorkloadParameter { Name = "token", Type = type }],
            Env = [new WorkloadEnv { Name = "TOKEN", From = "parameter:token" }],
        }],
    };

    [Fact]
    public async Task Submitted_parameters_merge_over_the_stored_ones_rather_than_replacing_them()
    {
        var plan = await Planner(Manifests(ParameterManifest())).BuildPlanAsync(
            InstanceId, To, intentId: null, Submitted(new() { ["serialPort"] = "/dev/ttyACM1" }));

        plan.CandidateResolvedParameters[WorkloadId]["serialPort"].Should().Be("/dev/ttyACM1");
        plan.CandidateResolvedParameters[WorkloadId]["logLevel"].Should().Be("info",
            "a value the submitted map omits must survive — the wizard never renders every stored parameter");
    }

    [Fact]
    public async Task An_undeclared_submitted_name_is_dropped()
    {
        var plan = await Planner(Manifests(ParameterManifest())).BuildPlanAsync(
            InstanceId, To, intentId: null, Submitted(new() { ["someOtherWorkloadParam"] = "nope" }));

        plan.CandidateResolvedParameters[WorkloadId].Should().NotContainKey("someOtherWorkloadParam");
    }

    [Fact]
    public async Task Without_adoption_the_operators_resource_override_survives_the_update()
    {
        var plan = await Planner(ResourceManifests(fromCpu: "100m", toCpu: "250m"), instances: WithOverrides())
            .BuildPlanAsync(InstanceId, To, intentId: null);

        plan.CandidateResolvedParameters[WorkloadId]["resources.limits.cpu"].Should().Be("50m",
            "an untouched update applies nothing on its own");
        plan.Workloads[0].TargetSpec.Resources!.LimitsCpu.Should().Be("50m");
    }

    [Fact]
    public async Task Adoption_clears_only_the_axis_whose_declaration_moved()
    {
        var plan = await Planner(ResourceManifests(fromCpu: "100m", toCpu: "250m"), instances: WithOverrides())
            .BuildPlanAsync(InstanceId, To, intentId: null, adoptDeclaredResourceWorkloadIds: [WorkloadId]);

        plan.CandidateResolvedParameters[WorkloadId]["resources.limits.cpu"].Should().BeNull(
            "the operator's override was given up");
        plan.CandidateResolvedParameters[WorkloadId]["resources.limits.memory"].Should().Be("128Mi",
            "this axis never moved, so it is not adoptable");

        var resources = plan.Workloads[0].TargetSpec.Resources!;
        resources.LimitsCpu.Should().Be("250m", "the candidate's declaration now drives the axis");
        resources.LimitsMemory.Should().Be("128Mi", "the untouched override still wins over the declaration");
    }

    [Fact]
    public async Task An_id_sent_for_a_workload_whose_declaration_did_not_move_clears_nothing()
    {
        var plan = await Planner(ResourceManifests(fromCpu: "100m", toCpu: "100m"), instances: WithOverrides())
            .BuildPlanAsync(InstanceId, To, intentId: null, adoptDeclaredResourceWorkloadIds: [WorkloadId]);

        plan.CandidateResolvedParameters[WorkloadId]["resources.limits.cpu"].Should().Be("50m");
    }

    [Fact]
    public async Task Adoption_leaves_the_rollback_snapshot_and_spec_holding_the_operators_own_value()
    {
        var plan = await Planner(ResourceManifests(fromCpu: "100m", toCpu: "250m"), instances: WithOverrides())
            .BuildPlanAsync(InstanceId, To, intentId: null, adoptDeclaredResourceWorkloadIds: [WorkloadId]);

        plan.PreviousResolvedParameters[WorkloadId]["resources.limits.cpu"].Should().Be("50m",
            "a rollback must put the operator back exactly where they were before the update");
        plan.Workloads[0].PreviousSpec!.Resources!.LimitsCpu.Should().Be("50m",
            "the parameter rows are only half of a rollback — this is the spec the orchestrator re-applies");
    }

    [Fact]
    public async Task The_rollback_target_clears_stored_operator_networks_exactly_as_the_normal_render_does()
    {
        const string modeKey = Reeve.Workloads.Domain.Network.WorkloadNetworkAttachmentGrammar.NetworkModeParameter;
        const string attachmentsKey = Reeve.Workloads.Domain.Network.WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter;
        var manifest = new DeploymentManifest
        {
            SchemaVersion = 1,
            Workloads = [Workload(WorkloadId) with { Network = new WorkloadNetwork { HostNetwork = true } }],
        };
        static FakeInstances WithStored(Dictionary<string, string?> stored)
        {
            var instance = WorkloadInstance.Create(new Guid("aaaaaaaa-0000-0000-0000-000000000001"), InstanceId, GroupId, WorkloadId, At);
            instance.SetResolvedParameters(stored, At);
            var instances = new FakeInstances();
            instances.Instances.Add(instance);
            return instances;
        }

        var stale = await Planner(Manifests(manifest), instances: WithStored(new() { [modeKey] = "cluster", [attachmentsKey] = "lan-a" }))
            .BuildPlanAsync(InstanceId, To, intentId: null);
        var clean = await Planner(Manifests(manifest), instances: WithStored(new() { [modeKey] = "cluster" }))
            .BuildPlanAsync(InstanceId, To, intentId: null);

        stale.PreviousSpecs[WorkloadId].Network!.Attachments.Should().BeEmpty();
        stale.PreviousSpecs[WorkloadId].SpecHash.Should().Be(clean.PreviousSpecs[WorkloadId].SpecHash);
        stale.PreviousSpecs[WorkloadId].SpecHash.Should().Be(stale.TargetSpecs[WorkloadId].SpecHash);
    }

    [Fact]
    public async Task The_rollback_target_enforces_the_path_policy_of_the_previous_manifest()
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(DeviceManifest(), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(Manifest(), Refused: false);
        var instance = WorkloadInstance.Create(Guid.NewGuid(), InstanceId, GroupId, WorkloadId, At);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["serialPort"] = "/etc/shadow" }, At);
        var instances = new FakeInstances();
        instances.Instances.Add(instance);

        var plan = () => Planner(manifests, instances: instances).BuildPlanAsync(InstanceId, To, intentId: null);

        await plan.Should().ThrowAsync<WorkloadParameterResolutionException>();
    }

    private static readonly Guid InstanceId = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid IntentId = new("99999999-8888-7777-6666-555555555555");

    private static FakeManifests AddingManifests()
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(Manifest(), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(TwoWorkloadManifest(), Refused: false);
        return manifests;
    }

    private static IWorkloadUpdatePlanner Planner(
        FakeManifests manifests,
        FakeInstances? instances = null,
        InMemoryWorkloadGroupRepository? modules = null,
        FakeHostBootstrapSource? host = null,
        IReadOnlyList<IWorkloadResourceNamer>? namers = null,
        IWorkloadParameterReferenceResolver? parameterReferenceResolver = null,
        ILogger<GroupManifestUpdatePlanner>? logger = null,
        IReadOnlyList<IWorkloadProvider>? providers = null,
        WorkloadParameterSecretCodec? secrets = null)
    {
        return new GroupManifestUpdatePlanner(
            manifests,
            modules ?? Row(From),
            instances ?? OneInstance(),
            new DefaultSettings(),
            new NoIdentity(),
            new WorkloadProviderRegistry(providers ?? []),
            new WorkloadResourceNamerRegistry(namers ?? []),
            host ?? new FakeHostBootstrapSource(),
            Options.Create(new ProductImageOptions()),
            logger ?? NullLogger<GroupManifestUpdatePlanner>.Instance,
            [new RecordingRolloutCertifier(RecordingRolloutCertifier.SessionMode)],
            secrets ?? new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider()),
            parameterReferenceResolver ?? new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver());
    }

    private static InMemoryWorkloadGroupRepository Row(string? installedVersion)
    {
        var modules = new InMemoryWorkloadGroupRepository();
        modules.Seed(GroupId, FakeManifests.SourceKey, installedVersion);
        return modules;
    }

    private sealed class FakeManifestsUnderAnyKey(DeploymentManifest manifest) : IWorkloadGroupManifestSource
    {
        public Task<AvailableGroupManifestRead> GetAvailableAsync(
            string groupId, CancellationToken cancellationToken = default)
            => Task.FromResult(AvailableGroupManifestRead.Nothing);

        public Task<GroupManifestRead> GetAtVersionAsync(
            string sourceKey, string groupId, string version, CancellationToken cancellationToken = default)
            => Task.FromResult(new GroupManifestRead(manifest, Refused: false));
    }

    private static FakeInstances WithOverrides()
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), InstanceId, GroupId, WorkloadId, At);
        instance.SetResolvedParameters(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["resources.limits.cpu"] = "50m",
                ["resources.limits.memory"] = "128Mi",
            },
            At);
        var instances = new FakeInstances();
        instances.Instances.Add(instance);
        return instances;
    }

    private static FakeInstances OneInstance()
    {
        var instances = new FakeInstances();
        instances.Instances.Add(Instance(WorkloadId));
        return instances;
    }

    private static FakeInstances Instances(WorkloadInstance instance)
    {
        var instances = new FakeInstances();
        instances.Instances.Add(instance);
        return instances;
    }

    private sealed class FixedReferenceResolver : IWorkloadParameterReferenceResolver
    {
        public ValueTask<WorkloadReferenceResolution> ResolveAsync(string reference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<WorkloadReferenceResolution>(new WorkloadReferenceResolution.Success("same-secret"));
    }

    private static IReadOnlyDictionary<string, string?> Submitted(Dictionary<string, string?> values) => values;

    private static string? AppVersion(WorkloadDesiredSpec spec)
        => spec.Env.FirstOrDefault(env => env.Name == WorkloadDesiredSpecFactory.AppVersionEnv)?.Value;

    private static WorkloadInstance Instance(string workloadId)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), InstanceId, GroupId, workloadId, At);
        instance.SetResolvedParameters(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["serialPort"] = "/dev/ttyUSB0",
                ["logLevel"] = "info",
            },
            At);
        return instance;
    }

    private static WorkloadInstance Freeze(WorkloadInstance instance, WorkloadInstanceState frozen)
    {
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, At);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, At);
        if (frozen == WorkloadInstanceState.Switching)
        {
            instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, At);
        }
        instance.TransitionTo(frozen, WorkloadStateReason.HealthObserved, null, At);
        return instance;
    }

    private static FakeManifests Manifests(DeploymentManifest? manifest = null)
    {
        manifest ??= Manifest();
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(manifest, Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(manifest, Refused: false);
        return manifests;
    }

    private static FakeManifests ResourceManifests(string fromCpu, string toCpu)
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(ResourceManifest(fromCpu), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(ResourceManifest(toCpu), Refused: false);
        return manifests;
    }

    private static readonly WorkloadExtensions ReeveExtension = new()
    {
        ByNamespace = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["reeve"] = JsonDocument
                .Parse("""{"protocol":{"version":"1","minSupported":"1"}}""").RootElement.Clone(),
        },
    };

    private static DeploymentManifest Manifest(
        WorkloadHealthMode? mode = null, WorkloadKind kind = WorkloadKind.Service)
        => new()
        {
            SchemaVersion = 1,
            Workloads = [Workload(WorkloadId, mode, kind)],
        };

    private static DeploymentManifest TwoWorkloadManifest() => new()
    {
        SchemaVersion = 1,
        Workloads = [Workload(WorkloadId), Workload("ui")],
    };

    private static DeploymentManifest ParameterManifest() => new()
    {
        SchemaVersion = 1,
        Workloads =
        [
            Workload(WorkloadId) with
            {
                Parameters =
                [
                    new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.Text },
                    new WorkloadParameter { Name = "logLevel", Type = WorkloadParameterType.Text },
                ],
                Env =
                [
                    new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" },
                    new WorkloadEnv { Name = "SERIAL_PORT", From = "parameter:serialPort" },
                    new WorkloadEnv { Name = "LOG_LEVEL", From = "parameter:logLevel" },
                ],
            },
        ],
    };

    private static DeploymentManifest ConfigManifest(string? withDefault, bool mounted)
    {
        var workload = ParameterManifest().Workloads[0];
        return new DeploymentManifest
        {
            SchemaVersion = 1,
            Workloads =
            [
                workload with
                {
                    Parameters =
                    [
                        .. workload.Parameters,
                        new WorkloadParameter { Name = "cfg", Type = WorkloadParameterType.Text, Default = withDefault },
                    ],
                    ConfigMapMounts = mounted
                        ? [new ConfigMapMount { MountPath = "/etc/app", Parameter = "cfg" }]
                        : [],
                },
            ],
        };
    }

    private static DeploymentManifest DeviceManifest() => new()
    {
        SchemaVersion = 1,
        Workloads =
        [
            Workload(WorkloadId) with
            {
                Privileges = new WorkloadPrivileges { Devices = [new WorkloadDevice { Parameter = "serialPort" }] },
                Parameters = [new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath }],
            },
        ],
    };

    private static DeploymentManifest ResourceManifest(string limitsCpu) => new()
    {
        SchemaVersion = 1,
        Workloads =
        [
            Workload(WorkloadId) with
            {
                Resources = new WorkloadResources
                {
                    Limits = new ResourceQuantities { Cpu = limitsCpu, Memory = "256Mi" },
                },
            },
        ],
    };

    private static WorkloadManifest Workload(
        string workloadId, WorkloadHealthMode? mode = null, WorkloadKind kind = WorkloadKind.Service)
        => new()
        {
            Id = workloadId,
            DisplayName = workloadId,
            Image = new WorkloadImage { Ref = "registry.example/zwave", Digest = new string('a', 64) },
            Kind = kind,
            Health = new WorkloadHealth { Mode = mode ?? WorkloadHealthMode.Container },
            Extensions = ReeveExtension,
            Env =
            [
                new WorkloadEnv { Name = "REEVE_GATEWAY_URL", From = "bootstrap:gatewayUrl" },
                new WorkloadEnv { Name = "REEVE_WORKLOAD_TOKEN", From = "bootstrap:workloadToken" },
            ],
        };

    private static IWorkloadInstanceRepository NoBoundInstances()
    {
        var instances = Substitute.For<IWorkloadInstanceRepository>();
        instances.ListByPluginInstanceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkloadInstance>>([]));
        return instances;
    }
}
