using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.Network;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadDeploymentDiffTests
{
    private static WorkloadManifest Workload(
        string id = "w",
        string digest = "sha256:" + "",
        WorkloadKind kind = WorkloadKind.SingleNode,
        WorkloadNetwork? network = null,
        WorkloadPrivileges? privileges = null,
        IReadOnlyList<WorkloadVolume>? volumes = null,
        UnsupportedPodPatch? patch = null,
        WorkloadExtensions? extensions = null,
        IReadOnlyList<WorkloadParameter>? parameters = null,
        WorkloadResources? resources = null)
        => new()
        {
            Id = id,
            DisplayName = id.ToUpperInvariant(),
            Image = new WorkloadImage
            {
                Ref = "reg/img",
                Digest = string.IsNullOrEmpty(digest) ? "sha256:" + new string('a', 64) : digest,
            },
            Kind = kind,
            Network = network,
            Privileges = privileges,
            Volumes = volumes ?? Array.Empty<WorkloadVolume>(),
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Extensions = extensions ?? WorkloadExtensions.None,
            Parameters = parameters ?? Array.Empty<WorkloadParameter>(),
            Resources = resources,
            UnsupportedPodPatch = patch,
        };

    private static DeploymentManifest Manifest(params WorkloadManifest[] workloads)
        => new() { SchemaVersion = 1, Workloads = workloads };

    private static string Digest(char c) => "sha256:" + new string(c, 64);

    private static UnsupportedPodPatch PodPatch(string rawJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
        return new UnsupportedPodPatch { RawJson = rawJson, Body = doc.RootElement.Clone() };
    }

    private static WorkloadParameter Param(string name, bool required, string? @default = null)
        => new() { Name = name, Type = WorkloadParameterType.Text, Required = required, Default = @default };

    private static IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> Profiles(
        params (string Id, WorkloadPrivilegeRequirements Profile)[] entries)
        => entries.ToDictionary(e => e.Id, e => e.Profile, StringComparer.Ordinal);

    private static WorkloadPrivilegeRequirements ProfileOf(WorkloadManifest w)
        => WorkloadPrivilegeRequirements.FromWorkload(w, new Dictionary<string, string>());

    private static IReadOnlyDictionary<string, WorkloadPrivilegeRequirements> ProfilesOf(DeploymentManifest m)
        => m.Workloads.ToDictionary(w => w.Id, ProfileOf, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> NoResolvedValues()
        => new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);

    private static WorkloadDeploymentDiff Compute(
        DeploymentManifest previous,
        DeploymentManifest candidate,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>? previousResolvedParameters = null,
        IReadOnlyDictionary<string, WorkloadPrivilegeRequirements>? previousConsented = null,
        IReadOnlyDictionary<string, WorkloadPrivilegeRequirements>? candidateProfiles = null)
        => WorkloadDeploymentDiff.Compute(
            previous,
            previousConsented ?? ProfilesOf(previous),
            previousResolvedParameters ?? NoResolvedValues(),
            candidate,
            candidateProfiles ?? ProfilesOf(candidate));

    [Fact]
    public void Identical_deployment_has_no_changes()
    {
        var w = Workload(digest: Digest('a'));
        var m = Manifest(w);

        var diff = Compute(m, m);

        diff.Workloads.Should().ContainSingle();
        var wd = diff.Workloads[0];
        wd.WorkloadId.Should().Be("w");
        wd.DigestChanged.Should().BeFalse();
        wd.RequirementsDiff.Security.Should().Be(RequirementChangeKind.Unchanged);
        wd.NewRequiredParameters.Should().BeEmpty();

        diff.AnySecurityEscalation.Should().BeFalse();
        diff.AnyNewRequiredParameters.Should().BeFalse();
        diff.AddedWorkloadIds.Should().BeEmpty();
        diff.RemovedWorkloadIds.Should().BeEmpty();
        diff.VolumeNotes.Should().BeEmpty();
    }

    [Fact]
    public void Digest_only_change_is_reported_from_and_to()
    {
        var prev = Manifest(Workload(digest: Digest('a')));
        var cand = Manifest(Workload(digest: Digest('b')));

        var diff = Compute(prev, cand);

        var wd = diff.Workloads[0];
        wd.DigestChanged.Should().BeTrue();
        wd.FromDigest.Should().Be(Digest('a'));
        wd.ToDigest.Should().Be(Digest('b'));
        wd.RequirementsDiff.Security.Should().Be(RequirementChangeKind.Unchanged);
        wd.NewRequiredParameters.Should().BeEmpty();
    }

    [Fact]
    public void Security_escalation_surfaces_via_profile_diff()
    {
        var prev = Manifest(Workload());
        var candWorkload = Workload(privileges: new WorkloadPrivileges { Privileged = true });
        var cand = Manifest(candWorkload);

        var diff = Compute(prev, cand);

        var wd = diff.Workloads[0];
        wd.RequirementsDiff.Security.Should().Be(RequirementChangeKind.Escalation);
        wd.RequirementsDiff.EscalatedFields.Should().ContainSingle().Which.Should().Contain("Privileged");
        diff.AnySecurityEscalation.Should().BeTrue();
    }

    [Fact]
    public void Security_narrowing_surfaces_via_profile_diff_and_is_not_escalation()
    {
        var prev = Manifest(Workload(privileges: new WorkloadPrivileges
        {
            Capabilities = new[] { WorkloadCapability.NetAdmin },
        }));
        var cand = Manifest(Workload());

        var diff = Compute(prev, cand);

        var wd = diff.Workloads[0];
        wd.RequirementsDiff.Security.Should().Be(RequirementChangeKind.Narrowing);
        wd.RequirementsDiff.NarrowedFields.Should().ContainSingle().Which.Should().Contain("NET_ADMIN");
        diff.AnySecurityEscalation.Should().BeFalse();
    }

    [Fact]
    public void Added_volume_is_informational_not_security()
    {
        var prev = Manifest(Workload());
        var cand = Manifest(Workload(volumes: new[]
        {
            new WorkloadVolume { Id = "cache", MountPath = "/cache", Size = "64Mi", Criticality = VolumeCriticality.Cache },
        }));

        var diff = Compute(prev, cand);

        var wd = diff.Workloads[0];
        wd.RequirementsDiff.Security.Should().Be(RequirementChangeKind.Unchanged);
        wd.RequirementsDiff.InformationalVolumeChange.Should().BeTrue();
        diff.AnySecurityEscalation.Should().BeFalse();
        diff.VolumeNotes.Should().ContainSingle().Which.Should().Contain("cache");
    }

    [Fact]
    public void Volume_growth_is_informational_not_security()
    {
        var prev = Manifest(Workload(volumes: new[]
        {
            new WorkloadVolume { Id = "cache", MountPath = "/cache", Size = "64Mi", Criticality = VolumeCriticality.Cache },
        }));
        var cand = Manifest(Workload(volumes: new[]
        {
            new WorkloadVolume { Id = "cache", MountPath = "/cache", Size = "256Mi", Criticality = VolumeCriticality.Cache },
        }));

        var diff = Compute(prev, cand);

        diff.AnySecurityEscalation.Should().BeFalse();
        diff.VolumeNotes.Should().ContainSingle().Which.Should().Contain("64Mi → 256Mi");
    }

    [Fact]
    public void Host_extensions_are_carried_verbatim_on_both_sides()
    {
        var previous = Extensions("""{"acme":{"tier":"gold"}}""");
        var candidate = Extensions("""{"acme":{"tier":"platinum"},"other":{"k":1}}""");
        var prev = Manifest(Workload(extensions: previous));
        var cand = Manifest(Workload(extensions: candidate), Workload(id: "added", extensions: candidate));

        var diff = Compute(prev, cand);

        var changed = diff.Workloads.Single(w => w.WorkloadId == "w");
        changed.FromExtensions!.For("acme")!.Value.GetProperty("tier").GetString().Should().Be("gold");
        changed.ToExtensions.For("acme")!.Value.GetProperty("tier").GetString().Should().Be("platinum");
        changed.ToExtensions.For("other").Should().NotBeNull();

        var added = diff.Workloads.Single(w => w.WorkloadId == "added");
        added.FromExtensions.Should().BeNull();
        added.ToExtensions.For("acme")!.Value.GetProperty("tier").GetString().Should().Be("platinum");
        diff.Workloads.Should().OnlyContain(w => !w.PreviousUnreadable, "both sides were read");
    }

    [Fact]
    public void An_unreadable_previous_side_carries_no_extensions_and_says_so()
    {
        var declared = Extensions("""{"acme":{"tier":"gold"}}""");
        var standIn = Manifest(Workload(extensions: declared));

        var diff = WorkloadDeploymentDiff.Compute(
            standIn, ProfilesOf(standIn), NoResolvedValues(), standIn, ProfilesOf(standIn), previousUnreadable: true);

        var workload = diff.Workloads.Should().ContainSingle().Subject;
        workload.PreviousUnreadable.Should().BeTrue();
        workload.IsAdded.Should().BeFalse();
        workload.FromExtensions.Should().BeSameAs(WorkloadExtensions.None);
        workload.ToExtensions.For("acme").Should().NotBeNull("the candidate side was read");
    }

    private static WorkloadExtensions Extensions(string json)
        => new()
        {
            ByNamespace = System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
        };

    [Fact]
    public void New_required_param_with_no_default_and_no_previous_value_is_flagged()
    {
        var prev = Manifest(Workload());
        var cand = Manifest(Workload(parameters: new[] { Param("apiToken", required: true) }));

        var diff = Compute(prev, cand);

        var wd = diff.Workloads[0];
        wd.NewRequiredParameters.Should().ContainSingle().Which.Should().Be("apiToken");
        diff.AnyNewRequiredParameters.Should().BeTrue();
    }

    [Fact]
    public void New_required_param_with_a_default_is_not_flagged()
    {
        var prev = Manifest(Workload());
        var cand = Manifest(Workload(parameters: new[] { Param("region", required: true, @default: "eu") }));

        var diff = Compute(prev, cand);

        diff.Workloads[0].NewRequiredParameters.Should().BeEmpty();
        diff.AnyNewRequiredParameters.Should().BeFalse();
    }

    [Fact]
    public void New_optional_param_is_not_flagged()
    {
        var prev = Manifest(Workload());
        var cand = Manifest(Workload(parameters: new[] { Param("nickname", required: false) }));

        var diff = Compute(prev, cand);

        diff.Workloads[0].NewRequiredParameters.Should().BeEmpty();
    }

    [Fact]
    public void Required_param_satisfiable_from_previous_resolved_value_is_not_flagged()
    {
        var prev = Manifest(Workload(parameters: new[] { Param("apiToken", required: true) }));
        var cand = Manifest(Workload(parameters: new[] { Param("apiToken", required: true) }));

        var previousResolved = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
        {
            ["w"] = new Dictionary<string, string?>(StringComparer.Ordinal) { ["apiToken"] = "existing-secret" },
        };

        var diff = Compute(prev, cand, previousResolvedParameters: previousResolved);

        diff.Workloads[0].NewRequiredParameters.Should().BeEmpty(
            "a required param already resolved from the prior install is satisfiable — not 'new'");
    }

    [Fact]
    public void Required_param_present_but_blank_in_previous_values_is_still_flagged()
    {
        var prev = Manifest(Workload());
        var cand = Manifest(Workload(parameters: new[] { Param("apiToken", required: true) }));

        var previousResolved = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
        {
            ["w"] = new Dictionary<string, string?>(StringComparer.Ordinal) { ["apiToken"] = "   " },
        };

        var diff = Compute(prev, cand, previousResolvedParameters: previousResolved);

        diff.Workloads[0].NewRequiredParameters.Should().ContainSingle().Which.Should().Be("apiToken");
    }

    [Fact]
    public void New_required_parameters_are_sorted_deterministically()
    {
        var prev = Manifest(Workload());
        var cand = Manifest(Workload(parameters: new[]
        {
            Param("zeta", required: true),
            Param("alpha", required: true),
            Param("mid", required: true),
        }));

        var diff = Compute(prev, cand);

        diff.Workloads[0].NewRequiredParameters.Should().Equal("alpha", "mid", "zeta");
    }

    [Fact]
    public void Added_workload_is_reported()
    {
        var prev = Manifest(Workload(id: "a"));
        var cand = Manifest(Workload(id: "a"), Workload(id: "b"));

        var diff = Compute(prev, cand);

        diff.AddedWorkloadIds.Should().Equal("b");
        diff.RemovedWorkloadIds.Should().BeEmpty();
        diff.Workloads.Select(w => w.WorkloadId).Should().Contain("a");
    }

    [Fact]
    public void Removed_workload_is_reported()
    {
        var prev = Manifest(Workload(id: "a"), Workload(id: "b"));
        var cand = Manifest(Workload(id: "a"));

        var diff = Compute(prev, cand);

        diff.RemovedWorkloadIds.Should().Equal("b");
        diff.AddedWorkloadIds.Should().BeEmpty();
    }

    [Fact]
    public void Added_and_removed_workload_ids_are_sorted()
    {
        var prev = Manifest(Workload(id: "keep"), Workload(id: "gone-z"), Workload(id: "gone-a"));
        var cand = Manifest(Workload(id: "keep"), Workload(id: "new-z"), Workload(id: "new-a"));

        var diff = Compute(prev, cand);

        diff.AddedWorkloadIds.Should().Equal("new-a", "new-z");
        diff.RemovedWorkloadIds.Should().Equal("gone-a", "gone-z");
    }

    [Fact]
    public void An_added_privileged_workload_has_a_vacuous_requirements_diff()
    {
        var prev = Manifest(Workload(id: "a"));
        var cand = Manifest(
            Workload(id: "a"),
            Workload(id: "b", privileges: new WorkloadPrivileges { Privileged = true }));

        var diff = Compute(prev, cand);

        diff.AddedWorkloadIds.Should().Equal("b");
        var added = diff.Workloads.Single(w => w.WorkloadId == "b");
        added.IsAdded.Should().BeTrue();
        added.RequirementsDiff.Security.Should().Be(RequirementChangeKind.Unchanged);
        added.RequirementsDiff.EscalatedFields.Should().BeEmpty();
        added.RequirementsDiff.NarrowedFields.Should().BeEmpty();
        diff.AnySecurityEscalation.Should().BeFalse();
    }

    [Fact]
    public void Added_non_privileged_workload_has_no_escalation_line_items()
    {
        var prev = Manifest(Workload(id: "a"));
        var cand = Manifest(
            Workload(id: "a"),
            Workload(id: "b", network: new WorkloadNetwork { Mdns = true }));

        var diff = Compute(prev, cand);

        var added = diff.Workloads.Single(w => w.WorkloadId == "b");
        added.RequirementsDiff.EscalatedFields.Should().BeEmpty(
            "an added workload with no red-list element must itemize no red privileges");
        added.RequirementsDiff.Security.Should().Be(RequirementChangeKind.Unchanged);
    }

    [Fact]
    public void Added_privileged_workload_escalation_lines_are_sorted_deterministically()
    {
        var prev = Manifest(Workload(id: "a"));
        var cand = Manifest(
            Workload(id: "a"),
            Workload(id: "b", privileges: new WorkloadPrivileges
            {
                Privileged = true,
                Capabilities = new[] { WorkloadCapability.SysAdmin, WorkloadCapability.NetAdmin },
            }));

        var diff = Compute(prev, cand);
        var first = diff.Workloads.Single(w => w.WorkloadId == "b").RequirementsDiff.EscalatedFields;

        var second = Compute(prev, cand).Workloads.Single(w => w.WorkloadId == "b").RequirementsDiff.EscalatedFields;
        first.Should().Equal(second);
    }

    [Fact]
    public void Added_workload_with_a_new_required_undefaulted_param_surfaces_it_as_new_required()
    {
        var prev = Manifest(Workload(id: "a"));
        var cand = Manifest(
            Workload(id: "a"),
            Workload(id: "b", parameters: new[] { Param("apiToken", required: true) }));

        var diff = Compute(prev, cand);

        var added = diff.Workloads.Single(w => w.WorkloadId == "b");
        added.IsAdded.Should().BeTrue();
        added.NewRequiredParameters.Should().ContainSingle().Which.Should().Be("apiToken",
            "an added workload's required, undefaulted, unsatisfiable param is a new-required param");
        diff.AnyNewRequiredParameters.Should().BeTrue();
    }

    [Fact]
    public void Added_workload_with_only_optional_or_defaulted_params_has_no_new_required()
    {
        var prev = Manifest(Workload(id: "a"));
        var cand = Manifest(
            Workload(id: "a"),
            Workload(id: "b", parameters: new[]
            {
                Param("nickname", required: false),
                Param("region", required: true, @default: "eu"),
            }));

        var diff = Compute(prev, cand);

        var added = diff.Workloads.Single(w => w.WorkloadId == "b");
        added.IsAdded.Should().BeTrue();
        added.NewRequiredParameters.Should().BeEmpty(
            "an optional param and a defaulted-required param are both satisfiable without operator input");
        diff.AnyNewRequiredParameters.Should().BeFalse();
    }

    [Fact]
    public void Per_workload_entries_are_ordered_by_id()
    {
        var prev = Manifest(Workload(id: "zeta"), Workload(id: "alpha"), Workload(id: "mid"));
        var cand = Manifest(Workload(id: "zeta"), Workload(id: "alpha"), Workload(id: "mid"));

        var diff = Compute(prev, cand);

        diff.Workloads.Select(w => w.WorkloadId).Should().Equal("alpha", "mid", "zeta");
    }

    private static WorkloadResources Resources(
        string? requestsCpu = null, string? requestsMemory = null,
        string? limitsCpu = null, string? limitsMemory = null)
        => new()
        {
            Requests = requestsCpu is null && requestsMemory is null
                ? null
                : new ResourceQuantities { Cpu = requestsCpu, Memory = requestsMemory },
            Limits = limitsCpu is null && limitsMemory is null
                ? null
                : new ResourceQuantities { Cpu = limitsCpu, Memory = limitsMemory },
        };

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Resolved(
        params (string Key, string Value)[] entries)
        => new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
        {
            ["w"] = entries.ToDictionary(e => e.Key, e => (string?)e.Value, StringComparer.Ordinal),
        };

    [Fact]
    public void A_declaration_that_did_not_move_between_versions_is_not_a_resources_change()
    {
        var diff = Compute(
            Manifest(Workload(resources: Resources(limitsCpu: "100m"))),
            Manifest(Workload(resources: Resources(limitsCpu: "100m"))),
            previousResolvedParameters: Resolved(("resources.limits.cpu", "50m")));

        diff.Workloads.Single().ResourcesChanged.Should().BeFalse(
            "the environment differing from the manifest is not a change BETWEEN versions");
    }

    [Fact]
    public void The_same_value_spelled_in_another_unit_is_not_a_change()
    {
        var diff = Compute(
            Manifest(Workload(resources: Resources(limitsMemory: "1Gi"))),
            Manifest(Workload(resources: Resources(limitsMemory: "1024Mi"))),
            previousResolvedParameters: Resolved(("resources.limits.memory", "512Mi")));

        diff.Workloads.Single().ResourcesChanged.Should().BeFalse("1Gi and 1024Mi are the same declaration");
    }

    [Fact]
    public void A_moved_declaration_reports_both_sides_and_the_operators_override()
    {
        var diff = Compute(
            Manifest(Workload(resources: Resources(limitsCpu: "100m"))),
            Manifest(Workload(resources: Resources(limitsCpu: "250m"))),
            previousResolvedParameters: Resolved(("resources.limits.cpu", "50m")));

        var w = diff.Workloads.Single();
        w.ResourcesChanged.Should().BeTrue();
        w.FromResources!.LimitsCpu.Should().Be("100m");
        w.ToResources!.LimitsCpu.Should().Be("250m");
        w.OperatorResourceOverrides!.LimitsCpu.Should().Be("50m");
    }

    [Fact]
    public void A_workload_without_an_override_reports_the_change_but_carries_no_overrides_to_adopt()
    {
        var diff = Compute(
            Manifest(Workload(resources: Resources(limitsCpu: "100m"))),
            Manifest(Workload(resources: Resources(limitsCpu: "250m"))));

        var w = diff.Workloads.Single();
        w.ResourcesChanged.Should().BeTrue();
        w.OperatorResourceOverrides.Should().BeNull("without an override there is nothing to adopt");
    }

    [Fact]
    public void An_added_workload_reports_no_resources_change_because_there_is_no_previous_declaration()
    {
        var diff = Compute(
            Manifest(Workload(id: "a")),
            Manifest(Workload(id: "a"), Workload(id: "b", resources: Resources(limitsCpu: "250m"))));

        var added = diff.Workloads.Single(w => w.IsAdded);
        added.ResourcesChanged.Should().BeFalse();
        added.FromResources.Should().BeNull();
        added.ToResources!.LimitsCpu.Should().Be("250m");
    }

    [Fact]
    public void A_declaration_that_appears_where_there_was_none_is_a_change()
    {
        var diff = Compute(
            Manifest(Workload()),
            Manifest(Workload(resources: Resources(limitsMemory: "256Mi"))),
            previousResolvedParameters: Resolved(("resources.limits.memory", "512Mi")));

        diff.Workloads.Single().ResourcesChanged.Should().BeTrue("an axis went from undeclared to declared");
    }

    [Fact]
    public void Only_the_axes_whose_declaration_moved_are_adoptable()
    {
        var moved = WorkloadDeploymentDiff.MovedResourceAxisParameters(
            Workload(resources: Resources(limitsCpu: "100m", limitsMemory: "256Mi")),
            Workload(resources: Resources(limitsCpu: "250m", limitsMemory: "256Mi")));

        moved.Should().Equal(["resources.limits.cpu"]);
    }

    [Fact]
    public void A_declaration_restated_in_another_unit_yields_no_adoptable_axis()
    {
        var moved = WorkloadDeploymentDiff.MovedResourceAxisParameters(
            Workload(resources: Resources(limitsMemory: "1Gi")),
            Workload(resources: Resources(limitsMemory: "1024Mi")));

        moved.Should().BeEmpty();
    }

    [Fact]
    public void An_added_workload_has_no_previous_declaration_so_nothing_is_adoptable()
    {
        var moved = WorkloadDeploymentDiff.MovedResourceAxisParameters(
            previous: null,
            Workload(resources: Resources(limitsCpu: "250m")));

        moved.Should().BeEmpty();
    }

    [Fact]
    public void Every_moved_axis_is_reported_under_its_reserved_parameter_name()
    {
        var moved = WorkloadDeploymentDiff.MovedResourceAxisParameters(
            Workload(resources: Resources(requestsCpu: "100m", requestsMemory: "64Mi", limitsCpu: "200m", limitsMemory: "128Mi")),
            Workload(resources: Resources(requestsCpu: "150m", requestsMemory: "96Mi", limitsCpu: "300m", limitsMemory: "256Mi")));

        moved.Should().BeEquivalentTo([
            "resources.requests.cpu", "resources.requests.memory",
            "resources.limits.cpu", "resources.limits.memory",
        ]);
    }
}
