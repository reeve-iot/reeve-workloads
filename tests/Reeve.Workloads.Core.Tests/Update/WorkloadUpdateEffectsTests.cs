using Microsoft.AspNetCore.DataProtection;
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Providers.Kubernetes;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class WorkloadUpdateEffectsTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RepositoryId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> calls = [];
    private readonly RecordingParameterWriter parameterWriter;
    private readonly InMemoryWorkloadGroupRepository modules = new();

    public WorkloadUpdateEffectsTests()
    {
        parameterWriter = new RecordingParameterWriter(calls);
    }

    private WorkloadUpdateEffects Build() => new(
        parameterWriter, modules, new FixedClock(Now), NullLogger<WorkloadUpdateEffects>.Instance,
        new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider()));

    [Fact]
    public async Task The_forward_commit_writes_only_the_parameters()
    {
        await Build().CommitParamsAsync(Plan());

        calls.Should().Equal("set-parameters");
        modules.Saves.Should().BeEmpty("the module row moved at SwappingModule, not here");
    }

    [Fact]
    public async Task The_forward_commit_persists_a_candidate_reference_instead_of_its_resolved_value()
    {
        const string reference = "ref:provider:path#password";
        const string resolvedSecret = "resolved-secret-value";
        var workload = new WorkloadManifest
        {
            Id = "gateway",
            DisplayName = "Gateway",
            Image = new WorkloadImage { Ref = "registry/image", Digest = "sha256:abc" },
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Parameters = [new WorkloadParameter { Name = "dbPassword", Required = true, Type = WorkloadParameterType.Password }],
        };
        var plan = Plan(candidates: new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
        {
            ["gateway"] = new Dictionary<string, string?> { ["dbPassword"] = resolvedSecret },
        }) with
        {
            CandidateManifest = new DeploymentManifest { SchemaVersion = 1, Workloads = [workload] },
            CandidateStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
            {
                ["gateway"] = new Dictionary<string, string?> { ["dbPassword"] = reference },
            },
        };

        await Build().CommitParamsAsync(plan);

        parameterWriter.LastWritten!["gateway"]["dbPassword"].Should().Be(reference);
        parameterWriter.LastWritten["gateway"]["dbPassword"].Should().NotBe(resolvedSecret);
    }

    [Fact]
    public async Task The_forward_commit_protects_a_value_the_candidate_types_as_a_password()
    {
        var workload = new WorkloadManifest
        {
            Id = "gateway",
            DisplayName = "Gateway",
            Image = new WorkloadImage { Ref = "registry/image", Digest = "sha256:abc" },
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Parameters = [new WorkloadParameter { Name = "token", Type = WorkloadParameterType.Password }],
        };
        var plan = Plan(candidates: new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
        {
            ["gateway"] = new Dictionary<string, string?> { ["token"] = "s3cret" },
        }) with
        {
            CandidateManifest = new DeploymentManifest { SchemaVersion = 1, Workloads = [workload] },
        };

        await Build().CommitParamsAsync(plan);

        parameterWriter.LastWritten!["gateway"]["token"].Should().StartWith(WorkloadParameterSecretCodec.Prefix);
    }

    [Fact]
    public async Task Only_the_planned_workloads_with_candidate_values_are_written()
    {
        var plan = Plan(
            workloadIds: ["gateway", "bridge"],
            candidates: new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
            {
                ["gateway"] = new Dictionary<string, string?> { ["port"] = "8080" },
                ["not-in-the-plan"] = new Dictionary<string, string?> { ["port"] = "9999" },
            });

        await Build().CommitParamsAsync(plan);

        parameterWriter.LastWritten!.Keys.Should().Equal("gateway");
        parameterWriter.LastGroupInstanceId.Should().Be(InstanceId);
    }

    [Fact]
    public async Task A_plan_that_lists_one_workload_twice_does_not_throw()
    {
        var plan = Plan(
            workloadIds: ["gateway", "gateway"],
            candidates: new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
            {
                ["gateway"] = new Dictionary<string, string?> { ["port"] = "8080" },
            });

        await Build().CommitParamsAsync(plan);

        parameterWriter.LastWritten!.Keys.Should().Equal("gateway");
    }

    [Fact]
    public async Task Committing_a_null_plan_throws()
    {
        await FluentActions.Awaiting(() => Build().CommitParamsAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task The_commit_stamps_the_write_with_the_clocks_instant()
    {
        await Build().CommitParamsAsync(Plan());

        parameterWriter.LastStamp.Should().Be(Now);
    }

    [Fact]
    public async Task The_rollback_restores_the_parameters_the_document_holds()
    {
        var intent = Intent();

        await Build().RestorePreviousParamsAsync(intent);

        parameterWriter.LastWritten!["gateway"]["port"].Should().Be("8080");
        parameterWriter.LastGroupInstanceId.Should().Be(InstanceId);
        parameterWriter.LastStamp.Should().Be(Now);
    }

    [Fact]
    public async Task Rollback_without_recorded_password_names_restores_literals_unchanged()
    {
        var intent = Intent();
        Set(intent, nameof(WorkloadUpdateIntent.PreviousPasswordParameterNamesDocument), null);

        await Build().RestorePreviousParamsAsync(intent);

        parameterWriter.LastWritten!["gateway"]["port"].Should().Be("8080");
        parameterWriter.LastGroupInstanceId.Should().Be(InstanceId);
    }

    [Fact]
    public async Task Rollback_protects_password_literals_using_the_previous_schema_names()
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), InstanceId, "2.5.0", "2.6.0", Now);
        intent.StageSnapshots(new WorkloadUpdateSnapshots
        {
            PreviousParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>
            {
                ["gateway"] = new Dictionary<string, string?> { ["dbPassword"] = "clear-secret", ["label"] = "plain" },
            },
            PreviousPasswordParameterNames = new Dictionary<string, IReadOnlyList<string>>
            {
                ["gateway"] = ["dbPassword"],
            },
        }, Now);
        var codec = new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider());
        var effects = new WorkloadUpdateEffects(parameterWriter, modules, new FixedClock(Now),
            NullLogger<WorkloadUpdateEffects>.Instance, codec);

        await effects.RestorePreviousParamsAsync(intent);

        var restored = parameterWriter.LastWritten!["gateway"];
        restored["dbPassword"].Should().StartWith(WorkloadParameterSecretCodec.Prefix);
        codec.Unprotect(new WorkloadManifest
        {
            Id = "gateway", DisplayName = "gateway", Image = new WorkloadImage { Ref = "image" },
            Kind = WorkloadKind.Service, Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            Parameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
        }, restored)["dbPassword"].Should().Be("clear-secret");
        restored["label"].Should().Be("plain");
    }

    [Fact]
    public async Task A_corrupt_parameter_snapshot_restores_nothing_rather_than_throwing()
    {
        var intent = Intent();
        Set(intent, nameof(WorkloadUpdateIntent.PreviousParametersDocument),
            new WorkloadParameterMapDocument { Unreadable = true });

        await FluentActions.Awaiting(() => Build().RestorePreviousParamsAsync(intent)).Should().NotThrowAsync();

        calls.Should().BeEmpty("a snapshot that cannot be read must not be guessed at");
    }

    [Fact]
    public async Task An_unstaged_parameter_snapshot_restores_nothing()
    {
        var intent = Intent(stageParameters: false);

        await Build().RestorePreviousParamsAsync(intent);

        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Restoring_from_a_null_intent_throws()
    {
        await FluentActions.Awaiting(() => Build().RestorePreviousParamsAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task The_installed_version_commit_moves_the_module_row()
    {
        var row = modules.Seed("zwave", "repo-1", "2.5.0");

        await Build().CommitInstalledVersionAsync("ZWave", "2.6.0");

        row.InstalledVersion.Should().Be("2.6.0");
        row.UpdatedAtUtc.Should().Be(Now);
        modules.Saves.Should().ContainSingle().Which.Should().BeSameAs(row);
        calls.Should().BeEmpty("the row is the library's own and saves itself");
    }

    [Fact]
    public async Task A_row_already_at_the_target_is_not_saved_again()
    {
        modules.Seed("zwave", "repo-1", "2.6.0");

        await Build().CommitInstalledVersionAsync("zwave", "2.6.0");

        modules.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task A_module_with_no_row_returns_without_throwing()
    {
        await FluentActions.Awaiting(() => Build().CommitInstalledVersionAsync("zwave", "2.6.0"))
            .Should().NotThrowAsync();

        modules.Saves.Should().BeEmpty();
    }

    [Fact]
    public void Every_missing_port_is_refused_at_construction()
    {
        var clock = new FixedClock(Now);
        var log = NullLogger<WorkloadUpdateEffects>.Instance;

        var attempts = new Action[]
        {
            () => _ = new WorkloadUpdateEffects(null!, modules, clock, log, new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider())),
            () => _ = new WorkloadUpdateEffects(parameterWriter, null!, clock, log, new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider())),
            () => _ = new WorkloadUpdateEffects(parameterWriter, modules, null!, log, new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider())),
            () => _ = new WorkloadUpdateEffects(parameterWriter, modules, clock, null!, new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider())),
            () => _ = new WorkloadUpdateEffects(parameterWriter, modules, clock, log, null!),
        };

        attempts.Should().AllSatisfy(attempt => attempt.Should().Throw<ArgumentNullException>());
    }

    private static WorkloadUpdatePlan Plan(
        IReadOnlyList<string>? workloadIds = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>? candidates = null)
    {
        var ids = workloadIds ?? ["gateway"];
        return new WorkloadUpdatePlan
        {
            GroupInstanceId = InstanceId,
            GroupId = "zwave",
            SourceKey = RepositoryId.ToString("D"),
            FromVersion = "2.5.0",
            ToVersion = "2.6.0",
            PreviousManifest = new DeploymentManifest { SchemaVersion = 1, Workloads = [] },
            CandidateManifest = new DeploymentManifest { SchemaVersion = 1, Workloads = [] },
            PreviousRequirements = new Dictionary<string, Reeve.Workloads.Abstractions.Domain.WorkloadPrivilegeRequirements>(StringComparer.Ordinal),
            CandidateRequirements = new Dictionary<string, Reeve.Workloads.Abstractions.Domain.WorkloadPrivilegeRequirements>(StringComparer.Ordinal),
            RequirementsFingerprint = "unused-here",
            PreviousResolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal),
            PreviousStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal),
            CandidateResolvedParameters = candidates
                ?? new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
                {
                    ["gateway"] = new Dictionary<string, string?> { ["port"] = "8080" },
                },
            CandidateStoredParameters = candidates
                ?? new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
                {
                    ["gateway"] = new Dictionary<string, string?> { ["port"] = "8080" },
                },
            Workloads = ids.Select(WorkloadItem).ToList(),
            TargetSpecs = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal),
            PreviousSpecs = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal),
            FromDigests = new Dictionary<string, string?>(StringComparer.Ordinal),
            ToDigests = new Dictionary<string, string?>(StringComparer.Ordinal),
        };
    }

    private static WorkloadUpdatePlanItem WorkloadItem(string workloadId) => new()
    {
        WorkloadId = workloadId,
        Reference = new WorkloadRef
        {
            InstanceId = InstanceId,
            GroupId = "zwave",
            WorkloadId = workloadId,
            Inst8 = "11111111",
            Provider = KubernetesProvider.Id,
        },
        TargetSpec = null!,
        PreviousSpec = null,
        CurrentExtensions = ReeveTestExtensions.Pair(),
        CandidateExtensions = ReeveTestExtensions.Pair(),
        Certification = WorkloadHealthGateCertification.HostEvidence,
        PreviousCertification = WorkloadHealthGateCertification.HostEvidence,
    };

    private static void Set(object entity, string property, object? value)
        => entity.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!.SetValue(entity, value);

    private static WorkloadUpdateIntent Intent(bool stageParameters = true)
    {
        var intent = WorkloadUpdateIntent.Create(Guid.NewGuid(), InstanceId, "2.5.0", "2.6.0", Now);
        intent.StageSnapshots(
            new WorkloadUpdateSnapshots
            {
                PreviousParameters = stageParameters
                    ? new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal)
                    {
                        ["gateway"] = new Dictionary<string, string?> { ["port"] = "8080" },
                    }
                    : null,
                PreviousPasswordParameterNames = stageParameters
                    ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                    {
                        ["gateway"] = Array.Empty<string>(),
                    }
                    : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            },
            Now);
        return intent;
    }

    private sealed class FixedClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow => now;
    }


    private sealed class RecordingParameterWriter(List<string> calls) : IWorkloadResolvedParameterWriter
    {
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>? LastWritten { get; private set; }

        public Guid? LastGroupInstanceId { get; private set; }

        public DateTimeOffset? LastStamp { get; private set; }

        public Task<int> SetResolvedParametersAsync(
            Guid groupInstanceId,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> byWorkloadId,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken = default)
        {
            calls.Add("set-parameters");
            LastGroupInstanceId = groupInstanceId;
            LastWritten = byWorkloadId;
            LastStamp = updatedAtUtc;
            return Task.FromResult(byWorkloadId.Count);
        }
    }
}
