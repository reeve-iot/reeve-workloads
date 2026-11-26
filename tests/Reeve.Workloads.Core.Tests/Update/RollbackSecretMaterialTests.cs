using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Groups;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.DeploymentFixtures;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Update;

public sealed class RollbackSecretMaterialTests
{
    private const string GroupId = "acme.zwave";
    private const string WorkloadId = "controller";
    private const string From = "1.0.0";
    private const string To = "1.1.0";
    private const string OldDbPassword = "old-db-secret";
    private const string HostMqttPassword = "not-a-real-password";
    private static readonly Guid GroupInstanceId = new("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset At = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_rollback_restores_a_previous_spec_whose_secret_references_are_not_the_token()
    {
        var (h, intent) = await FailedAtTheGateAsync(operatorParameters: null);

        var restore = h.Provider.AppliedSpecs.Last();
        using var scope = new AssertionScope();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack, intent.Error);
        restore.SpecHash.Should().Be(h.Previous.SpecHash, "the last apply is the restore of the previous version");
        restore.SecretMaterial.Should().Equal(new Dictionary<string, string>
        {
            ["DB_PASSWORD"] = OldDbPassword,
            ["MQTT_PASSWORD"] = HostMqttPassword,
        });
    }

    [Fact]
    public async Task A_rollback_restores_the_previous_password_not_the_one_submitted_with_the_update()
    {
        var (h, intent) = await FailedAtTheGateAsync(
            new Dictionary<string, string?> { ["dbPassword"] = "new-db-secret" });

        var forward = h.Provider.AppliedSpecs.First();
        var restore = h.Provider.AppliedSpecs.Last();
        using var scope = new AssertionScope();
        intent.Outcome.Should().Be(WorkloadUpdateOutcome.RolledBack, intent.Error);
        forward.SecretMaterial["DB_PASSWORD"].Should().Be("new-db-secret", "the candidate carries the submitted password");
        restore.SecretMaterial["DB_PASSWORD"].Should().Be(OldDbPassword);
    }

    [Fact]
    public async Task The_recorded_previous_spec_stays_free_of_plaintext()
    {
        var (_, intent) = await FailedAtTheGateAsync(operatorParameters: null);

        var recorded = JsonSerializer.Serialize(intent.PreviousSpecsDocument);
        using var scope = new AssertionScope();
        recorded.Should().Contain("MQTT_PASSWORD", "the snapshot records the reference, by name");
        recorded.Should().NotContain(OldDbPassword).And.NotContain(HostMqttPassword);
    }

    private static async Task<(Harness H, WorkloadUpdateIntent Intent)> FailedAtTheGateAsync(
        IReadOnlyDictionary<string, string?>? operatorParameters)
    {
        var planner = RealPlanner();
        var first = await planner.BuildPlanAsync(GroupInstanceId, To, intentId: null);
        var h = new Harness(new OrchestratorHarness(first, At), first.Workloads.Single().PreviousSpec!);
        h.Inner.Planner.Real = planner;
        h.Provider.ApplyBehaviour[WorkloadId] = RefuseUnresolvedSecretReferences;
        h.Inner.Gate.VerdictsByWorkload[WorkloadId] = new Queue<WorkloadHealthGateVerdict>([WorkloadHealthGateVerdict.Failed]);
        h.Inner.ObservedVersions.VersionsByInstance[first.Workloads.Single().Reference.InstanceId] = To;

        var intent = await h.Inner.StageManualAsync(first, operatorParameters);
        for (var tick = 0; tick < 30 && !intent.IsTerminal; tick++)
        {
            await h.Inner.Orchestrator.AdvanceAsync(intent);
        }

        return (h, intent);
    }

    private static WorkloadApplyResult RefuseUnresolvedSecretReferences(WorkloadDesiredSpec spec)
    {
        var unresolved = spec.Env
            .Where(e => e.Kind == ResolvedEnvKind.SecretRef
                && e.CredentialId is null
                && !spec.SecretMaterial.ContainsKey(e.Name))
            .Select(e => e.Name)
            .ToList();
        return unresolved.Count == 0
            ? WorkloadApplyResult.Applied(spec.SpecHash)
            : WorkloadApplyResult.Failed(WorkloadApplyReason.Rejected,
                $"secret reference(s) with no resolved material: {string.Join(", ", unresolved)}");
    }

    private sealed record Harness(OrchestratorHarness Inner, WorkloadDesiredSpec Previous)
    {
        public RecordingProvider Provider => Inner.Provider;
    }

    private static IWorkloadUpdatePlanner RealPlanner()
    {
        var manifests = new FakeManifests();
        manifests.AtVersion[From] = new GroupManifestRead(Manifest('a'), Refused: false);
        manifests.AtVersion[To] = new GroupManifestRead(Manifest('b'), Refused: false);
        var modules = new InMemoryWorkloadGroupRepository();
        modules.Seed(GroupId, FakeManifests.SourceKey, From);
        var secrets = new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider());
        var instance = WorkloadInstance.Create(Guid.NewGuid(), GroupInstanceId, GroupId, WorkloadId, At);
        instance.SetResolvedParameters(
            secrets.Protect(Manifest('a').Workloads[0], new Dictionary<string, string?> { ["dbPassword"] = OldDbPassword }), At);
        var instances = new FakeInstances();
        instances.Instances.Add(instance);

        return new GroupManifestUpdatePlanner(
            manifests,
            modules,
            instances,
            new DefaultSettings(),
            new NoIdentity(),
            new WorkloadProviderRegistry([]),
            new WorkloadResourceNamerRegistry([]),
            new FakeHostBootstrapSource(),
            Options.Create(new ProductImageOptions()),
            NullLogger<GroupManifestUpdatePlanner>.Instance,
            [],
            secrets, new Reeve.Workloads.Core.NoWorkloadParameterReferenceResolver());
    }

    private static DeploymentManifest Manifest(char digest) => new()
    {
        SchemaVersion = 1,
        Workloads =
        [
            new WorkloadManifest
            {
                Id = WorkloadId,
                DisplayName = WorkloadId,
                Image = new WorkloadImage { Ref = "registry.example/zwave", Digest = new string(digest, 64) },
                Kind = WorkloadKind.Service,
                Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
                Parameters = [DeploymentManifestBuilder.Param("dbPassword", required: true, type: WorkloadParameterType.Password)],
                Env =
                [
                    new WorkloadEnv { Name = "DB_PASSWORD", From = "parameter:dbPassword" },
                    new WorkloadEnv { Name = "MQTT_PASSWORD", From = "bootstrap:mqttPassword" },
                ],
                Extensions = new WorkloadExtensions
                {
                    ByNamespace = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["reeve"] = JsonDocument
                            .Parse("""{"protocol":{"version":"1","minSupported":"1"}}""").RootElement.Clone(),
                    },
                },
            },
        ],
    };
}
