using Reeve.Workloads.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Core.Update;
using Reeve.Workloads.Domain.Network;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Testing;
using Xunit;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadParameterEditServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private const string PluginTypeId = "acme.zwave";
    private const string WorkloadId = "controller";

    private readonly InstanceStore repository = new();
    private readonly FakeWorkloadClusterBus bus = new();
    private readonly StubIntentProvider intentProvider = new();
    private readonly StubInputsProvider inputsProvider = new();
    private readonly EphemeralDataProtectionProvider protectionProvider = new();
    private readonly WorkloadParameterSecretCodec parameterSecrets;
    private readonly UnpinnedReferenceRolloutGuard referenceRolloutGuard;

    public WorkloadParameterEditServiceTests()
    {
        parameterSecrets = new WorkloadParameterSecretCodec(protectionProvider);
        referenceRolloutGuard = new UnpinnedReferenceRolloutGuard(protectionProvider);
    }

    private WorkloadParameterEditService CreateService(
        IWorkloadInstanceRepository? editRepository = null,
        IWorkloadAuditSink? auditSink = null,
        UnpinnedReferenceRolloutGuard? rolloutGuard = null)
        => new(
            editRepository ?? repository,
            intentProvider,
            inputsProvider,
            bus.Nudge,
            new FixedClock(Now),
            new WorkloadDeviceGroupIdWriter(inventory),
            NullLogger<WorkloadParameterEditService>.Instance,
            parameterSecrets,
            rolloutGuard ?? referenceRolloutGuard,
            auditSink ?? new NoWorkloadAuditSink(),
            ProviderRegistryFakes.FirstPartyRegistry());

    [Fact]
    public async Task Rebinding_password_reference_preserves_applied_hash_and_does_not_nudge()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "same-secret", ["logLevel"] = "info",
        });
        var appliedHash = instance.AppliedSpecHash;

        var result = await CreateService().RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "same-secret", "ref:vault:zwave#apiPassword", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["apiPassword"].Should().Be("ref:vault:zwave#apiPassword");
        stored.AppliedSpecHash.Should().Be(appliedHash);
        stored.State.Should().Be(WorkloadInstanceState.Healthy);
        bus.Published.Should().BeEmpty();
        var references = new Dictionary<string, string?> { ["apiPassword"] = "ref:vault:zwave#apiPassword" };
        referenceRolloutGuard.ReferencesForHash(stored, references).Should().BeEmpty();
        referenceRolloutGuard.GetPending(stored, references,
            new Dictionary<string, string?> { ["apiPassword"] = "same-secret" }, ["apiPassword"]).Should().BeEmpty();
        referenceRolloutGuard.GetPending(stored, references,
            new Dictionary<string, string?> { ["apiPassword"] = "rotated-secret" }, ["apiPassword"])
            .Should().ContainSingle(item => item.ParameterName == "apiPassword");
        referenceRolloutGuard.ApproveValues(stored, references,
            new Dictionary<string, string?> { ["apiPassword"] = "rotated-secret" }, ["apiPassword"], Now.AddMinutes(1));
        referenceRolloutGuard.ReferencesForHash(stored, references).Should().BeEquivalentTo(references);
        referenceRolloutGuard.GetPending(stored, references,
            new Dictionary<string, string?> { ["apiPassword"] = "rotated-secret" }, ["apiPassword"]).Should().BeEmpty();
    }

    [Fact]
    public async Task Rebinding_when_rollout_key_protection_fails_returns_validation_without_mutating()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "same-secret", ["logLevel"] = "info",
        });
        var failingGuard = new UnpinnedReferenceRolloutGuard(new FailingProtectionProvider());

        var result = await CreateService(rolloutGuard: failingGuard).RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "same-secret", "ref:vault:zwave#apiPassword", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        ClearPassword((await repository.GetByIdAsync(instance.Id))!).Should().Be("same-secret");
        repository.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Version_pinned_rebind_succeeds_when_rollout_key_protection_is_unavailable()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "same-secret", ["logLevel"] = "info",
        });

        var result = await CreateService(rolloutGuard: new UnpinnedReferenceRolloutGuard(new FailingProtectionProvider()))
            .RebindPasswordReferenceAsync(instance.Id, "apiPassword", "same-secret", "ref:vault:immutable#key@3",
                null, isVersionPinned: true);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        repository.Saves.Should().Be(1);
        (await repository.GetByIdAsync(instance.Id))!.GetResolvedParameters()["apiPassword"]
            .Should().Be("ref:vault:immutable#key@3");
    }

    [Fact]
    public async Task Rebinding_compares_expected_literal_case_sensitively()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "same-secret", ["logLevel"] = "info",
        });

        var result = await CreateService().RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "SAME-secret", "ref:vault:zwave#apiPassword", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.PreconditionFailed);
        result.FailureCode.Should().Be(WorkloadParameterEditFailureCode.StoredValueMismatch);
        repository.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Rebinding_does_not_change_restart_marker()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "same-secret", ["logLevel"] = "info",
        });
        instance.MarkRestart(Now);
        var restartGeneration = instance.RestartGeneration;

        var result = await CreateService().RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "same-secret", "ref:vault:zwave#apiPassword", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        (await repository.GetByIdAsync(instance.Id))!.RestartGeneration.Should().Be(restartGeneration);
    }

    [Fact]
    public async Task Rebind_audit_metadata_never_contains_the_password_literal()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "unique-secret-marker", ["logLevel"] = "info",
        });
        var audit = new RecordingAuditSink();

        var result = await CreateService(auditSink: audit).RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "unique-secret-marker", "ref:vault:zwave#apiPassword", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        JsonSerializer.Serialize(audit.Entries.Single().Metadata).Should().NotContain("unique-secret-marker");
    }

    [Fact]
    public async Task Rebinding_a_non_password_parameter_is_refused_without_a_save()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "same-secret", ["logLevel"] = "info",
        });

        var result = await CreateService().RebindPasswordReferenceAsync(
            instance.Id, "logLevel", "info", "ref:vault:zwave#logLevel", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        repository.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Rebinding_while_an_intent_is_active_is_refused_without_a_save()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "same-secret", ["logLevel"] = "info",
        });
        var activeIntent = Guid.NewGuid();
        intentProvider.ActiveIntentByPlugin[instance.GroupInstanceId] = activeIntent;

        var result = await CreateService().RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "same-secret", "ref:vault:zwave#apiPassword", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Conflict);
        result.ActiveIntentId.Should().Be(activeIntent);
        repository.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Rebinding_requires_a_reference_binding_without_a_save()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "same-secret", ["logLevel"] = "info",
        });

        var result = await CreateService().RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "same-secret", "vault/zwave#apiPassword", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        repository.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Rebinding_over_unreadable_rollout_json_is_refused_without_a_save()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "same-secret", ["logLevel"] = "info",
        });
        instance.SetReferenceRolloutState("{bad", Now);

        var result = await CreateService().RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "same-secret", "ref:vault:zwave#apiPassword", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        ClearPassword((await repository.GetByIdAsync(instance.Id))!).Should().Be("same-secret");
        repository.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Rebinding_frozen_or_terminating_instance_is_refused_without_a_save()
    {
        var frozen = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "frozen-secret", ["logLevel"] = "info",
        });
        frozen.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now.AddSeconds(1));
        var terminating = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "terminating-secret", ["logLevel"] = "info",
        });
        terminating.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, Now.AddSeconds(1));

        var service = CreateService();
        var frozenResult = await service.RebindPasswordReferenceAsync(
            frozen.Id, "apiPassword", "frozen-secret", "ref:vault:frozen", null, isVersionPinned: false);
        var terminatingResult = await service.RebindPasswordReferenceAsync(
            terminating.Id, "apiPassword", "terminating-secret", "ref:vault:terminating", null, isVersionPinned: false);

        frozenResult.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        terminatingResult.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        repository.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Rebinding_with_a_stale_literal_precondition_writes_nothing()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "current-secret", ["logLevel"] = "info",
        });

        var result = await CreateService().RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "stale-secret", "ref:vault:zwave#apiPassword", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.PreconditionFailed);
        result.FailureCode.Should().Be(WorkloadParameterEditFailureCode.StoredValueMismatch);
        ClearPassword((await repository.GetByIdAsync(instance.Id))!).Should().Be("current-secret");
        repository.Saves.Should().Be(0);
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Rebinding_an_existing_reference_returns_its_own_failure_code()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "ref:vault:existing", ["logLevel"] = "info",
        });

        var result = await CreateService().RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "same-secret", "ref:vault:zwave#apiPassword", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.PreconditionFailed);
        result.FailureCode.Should().Be(WorkloadParameterEditFailureCode.AlreadyReference);
        repository.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_rebind_write_is_refused_without_audit_or_nudge()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "same-secret", ["logLevel"] = "info",
        });
        var audit = new RecordingAuditSink();
        var result = await CreateService(
            new FaultingSaveRepository(repository, new WorkloadConcurrencyConflictException("concurrent row update")),
            audit).RebindPasswordReferenceAsync(
                instance.Id, "apiPassword", "same-secret", "ref:vault:zwave#apiPassword", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.PreconditionFailed);
        result.FailureCode.Should().Be(WorkloadParameterEditFailureCode.ConcurrentChange);
        audit.Entries.Should().BeEmpty();
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Rebind_audit_runs_after_the_reference_write_is_persisted()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "same-secret", ["logLevel"] = "info",
        });
        var audit = new RecordingAuditSink(entry =>
        {
            repository.GetByIdAsync(instance.Id).Result!.GetResolvedParameters()["apiPassword"]
                .Should().Be("ref:vault:zwave#apiPassword");
            entry.Action.Should().Be("workload.password_reference_rebound");
        });

        using var cancellation = new CancellationTokenSource();
        var result = await CreateService(auditSink: audit).RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "same-secret", "ref:vault:zwave#apiPassword", null, isVersionPinned: false,
            cancellation.Token);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        audit.Entries.Should().ContainSingle();
        audit.LastCancellationToken.Should().Be(CancellationToken.None);
        bus.Published.Should().BeEmpty();
    }

    private readonly FakeDeviceGroupSource inventory = new();


    private sealed class FailingProtectionProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new FailingProtector();
    }

    private sealed class FailingProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => throw new CryptographicException("injected protection failure");
        public byte[] Unprotect(byte[] protectedData) => throw new CryptographicException("injected protection failure");
        public IDataProtector CreateProtector(string purpose) => this;
    }

    private static WorkloadManifest BuildManifest() => new()
    {
        Id = WorkloadId,
        DisplayName = "Z-Wave controller",
        Image = new WorkloadImage { Ref = "ghcr.io/reeve/zwave", Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.SingleNode,
        Privileges = new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } } },
        Parameters = new[]
        {
            new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath, Required = true },
            new WorkloadParameter { Name = "apiPassword", Type = WorkloadParameterType.Password },
            new WorkloadParameter { Name = "logLevel", Type = WorkloadParameterType.Text, Default = "info" },
        },
        Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
    };

    private WorkloadInstance SeedHealthy(IReadOnlyDictionary<string, string?> parameters, Guid? pluginInstanceId = null)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), pluginInstanceId ?? Guid.NewGuid(), PluginTypeId, WorkloadId, Now);
        instance.SetProvider(KubernetesProvider.Id, Now);
        instance.SetAppliedSpecHash("sha256:applied", Now);
        inputsProvider.Manifest = BuildManifest();
        instance.SetResolvedParameters(parameterSecrets.Protect(inputsProvider.Manifest, parameters), Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        repository.AddAsync(instance).GetAwaiter().GetResult();
        return instance;
    }

    private void AssertNudged(Guid instanceId)
    {
        bus.Published.Should().ContainSingle();
        var (channel, payload) = bus.Published[0];
        channel.Should().Be(WorkloadClusterChannels.ReconcileRequest);
        payload.Should().BeOfType<WorkloadReconcileRequestMessage>().Which.InstanceId.Should().Be(instanceId);
    }

    [Fact]
    public async Task Editing_a_non_path_param_persists_moves_to_deploying_and_nudges()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["logLevel"] = "info",
        });

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["logLevel"].Should().Be("debug");
        stored.State.Should().Be(WorkloadInstanceState.Deploying);
        AssertNudged(instance.Id);
    }

    [Fact]
    public async Task Editing_a_parameter_clears_a_stale_reference_rollout_hold()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "old-secret", ["logLevel"] = "info",
        });
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired,
            "Password reference rollout state is unavailable (unreadable-key).", Now.AddMinutes(1));

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "replacement-secret", ["logLevel"] = "info",
        });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        (await repository.GetByIdAsync(instance.Id))!.State.Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public async Task A_Docker_edit_drops_a_user_namespace_value()
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" });
        inputsProvider.Provider = DockerProvider.Descriptor;

        var result = await EditJudgedAsync(CreateService(), instance.Id,
            new Dictionary<string, string?> { ["security.userNamespace"] = "true" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        (await repository.GetByIdAsync(instance.Id))!.GetResolvedParameters()
            .Should().NotContainKey("security.userNamespace");
    }

    [Fact]
    public async Task A_password_only_edit_bumps_the_restart_generation()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["apiPassword"] = "old",
            ["logLevel"] = "info",
        });
        var before = instance.RestartGeneration;

        var result = await EditJudgedAsync(CreateService(), instance.Id,
            new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "new", ["logLevel"] = "info" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        (await repository.GetByIdAsync(instance.Id))!.RestartGeneration.Should().Be(before + 1);
    }

    [Fact]
    public async Task A_masked_password_left_untouched_does_not_bump_the_restart_generation()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["apiPassword"] = "old",
            ["logLevel"] = "info",
        });
        var before = instance.RestartGeneration;

        var result = await EditJudgedAsync(CreateService(), instance.Id,
            new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "***", ["logLevel"] = "info" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        (await repository.GetByIdAsync(instance.Id))!.RestartGeneration.Should().Be(before);
    }

    [Fact]
    public async Task A_masked_authenticated_password_read_modify_write_preserves_ciphertext_and_restart_generation()
    {
        var manifest = BuildManifest();
        var encrypted = parameterSecrets.Protect(manifest, new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "stored-secret", ["logLevel"] = "info",
        });
        var instance = SeedHealthy(encrypted);
        var originalCiphertext = encrypted["apiPassword"];
        var before = instance.RestartGeneration;

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "***", ["logLevel"] = "debug",
        });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var updated = (await repository.GetByIdAsync(instance.Id))!;
        updated.GetResolvedParameters()["apiPassword"].Should().Be(originalCiphertext,
            "an unrelated read-modify-write must retain the authenticated bytes instead of encrypting them again");
        updated.RestartGeneration.Should().Be(before,
            "the clear password value did not change");
    }

    [Fact]
    public async Task A_blank_stored_password_cleared_to_nothing_does_not_bump_the_restart_generation()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["apiPassword"] = "   ",
            ["logLevel"] = "info",
        });
        var before = instance.RestartGeneration;

        var result = await EditJudgedAsync(CreateService(), instance.Id,
            new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = null, ["logLevel"] = "info" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        (await repository.GetByIdAsync(instance.Id))!.RestartGeneration.Should().Be(before);
    }

    [Fact]
    public async Task A_non_password_edit_does_not_bump_the_restart_generation()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["apiPassword"] = "old",
            ["logLevel"] = "info",
        });
        var before = instance.RestartGeneration;

        var result = await EditJudgedAsync(CreateService(), instance.Id,
            new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "***", ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        (await repository.GetByIdAsync(instance.Id))!.RestartGeneration.Should().Be(before);
    }

    [Fact]
    public async Task A_host_refusal_refuses_the_edit_with_the_hosts_reason_and_writes_nothing()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["logLevel"] = "info",
        });
        inputsProvider.Refusal = "host says no";

        var result = await CreateService().UpdateParametersAsync(
            instance.Id,
            new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "debug" },
            Guid.NewGuid(),
            new WorkloadWritePrecondition
            {
                RequirementsFingerprint = "unjudged",
                AttachmentPins = new Dictionary<string, WorkloadAttachmentPin>(StringComparer.Ordinal),
            });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.HostRefused);
        result.Error.Should().Contain("host says no");
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["logLevel"].Should().Be("info");
        stored.State.Should().Be(WorkloadInstanceState.Healthy);
        bus.Published.Should().BeEmpty();
    }

    private WorkloadInstance SeedFrozen(WorkloadInstanceState frozenState)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), PluginTypeId, WorkloadId, Now);
        instance.SetProvider(KubernetesProvider.Id, Now);
        instance.SetAppliedSpecHash("sha256:applied", Now);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" }, Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.ExternalHealthObserved, null, Now);
        if (frozenState == WorkloadInstanceState.ExternallyManaged)
        {
            instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);
        }
        instance.State.Should().Be(frozenState);
        instance.Provider.Should().Be(KubernetesProvider.Id, "the switch PRESERVES the original managed provider");
        repository.AddAsync(instance).GetAwaiter().GetResult();
        inputsProvider.Manifest = BuildManifest();
        return instance;
    }

    [Fact]
    public async Task Editing_a_disowned_ExternallyManaged_instance_is_rejected_and_never_re_adopts()
    {
        var instance = SeedFrozen(WorkloadInstanceState.ExternallyManaged);

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.State.Should().Be(WorkloadInstanceState.ExternallyManaged, "the guard must NOT drive the frozen instance to Deploying");
        stored.Provider.Should().Be(KubernetesProvider.Id);
        stored.GetResolvedParameters()["logLevel"].Should().Be("info", "no parameter mutation may persist");
        bus.Published.Should().BeEmpty("no reconcile nudge — a re-adopt must not be triggered");
    }

    [Fact]
    public async Task Editing_a_connected_manual_instance_persists_and_moves_to_deploying()
    {
        var instance = SeedConnectedManual(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["logLevel"].Should().Be("debug");
        stored.State.Should().Be(WorkloadInstanceState.Deploying, "the operator is asked to redeploy the re-rendered artifact");
        stored.AppliedSpecHash.Should().BeNull();
        AssertNudged(instance.Id);
    }

    [Fact]
    public async Task Rebinding_a_connected_manual_instance_password_to_a_reference_is_saved()
    {
        var instance = SeedConnectedManual(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "manual-secret", ["logLevel"] = "info",
        });

        var result = await CreateService().RebindPasswordReferenceAsync(
            instance.Id, "apiPassword", "manual-secret", "ref:vault:manual", null, isVersionPinned: false);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        (await repository.GetByIdAsync(instance.Id))!.GetResolvedParameters()["apiPassword"].Should().Be("ref:vault:manual");
    }

    private string? ClearPassword(WorkloadInstance instance)
        => parameterSecrets.Unprotect(inputsProvider.Manifest!, instance.GetResolvedParameters())["apiPassword"];

    private WorkloadInstance SeedConnectedManual(IReadOnlyDictionary<string, string?> parameters)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), PluginTypeId, WorkloadId, Now);
        instance.SetProvider(ManualProvider.Id, Now);
        instance.SetAppliedSpecHash("sha256:applied", Now);
        inputsProvider.Manifest = BuildManifest();
        instance.SetResolvedParameters(parameterSecrets.Protect(inputsProvider.Manifest, parameters), Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);
        repository.AddAsync(instance).GetAwaiter().GetResult();
        inputsProvider.Provider = ManualProvider.Descriptor;
        return instance;
    }

    [Fact]
    public async Task Editing_a_host_held_instance_is_rejected_and_keeps_its_hold()
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "withdrawn", Now);
        await repository.UpdateAsync(instance);

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.IsHeldByHost.Should().BeTrue("an operator cannot lift the host's hold");
        stored.GetResolvedParameters()["logLevel"].Should().Be("info");
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Editing_a_paused_instance_stores_the_edit_and_stays_paused()
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });
        instance.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, "paused", Now);
        await repository.UpdateAsync(instance);

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.State.Should().Be(WorkloadInstanceState.Suspended, "a pause is a deliberate hold; resume applies the change");
        stored.StateReason.Should().Be(WorkloadStateReason.OperatorPaused);
        stored.AppliedSpecHash.Should().BeNull();
        stored.GetResolvedParameters()["logLevel"].Should().Be("debug");
    }

    [Fact]
    public async Task Editing_a_mid_switch_Switching_instance_is_rejected_with_no_partial_persist()
    {
        var instance = SeedFrozen(WorkloadInstanceState.Switching);

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.State.Should().Be(WorkloadInstanceState.Switching);
        stored.GetResolvedParameters()["logLevel"].Should().Be("info", "no SetResolvedParameters/SetAppliedSpecHash may persist while Switching");
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_is_rejected_with_409_and_the_active_intent_id_when_an_intent_is_active()
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });
        var activeIntent = Guid.NewGuid();
        intentProvider.ActiveIntentByPlugin[instance.GroupInstanceId] = activeIntent;

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Conflict);
        result.ActiveIntentId.Should().Be(activeIntent);

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["logLevel"].Should().Be("info", "the edit must not persist while an intent is active");
        stored.State.Should().Be(WorkloadInstanceState.Healthy);
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reserved_existingClaim_binding_on_the_day2_edit_path_is_rejected_and_never_touches_state_or_pvc()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["logLevel"] = "info",
        });
        var appliedHashBefore = instance.AppliedSpecHash;

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["existingClaim.state"] = "reeve-wl-zwave-zwave-js-state-deadbeef" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed,
            "a reserved existingClaim.* binding cannot be applied via a day-2 parameter edit");
        result.Error.Should().Contain("existingClaim.state");

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("existingClaim.state",
            "the reserved binding must never be persisted");
        stored.State.Should().Be(WorkloadInstanceState.Healthy, "no state mutation — the instance stays live/unchanged");
        stored.AppliedSpecHash.Should().Be(appliedHashBefore, "AppliedSpecHash must NOT be cleared (no forced re-apply)");
        bus.Published.Should().BeEmpty("no reconcile nudge — nothing was applied");
    }

    [Fact]
    public async Task A_reserved_existingClaim_binding_is_rejected_even_alongside_a_legit_param_change()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["logLevel"] = "info",
        });

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?>
            {
                ["logLevel"] = "debug",
                ["existingClaim.state"] = "reeve-wl-zwave-zwave-js-state-deadbeef",
            });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["logLevel"].Should().Be("info", "the whole request is fail-closed — even the valid sibling edit does not land");
        stored.State.Should().Be(WorkloadInstanceState.Healthy);
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reserved_volumeSize_override_on_the_day2_edit_path_is_rejected_and_never_persisted()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["logLevel"] = "info",
        });
        var appliedHashBefore = instance.AppliedSpecHash;

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["volumeSize.state"] = "1Mi" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed,
            "a reserved volumeSize.* override cannot be set via a day-2 parameter edit (it bypasses the upward-only gate)");
        result.Error.Should().Contain("volumeSize.state");

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("volumeSize.state", "the reserved override must never be persisted");
        stored.State.Should().Be(WorkloadInstanceState.Healthy);
        stored.AppliedSpecHash.Should().Be(appliedHashBefore, "no forced re-apply");
        bus.Published.Should().BeEmpty();
    }

    private const string UndeclaredAccGidMessage =
        "Reserved parameter name(s) for workload 'controller': acc:gid. "
        + "A name containing ':' carries a server-derived value and can never be set by a client.";

    [Fact]
    public async Task An_undeclared_parameter_name_is_rejected_with_installs_message_and_never_persisted()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["logLevel"] = "info",
        });
        var appliedHashBefore = instance.AppliedSpecHash;

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["logLevel"] = "debug", ["acc:gid"] = "0" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        result.Error.Should().Be(UndeclaredAccGidMessage, "the edit path must answer with install's exact message");

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("acc:gid", "the undeclared key must never be persisted");
        stored.GetResolvedParameters()["logLevel"].Should().Be("info",
            "fail-closed: the legitimate change riding alongside is rejected too, never half-applied");
        stored.State.Should().Be(WorkloadInstanceState.Healthy);
        stored.AppliedSpecHash.Should().Be(appliedHashBefore, "no forced re-apply");
        bus.Published.Should().BeEmpty("no reconcile nudge — nothing was applied");
    }

    [Fact]
    public async Task A_pre_planted_undeclared_key_re_posted_as_the_mask_sentinel_is_rejected_too()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["logLevel"] = "info",
            ["acc:gid"] = "0",
        });

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["logLevel"] = "debug", ["acc:gid"] = "***" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        result.Error.Should().Be(UndeclaredAccGidMessage);
        bus.Published.Should().BeEmpty();
    }

    private static WorkloadManifest BuildAcceleratorManifest() => new()
    {
        Id = WorkloadId,
        DisplayName = "Accelerated controller",
        Image = new WorkloadImage { Ref = "ghcr.io/reeve/zwave", Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.SingleNode,
        Privileges = new WorkloadPrivileges
        {
            Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 2, Group = WorkloadDeviceGroup.Auto } },
        },
        Parameters = new[]
        {
            new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath },
            new WorkloadParameter { Name = "logLevel", Type = WorkloadParameterType.Text, Default = "info" },
        },
        Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
    };

    [Theory]
    [InlineData("acc:gid")]
    [InlineData("acc[0]:gid")]
    public async Task A_colon_namespaced_name_is_refused_by_the_day_2_edit_even_though_it_resolves(string submitted)
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["acc[0]"] = "/dev/dri/renderD128" });
        inputsProvider.Manifest = BuildAcceleratorManifest();
        var appliedHashBefore = instance.AppliedSpecHash;

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { [submitted] = "999" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        result.Error.Should().Be(
            $"Reserved parameter name(s) for workload 'controller': {submitted}. "
            + "A name containing ':' carries a server-derived value and can never be set by a client.");

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey(submitted, "a forged group id must never be persisted");
        stored.AppliedSpecHash.Should().Be(appliedHashBefore, "no forced re-apply");
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task An_indexed_device_key_is_still_accepted_by_the_day_2_edit()
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["acc[0]"] = "/dev/dri/renderD128" });
        inputsProvider.Manifest = BuildAcceleratorManifest();

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["acc[1]"] = "/dev/dri/renderD129", ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["acc[1]"].Should().Be("/dev/dri/renderD129");
        stored.GetResolvedParameters()["logLevel"].Should().Be("debug");
    }

    [Fact]
    public async Task A_reserved_but_undeclared_name_clears_the_accept_list_and_is_refused_by_its_own_gate()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["logLevel"] = "info",
        });

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["logLevel"] = "debug", ["resources.limits.memory"] = "512Mi" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        result.Error.Should().NotBe(UndeclaredAccGidMessage.Replace("acc:gid", "resources.limits.memory"),
            "a reserved name is NOT unknown — it must clear the accept-list and fall to the resources gate");
        result.Error.Should().Contain("resources operation",
            "the operator is sent to the endpoint that validates strictly, not told the name is unknown");

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["logLevel"].Should().Be("info",
            "fail-closed: the legitimate change riding alongside is rejected too, never half-applied");
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_workload_whose_author_declared_a_resources_name_can_still_be_edited()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["logLevel"] = "info",
        });
        var manifest = BuildManifest();
        inputsProvider.Manifest = manifest with
        {
            Parameters = manifest.Parameters.Append(new WorkloadParameter
            {
                Name = "resources.limits.memory",
                Type = WorkloadParameterType.Text,
                Default = "256Mi",
            }).ToArray(),
        };

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?>
            {
                ["logLevel"] = "debug",
                ["resources.limits.memory"] = "256Mi",
            });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["logLevel"].Should().Be("debug");
    }

    [Fact]
    public async Task A_resource_override_cannot_be_set_through_a_raw_parameter_edit()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["logLevel"] = "info",
        });
        var appliedHashBefore = instance.AppliedSpecHash;

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["resources.limits.memory"] = "1e3" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed,
            "a reserved resources.* override must go through the resources endpoint, which gates it strictly");
        result.Error.Should().Contain("resources.limits.memory");

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("resources.limits.memory");
        stored.State.Should().Be(WorkloadInstanceState.Healthy);
        stored.AppliedSpecHash.Should().Be(appliedHashBefore, "no forced re-apply");
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Device_path_outside_dev_prefix_is_validation_failed_and_never_persisted()
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/etc/shadow", ["logLevel"] = "info" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        result.Error.Should().NotBeNullOrWhiteSpace();

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["serialPort"].Should().Be("/dev/ttyUSB0", "a policy-violating edit is fail-closed");
        stored.State.Should().Be(WorkloadInstanceState.Healthy);
        bus.Published.Should().BeEmpty();
    }

    private static WorkloadManifest BuildPlacementManifest() => BuildManifest() with
    {
        Parameters = new[]
        {
            new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath, Required = true },
            new WorkloadParameter { Name = "placement", Type = WorkloadParameterType.NodeSelector },
            new WorkloadParameter { Name = "logLevel", Type = WorkloadParameterType.Text, Default = "info" },
        },
    };

    [Fact]
    public async Task A_quoted_node_selector_value_is_validation_failed_and_never_persisted()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["placement"] = "kubernetes.io/hostname=worker01",
        });
        inputsProvider.Manifest = BuildPlacementManifest();

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?>
            {
                ["serialPort"] = "/dev/ttyUSB0",
                ["placement"] = "intel.feature.node.kubernetes.io/gpu='true'",
            });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        result.Error.Should().Contain("'true'", "the operator must see the value that was refused");

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["placement"].Should().Be("kubernetes.io/hostname=worker01",
            "a placement that could never apply must not replace one that does");
        stored.State.Should().Be(WorkloadInstanceState.Healthy);
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_legal_node_selector_value_is_saved()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["placement"] = "kubernetes.io/hostname=worker01",
        });
        inputsProvider.Manifest = BuildPlacementManifest();

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?>
            {
                ["serialPort"] = "/dev/ttyUSB0",
                ["placement"] = "intel.feature.node.kubernetes.io/gpu=true",
            });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["placement"].Should().Be("intel.feature.node.kubernetes.io/gpu=true");
    }

    [Fact]
    public async Task A_bad_stored_placement_does_not_block_an_unrelated_edit()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["placement"] = "intel.feature.node.kubernetes.io/gpu='true'",
        });
        inputsProvider.Manifest = BuildPlacementManifest();

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        (await repository.GetByIdAsync(instance.Id))!.GetResolvedParameters()["logLevel"].Should().Be("debug");
    }

    [Fact]
    public async Task A_masked_placement_left_untouched_is_not_judged()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["placement"] = "intel.feature.node.kubernetes.io/gpu='true'",
        });
        inputsProvider.Manifest = BuildPlacementManifest();

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?>
            {
                ["serialPort"] = "/dev/ttyUSB0",
                ["placement"] = WorkloadParameterMaskPolicy.MaskSentinel,
                ["logLevel"] = "debug",
            });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
    }

    [Fact]
    public async Task Changing_a_device_path_with_a_matching_fingerprint_succeeds()
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB1", ["logLevel"] = "info" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);

        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["serialPort"].Should().Be("/dev/ttyUSB1");
        stored.State.Should().Be(WorkloadInstanceState.Deploying);
        AssertNudged(instance.Id);
    }

    private static WorkloadManifest BuildAutoGroupManifest() => BuildManifest() with
    {
        Privileges = new WorkloadPrivileges
        {
            Devices = new[] { new WorkloadDevice { Parameter = "serialPort", Group = WorkloadDeviceGroup.Auto } },
        },
    };

    [Fact]
    public async Task Changing_the_bound_device_stamps_the_new_devices_owning_group_id()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["serialPort:gid"] = "993",
            ["logLevel"] = "info",
        });
        inputsProvider.Manifest = BuildAutoGroupManifest();
        inventory.With("/dev/ttyUSB1", 104);

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB1", ["logLevel"] = "info" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["serialPort:gid"].Should().Be("104",
            "the edit path must re-stamp the gid, or the pod keeps the previous device's group");
    }

    [Fact]
    public async Task Changing_the_bound_device_to_an_unknown_path_clears_the_stale_group_id()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["serialPort:gid"] = "993",
            ["logLevel"] = "info",
        });
        inputsProvider.Manifest = BuildAutoGroupManifest();

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB1", ["logLevel"] = "info" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("serialPort:gid");
    }

    [Fact]
    public async Task Posting_the_mask_sentinel_keeps_the_stored_secret()
    {
        var protectedSecret = parameterSecrets.Protect(BuildManifest(),
            new Dictionary<string, string?> { ["apiPassword"] = "s3cret" })["apiPassword"];
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["apiPassword"] = protectedSecret,
            ["logLevel"] = "info",
        });

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "***", ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["apiPassword"].Should().Be(protectedSecret,
            "posting the mask must retain the exact stored ciphertext");
        parameterSecrets.Unprotect(BuildManifest(), stored.GetResolvedParameters())["apiPassword"]
            .Should().Be("s3cret", "posting the mask retains the previously stored secret");
        stored.GetResolvedParameters()["logLevel"].Should().Be("debug");
    }

    [Fact]
    public async Task Posting_a_new_secret_value_replaces_the_stored_secret()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["apiPassword"] = "s3cret",
            ["logLevel"] = "info",
        });

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "n3wpass", ["logLevel"] = "info" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["apiPassword"].Should().StartWith(WorkloadParameterSecretCodec.Prefix)
            .And.NotContain("n3wpass");
        parameterSecrets.Unprotect(BuildManifest(), stored.GetResolvedParameters())["apiPassword"].Should().Be("n3wpass");
    }

    [Fact]
    public async Task A_lost_key_does_not_block_replacing_the_stored_password()
    {
        var otherKeyRing = new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider());
        var badCiphertext = otherKeyRing.Protect(BuildManifest(), new Dictionary<string, string?> { ["apiPassword"] = "old" });
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["apiPassword"] = badCiphertext["apiPassword"],
            ["logLevel"] = "info",
        });

        var result = await EditJudgedAsync(CreateService(), instance.Id,
            new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["apiPassword"] = "replacement", ["logLevel"] = "info" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var saved = (await repository.GetByIdAsync(instance.Id))!.GetResolvedParameters();
        parameterSecrets.Unprotect(BuildManifest(), saved)["apiPassword"].Should().Be("replacement");
    }

    [Fact]
    public async Task Posting_the_mask_sentinel_keeps_the_stored_value_of_a_NON_password_param()
    {
        var instance = SeedHealthy(new Dictionary<string, string?>
        {
            ["serialPort"] = "/dev/ttyUSB0",
            ["logLevel"] = "info",
        });

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?>
            {
                ["serialPort"] = "***",
                ["logLevel"] = "debug",
            });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["serialPort"].Should().Be(
            "/dev/ttyUSB0",
            "re-posting the sentinel means 'unchanged' — never 'set it to ***'");
        stored.GetResolvedParameters()["logLevel"].Should().Be("debug");
    }

    [Fact]
    public async Task Unknown_instance_returns_not_found()
    {
        var result = await EditJudgedAsync(CreateService(), Guid.NewGuid(), new Dictionary<string, string?> { ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.NotFound);
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task No_longer_declared_workload_returns_not_found()
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });
        inputsProvider.Manifest = null;

        var result = await EditJudgedAsync(CreateService(), instance.Id, new Dictionary<string, string?> { ["logLevel"] = "debug" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.NotFound);
        bus.Published.Should().BeEmpty();
    }

    private const string AttachmentName = "iot-macvlan";
    private const string ProductionLanAttachment = "prod-lan-macvlan";

    private static WorkloadManifest BuildAttachableManifest(
        WorkloadKind kind = WorkloadKind.SingleNode, bool ipv6Required = false) => new()
    {
        Id = WorkloadId,
        DisplayName = "Z-Wave controller",
        Image = new WorkloadImage { Ref = "ghcr.io/reeve/zwave", Digest = "sha256:" + new string('a', 64) },
        Kind = kind,
        Privileges = new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "serialPort" } } },
        Network = new WorkloadNetwork
        {
            Ipv6Required = ipv6Required,
            Attachments = new[]
            {
                new WorkloadNetworkAttachmentSlot { Parameter = "net.iot", Description = "The operator's IoT segment." },
            },
        },
        Parameters = new[]
        {
            new WorkloadParameter { Name = "serialPort", Type = WorkloadParameterType.DevicePath, Required = true },
            new WorkloadParameter { Name = "net.iot", Type = WorkloadParameterType.NetworkAttachment },
            new WorkloadParameter { Name = "logLevel", Type = WorkloadParameterType.Text, Default = "info" },
        },
        Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
    };

    private static Dictionary<string, string?> AttachedParameters(string attachment = AttachmentName) => new(StringComparer.Ordinal)
    {
        ["serialPort"] = "/dev/ttyUSB0",
        ["logLevel"] = "info",
        [WorkloadNetworkAttachmentGrammar.NetworkModeParameter] = WorkloadNetworkAttachmentGrammar.OperatorNetworkMode,
        [WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter] = attachment,
    };

    private static Dictionary<string, string?> SwapToProductionLan() => new(StringComparer.Ordinal)
    {
        ["serialPort"] = "/dev/ttyUSB0",
        ["logLevel"] = "info",
        [WorkloadNetworkAttachmentGrammar.NetworkModeParameter] = WorkloadNetworkAttachmentGrammar.OperatorNetworkMode,
        [WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter] = ProductionLanAttachment,
    };

    private async Task<WorkloadParameterEditResult> EditJudgedAsync(
        WorkloadParameterEditService service,
        Guid instanceId,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, WorkloadAttachmentPin>? pins = null)
        => await service.UpdateParametersAsync(
            instanceId, values, Guid.NewGuid(), await JudgedAsync(instanceId, values, pins));

    private async Task<WorkloadWritePrecondition> JudgedAsync(
        Guid instanceId,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, WorkloadAttachmentPin>? pins = null)
    {
        var requirements = new WorkloadRequirementsService(null!, null!, null!, repository, inputsProvider, null!);
        var read = await requirements.ForParameterEditAsync(instanceId, values);
        return new WorkloadWritePrecondition
        {
            RequirementsFingerprint = read.Read?.Fingerprint ?? "unjudged: " + read.Status,
            AttachmentPins = pins ?? new Dictionary<string, WorkloadAttachmentPin>(StringComparer.Ordinal),
        };
    }

    private static WorkloadAttachmentPin PinFor(string attachment) => new()
    {
        Mode = WorkloadNetworkMode.OperatorNetwork,
        AttachmentNames = [attachment],
    };

    private WorkloadInstance SeedAttached()
    {
        var instance = SeedHealthy(AttachedParameters());
        inputsProvider.Manifest = BuildAttachableManifest();
        instance.PinAttachment(PinFor(AttachmentName), Now);
        return instance;
    }

    [Fact]
    public async Task A_fingerprint_that_does_not_match_the_edit_writes_nothing()
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });
        var body = new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB1", ["logLevel"] = "info" };
        var judgedEarlier = await JudgedAsync(instance.Id, new Dictionary<string, string?> { ["logLevel"] = "debug" });

        var result = await CreateService().UpdateParametersAsync(instance.Id, body, Guid.NewGuid(), judgedEarlier with
        {
            AttachmentPins = new Dictionary<string, WorkloadAttachmentPin> { [WorkloadId] = PinFor(AttachmentName) },
        });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.PreconditionFailed);
        result.Error.Should().Contain(WorkloadId);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()["serialPort"].Should().Be("/dev/ttyUSB0");
        stored.AppliedSpecHash.Should().Be("sha256:applied");
        stored.State.Should().Be(WorkloadInstanceState.Healthy);
        stored.ReadAttachmentPin().Verdict.Should().Be(Reeve.Workloads.Abstractions.Domain.Entities.Documents.WorkloadDocumentVerdict.Absent);
        repository.Saves.Should().Be(0);
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fingerprint_that_matches_only_ignoring_case_writes_nothing()
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });
        var body = new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "debug" };
        var judged = await JudgedAsync(instance.Id, body);

        var result = await CreateService().UpdateParametersAsync(instance.Id, body, Guid.NewGuid(), judged with
        {
            RequirementsFingerprint = judged.RequirementsFingerprint.ToUpperInvariant(),
        });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.PreconditionFailed);
        repository.Saves.Should().Be(0);
    }

    [Fact]
    public async Task A_value_that_fails_validation_is_reported_before_the_precondition()
    {
        var instance = SeedHealthy(new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0", ["logLevel"] = "info" });

        var result = await CreateService().UpdateParametersAsync(
            instance.Id,
            new Dictionary<string, string?> { ["serialPort"] = "/etc/shadow" },
            null,
            new WorkloadWritePrecondition { RequirementsFingerprint = "judged-over-something-else" });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.ValidationFailed);
        repository.Saves.Should().Be(0);
    }

    [Fact]
    public async Task An_attachment_swap_with_a_matching_fingerprint_applies_and_records_its_pin_in_the_same_save()
    {
        var instance = SeedAttached();
        var accepted = PinFor(ProductionLanAttachment);

        var result = await EditJudgedAsync(
            CreateService(), instance.Id, SwapToProductionLan(),
            new Dictionary<string, WorkloadAttachmentPin> { [WorkloadId] = accepted });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()[WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter]
            .Should().Be(ProductionLanAttachment);
        stored.ReadAttachmentPin().Value.Should().BeEquivalentTo(accepted);
        stored.AppliedSpecHash.Should().BeNull("the applied hash is cleared so the rollout fires");
        stored.State.Should().Be(WorkloadInstanceState.Deploying);
        repository.Saves.Should().Be(1, "the pin rides the parameter write, not a save of its own");
        AssertNudged(instance.Id);
    }

    [Fact]
    public async Task An_edit_that_names_no_pin_keeps_the_recorded_pin()
    {
        var instance = SeedAttached();
        var body = AttachedParameters();
        body["logLevel"] = "debug";

        var result = await EditJudgedAsync(CreateService(), instance.Id, body);

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.Success);
        (await repository.GetByIdAsync(instance.Id))!.ReadAttachmentPin().Value!.AttachmentNames.Should().Equal(AttachmentName);
    }

    [Fact]
    public async Task A_pin_for_another_workload_writes_nothing()
    {
        var instance = SeedAttached();

        var result = await EditJudgedAsync(
            CreateService(), instance.Id, SwapToProductionLan(),
            new Dictionary<string, WorkloadAttachmentPin>
            {
                [WorkloadId] = PinFor(ProductionLanAttachment),
                ["sibling"] = PinFor(ProductionLanAttachment),
            });

        result.Outcome.Should().Be(WorkloadParameterEditOutcome.PreconditionFailed);
        var stored = await repository.GetByIdAsync(instance.Id);
        stored!.GetResolvedParameters()[WorkloadNetworkAttachmentGrammar.NetworkAttachmentsParameter].Should().Be(AttachmentName);
        stored.ReadAttachmentPin().Value!.AttachmentNames.Should().Equal(AttachmentName);
        repository.Saves.Should().Be(0);
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_save_that_fails_nudges_nothing()
    {
        var instance = SeedAttached();
        var precondition = await JudgedAsync(instance.Id, SwapToProductionLan());

        var act = () => CreateService(new FaultingSaveRepository(repository))
            .UpdateParametersAsync(instance.Id, SwapToProductionLan(), null, precondition);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*induced*");
        bus.Published.Should().BeEmpty();
    }

    private sealed class FaultingSaveRepository(IWorkloadInstanceRepository inner, Exception? failure = null) : IWorkloadInstanceRepository
    {
        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => inner.GetByIdAsync(id, cancellationToken);

        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => throw failure ?? new InvalidOperationException("induced failure at the single SaveChanges boundary");

        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => inner.AddAsync(instance, cancellationToken);

        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(
            Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => inner.ListByPluginInstanceAsync(pluginInstanceId, cancellationToken);

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => inner.ListAllAsync(cancellationToken);

        public Task<WorkloadInstance?> GetByWorkloadAsync(
            Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => inner.GetByWorkloadAsync(pluginInstanceId, workloadId, cancellationToken);

        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(
            WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => inner.ListByStateAsync(state, cancellationToken);

        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(
            CancellationToken cancellationToken = default)
            => inner.CountByStateAsync(cancellationToken);

        public Task<bool> TrySetRestoreBindingUnresolvedAsync(
            Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => inner.TrySetRestoreBindingUnresolvedAsync(workloadInstanceId, nowUtc, cancellationToken);

        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(instance, cancellationToken);
    }

    private sealed class RecordingAuditSink(Action<WorkloadAuditEntry>? onWrite = null) : IWorkloadAuditSink
    {
        public List<WorkloadAuditEntry> Entries { get; } = [];
        public CancellationToken LastCancellationToken { get; private set; }

        public Task WriteAsync(WorkloadAuditEntry entry, CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;
            onWrite?.Invoke(entry);
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class StubIntentProvider : IActiveWorkloadIntentProvider
    {
        public Dictionary<Guid, Guid> ActiveIntentByPlugin { get; } = new();

        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<WorkloadDesiredSpec?>(null);

        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken)
            => Task.FromResult(ActiveIntentByPlugin.TryGetValue(pluginInstanceId, out var id) ? id : (Guid?)null);
    }

    private sealed class StubInputsProvider : IWorkloadReconcileInputsProvider
    {
        public WorkloadManifest? Manifest { get; set; }
        public WorkloadProviderDescriptor Provider { get; set; } = KubernetesProvider.Descriptor;

        public string? Refusal { get; set; }

        public Task<WorkloadReconcileInputsResult> GetInputsAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            if (Manifest is null)
            {
                return ReconcileInputsAnswers.Of(null, Refusal);
            }

            return ReconcileInputsAnswers.Of(new WorkloadReconcileInputs
            {
                Namer = DefaultWorkloadResourceNamer.Instance,
                WorkloadManifest = Manifest,
                ResolvedParameters = instance.GetResolvedParameters(),
                Settings = new WorkloadOrchestratorSettings(),
                Bootstrap = new WorkloadBootstrapContext { InstanceId = instance.Id, SecretName = "reeve-wl-secret" },
                EffectiveProvider = Provider,
                InstallationId = "test-install",
            }, Refusal);
        }
    }

    private sealed class NoopStatusNotifier : IWorkloadStatusRealtimeNotifier
    {
        public Task NotifyStatusChangedAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class InstanceStore : IWorkloadInstanceRepository
    {
        private readonly Dictionary<Guid, WorkloadInstance> byId = [];

        public int Saves { get; private set; }

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(byId.GetValueOrDefault(id));

        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid groupInstanceId, string workloadId, CancellationToken cancellationToken = default)
            => Task.FromResult(byId.Values.FirstOrDefault(
                i => i.GroupInstanceId == groupInstanceId && i.WorkloadId == workloadId));

        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid groupInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(
                byId.Values.Where(i => i.GroupInstanceId == groupInstanceId).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(byId.Values.Where(i => i.State == state).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(byId.Values.ToList());

        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(
                byId.Values.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count()));

        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            byId[instance.Id] = instance;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            Saves++;
            byId[instance.Id] = instance;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            byId.Remove(instance.Id);
            return Task.CompletedTask;
        }
    }
}
