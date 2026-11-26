using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;
using Reeve.Workloads.Providers.Docker;
using Xunit;

using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerConnectSequenceTests
{
    private const string Bridge = "reeve-workloads";
    private const string ContainerId = "c0ffee0000";
    private const string TargetSpecHash = "abc123def456";

    private readonly DockerContainerManager _sut = new();

    [Fact]
    public async Task attachments_are_connected_in_declared_order_after_create_and_before_start()
    {
        var daemon = new FakeDaemon();

        var result = await _sut.CreateAndStartAsync(
            daemon.Client, Parameters(), ["zeta-net", "mid-net", "alpha-net"], CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Container!.IsRunning.Should().BeTrue();

        daemon.Calls.Should().Equal(
            "create",
            "connect:zeta-net",
            "connect:mid-net",
            "connect:alpha-net",
            "start");

        daemon.Connected.Should().Equal(["zeta-net", "mid-net", "alpha-net"],
            "declared order is the connect sequence, and the connect sequence is what decides which endpoint the "
            + "daemon elects as the default gateway — a sorted list would read alpha, mid, zeta");

        daemon.ConnectedContainerIds.Should().OnlyContain(id => id == ContainerId,
            "each attachment is joined to the container that was just created");
    }

    [Fact]
    public async Task an_empty_attachment_list_creates_and_starts_exactly_as_before_and_touches_no_network()
    {
        var daemon = new FakeDaemon();

        var viaInterface = await ((IDockerContainerManager)_sut)
            .CreateAndStartAsync(daemon.Client, Parameters(), CancellationToken.None);
        var viaOverload = await _sut.CreateAndStartAsync(daemon.Client, Parameters(), [], CancellationToken.None);

        viaInterface.Succeeded.Should().BeTrue();
        viaOverload.Succeeded.Should().BeTrue();
        daemon.Calls.Should().Equal("create", "start", "create", "start");
        daemon.Connected.Should().BeEmpty("no attachment ⇒ ConnectNetworkAsync is never called");
    }

    [Fact]
    public async Task a_mid_sequence_connect_failure_removes_the_created_container_and_never_starts_it()
    {
        var daemon = new FakeDaemon
        {
            FailConnectTo = "mid-net",
            ConnectFailure = new HttpRequestException("connection reset by the docker daemon"),
        };

        var result = await _sut.CreateAndStartAsync(
            daemon.Client, Parameters(), ["zeta-net", "mid-net", "alpha-net"], CancellationToken.None);

        result.Succeeded.Should().BeFalse();

        daemon.Calls.Should().Equal(
            "create",
            "connect:zeta-net",
            "connect:mid-net",
            "remove");

        daemon.Calls.Should().NotContain("start",
            "ALL-OR-NOTHING: a partially connected container is NEVER started — starting it is precisely the "
            + "best-effort implementation this case exists to fail");
        daemon.Calls.Should().NotContain("connect:alpha-net",
            "the sequence aborts at the first failure rather than pressing on with the rest of the list");
        daemon.Removed.Should().Equal([ContainerId], "the created container is removed, not stranded");

        result.Container.Should().BeNull(
            "nothing was left behind, so the caller's rollback path has no candidate to tear down — handing back a "
            + "ref to a container that no longer exists would make the rollback capture evidence from a ghost");
    }

    [Fact]
    public async Task a_mid_sequence_connect_failure_is_returned_as_a_retryable_typed_failure()
    {
        var daemon = new FakeDaemon
        {
            FailConnectTo = "iot-macvlan",
            ConnectFailure = new HttpRequestException("the daemon closed the connection"),
        };

        var result = await _sut.CreateAndStartAsync(
            daemon.Client, Parameters(), ["iot-macvlan"], CancellationToken.None);

        result.FailureKind.Should().Be(DockerFailureKind.Unreachable,
            "a connect failure that is not a 404 is RETRYABLE — the shell maps Unreachable to "
            + "ProviderUnreachable and the next reconcile pass re-runs the whole create→connect→start sequence");
        result.FailureKind.Should().NotBe(DockerFailureKind.Rejected,
            "a transient driver refusal must not be classified fatal — that would park the workload permanently");
        result.Message.Should().Contain("iot-macvlan", "the failure names the network it could not join");
    }

    [Fact]
    public async Task a_404_on_the_attachment_network_is_Rejected_fatal_and_names_the_network()
    {
        var daemon = new FakeDaemon
        {
            FailConnectTo = "iot-macvlan",
            ConnectFailure = new DockerApiException(HttpStatusCode.NotFound, "no such network: iot-macvlan"),
        };

        var result = await _sut.CreateAndStartAsync(
            daemon.Client, Parameters(), ["iot-macvlan"], CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(DockerFailureKind.Rejected,
            "a network deleted or re-created out of band is PERMANENT (Reeve never owns it) — retrying it "
            + "forever is the failure mode this classification exists to prevent");
        result.FailureKind.Should().NotBe(DockerFailureKind.Unreachable,
            "classifying the 404 as retryable is the wrong implementation this case is written against");
        result.Message.Should().Contain("iot-macvlan",
            "the operator-facing reason NAMES the network, so the diagnosis does not require reading logs");
        daemon.Calls.Should().NotContain("start");
        daemon.Removed.Should().Equal([ContainerId]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "reeve_default", "network reeve_default is not allowed to accept this container")]
    [InlineData(HttpStatusCode.BadRequest, "host", "container cannot be disconnected from host network or connected to host network")]
    [InlineData(HttpStatusCode.BadRequest, "none", "container cannot be connected to network none while it has other networks")]
    public async Task An_attachment_the_daemon_refuses_to_connect_fails_the_apply_with_the_daemon_reason(
        HttpStatusCode status, string network, string daemonText)
    {
        var daemon = new FakeDaemon
        {
            FailConnectTo = network,
            ConnectFailure = new DockerApiException(status, daemonText),
        };

        var result = await _sut.CreateAndStartAsync(
            daemon.Client, Parameters(), [network], CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(DockerFailureKind.Rejected,
            "a refusal is permanent: Unreachable would retry it forever and leave the instance in Deploying");
        result.Message.Should().Contain(daemonText).And.Contain(network);
        daemon.Calls.Should().NotContain("start");
        daemon.Removed.Should().Equal([ContainerId]);
    }

    [Fact]
    public async Task A_daemon_server_error_on_connect_stays_retryable()
    {
        var daemon = new FakeDaemon
        {
            FailConnectTo = "iot-macvlan",
            ConnectFailure = new DockerApiException(HttpStatusCode.InternalServerError, "daemon busy"),
        };

        var result = await _sut.CreateAndStartAsync(
            daemon.Client, Parameters(), ["iot-macvlan"], CancellationToken.None);

        result.FailureKind.Should().Be(DockerFailureKind.Unreachable);
    }

    [Theory]
    [InlineData("409")]
    [InlineData("message")]
    public async Task an_already_connected_409_is_success_so_a_retry_converges(string shape)
    {
        var daemon = new FakeDaemon
        {
            FailConnectTo = "iot-macvlan",
            ConnectFailure = shape == "409"
                ? new DockerApiException(HttpStatusCode.Conflict, "endpoint with name x already exists in network")
                : new DockerApiException(
                    HttpStatusCode.InternalServerError,
                    "endpoint already exists in network iot-macvlan"),
        };

        var result = await _sut.CreateAndStartAsync(
            daemon.Client, Parameters(), ["iot-macvlan", "lab-ipvlan"], CancellationToken.None);

        result.Succeeded.Should().BeTrue(
            "ConnectNetworkAsync is treated as IDEMPOTENT — an already-connected endpoint is the desired "
            + "state, so a retry converges instead of removing a container that was actually fine");
        result.Container!.IsRunning.Should().BeTrue();
        daemon.Calls.Should().Equal("create", "connect:iot-macvlan", "connect:lab-ipvlan", "start");
        daemon.Removed.Should().BeEmpty("nothing failed, so nothing is torn down");
    }

    [Fact]
    public async Task a_blank_attachment_name_is_refused_rather_than_connected_to_nothing()
    {
        var daemon = new FakeDaemon();

        var result = await _sut.CreateAndStartAsync(
            daemon.Client, Parameters(), ["iot-macvlan", "   "], CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(DockerFailureKind.Rejected);
        daemon.Calls.Should().NotContain("start",
            "silently skipping a blank name would start a container the operator believes is attached");
        daemon.Removed.Should().Equal([ContainerId]);
    }

    [Fact]
    public void the_recovery_table_answers_both_all_or_nothing_outcomes()
    {
        DockerRecoveryDecisionTable
            .Decide([], TargetSpecHash)
            .Action.Should().Be(DockerRecoveryAction.CreateFresh,
                "the removed candidate leaves no trace, so the next pass simply creates + connects + starts again — "
                + "a retryable failure with a defined resumption point, not a wedge");

        DockerRecoveryDecisionTable
            .Decide([Ref("old", "oldhash1111", running: true)], TargetSpecHash)
            .Action.Should().Be(DockerRecoveryAction.RunFullSwap,
                "with the previous still live the next pass re-runs the full swap");

        DockerRecoveryDecisionTable
            .Decide([Ref("target", TargetSpecHash, running: false)], TargetSpecHash)
            .Action.Should().Be(DockerRecoveryAction.ResumeStartNew,
                "ResumeStartNew is StartGateExistingAsync — the branch that idempotently re-connects the desired "
                + "attachments BEFORE starting; without that reconcile this cell is how an unattached container "
                + "becomes permanently 'converged'");
    }

    [Fact]
    public async Task the_start_gate_reconciles_a_container_created_without_attachments_before_starting_it()
    {
        var daemon = new FakeDaemon { Observed = [Listed("wl-matter", TargetSpecHash, state: "created")] };
        var recreate = new DockerJournaledRecreate(_sut);

        var result = await recreate.RecreateAsync(
            daemon.Client,
            Spec(["iot-macvlan", "lab-ipvlan"]),
            Ref("wl-matter", TargetSpecHash, running: false),
            Parameters(),
            Substitute.For<IDockerRollbackCoordinator>(),
            CancellationToken.None);

        result.Reason.Should().Be(WorkloadApplyReason.Applied);
        result.Succeeded.Should().BeTrue();

        daemon.Connected.Should().Equal(["iot-macvlan", "lab-ipvlan"],
            "the container was created on an earlier pass with NO attachment; the resume leg reconciles the desired "
            + "list (idempotent connect) rather than inheriting whatever the crashed pass managed to apply");

        daemon.Calls.IndexOf("connect:lab-ipvlan").Should().BeLessThan(daemon.Calls.IndexOf("start"),
            "the reconcile happens BEFORE the start — a container started first would run unattached for the rest of "
            + "its life while its correct SpecHash label made drift report it converged");
    }

    [Fact]
    public async Task the_start_gate_reconcile_converges_when_the_container_is_already_connected()
    {
        var daemon = new FakeDaemon
        {
            Observed = [Listed("wl-matter", TargetSpecHash, state: "created")],
            FailConnectTo = "iot-macvlan",
            ConnectFailure = new DockerApiException(HttpStatusCode.Conflict, "endpoint already exists"),
        };
        var recreate = new DockerJournaledRecreate(_sut);

        var result = await recreate.RecreateAsync(
            daemon.Client,
            Spec(["iot-macvlan"]),
            Ref("wl-matter", TargetSpecHash, running: false),
            Parameters(),
            Substitute.For<IDockerRollbackCoordinator>(),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue("409 is success, so a resumed pass converges");
        daemon.Calls.Should().Contain("start");
    }

    [Fact]
    public async Task the_start_gate_refuses_to_start_when_the_attachment_reconcile_cannot_converge()
    {
        var daemon = new FakeDaemon
        {
            Observed = [Listed("wl-matter", TargetSpecHash, state: "created")],
            FailConnectTo = "iot-macvlan",
            ConnectFailure = new DockerApiException(HttpStatusCode.NotFound, "no such network: iot-macvlan"),
        };
        var rollback = Substitute.For<IDockerRollbackCoordinator>();
        rollback.RollbackAsync(default!, default!, default!, default, default)
            .ReturnsForAnyArgs(new DockerRollbackResult
            {
                PreviousRestored = false,
                Message = "rolled back the failed candidate; the previous container could not be restored.",
            });
        var recreate = new DockerJournaledRecreate(_sut);

        var result = await recreate.RecreateAsync(
            daemon.Client,
            Spec(["iot-macvlan"]),
            Ref("wl-matter", TargetSpecHash, running: false),
            Parameters(),
            rollback,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(WorkloadApplyReason.Rejected, "the 404 classification survives the recreate seam");
        result.Message.Should().Contain("iot-macvlan");
        result.Message.Should().Contain("the previous container could not be restored",
            "the rollback's outcome reaches the operator in the apply message");
        daemon.Calls.Should().NotContain("start",
            "the same all-or-nothing rule holds on the resume leg: a container whose attachments cannot be converged "
            + "is never started");
    }

    [Fact]
    public async Task the_recreate_create_path_connects_the_attachments_between_create_and_start()
    {
        var daemon = new FakeDaemon();
        var recreate = new DockerJournaledRecreate(_sut);

        var result = await recreate.RecreateAsync(
            daemon.Client,
            Spec(["iot-macvlan", "lab-ipvlan"]),
            Ref("wl-matter", "stale0000", running: true),
            Parameters(),
            Substitute.For<IDockerRollbackCoordinator>(),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        daemon.Calls.Should().ContainInOrder("create", "connect:iot-macvlan", "connect:lab-ipvlan", "start");
    }

    [Fact]
    public async Task the_fresh_create_path_in_the_provider_connects_the_attachments_between_create_and_start()
    {
        var daemon = new FakeDaemon();
        var provider = BuildProvider(daemon, _sut);

        var result = await provider.ApplyAsync(Spec(["iot-macvlan", "lab-ipvlan"]), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        daemon.Calls.Should().ContainInOrder("create", "connect:iot-macvlan", "connect:lab-ipvlan", "start");
        daemon.Connected.Should().Equal(["iot-macvlan", "lab-ipvlan"]);
    }

    [Fact]
    public async Task a_container_manager_that_cannot_connect_attachments_refuses_rather_than_starting_it_unattached()
    {
        var daemon = new FakeDaemon();
        var incapable = new AttachmentBlindContainerManager(_sut);

        var refused = await DockerAttachmentSequence.CreateConnectAndStartAsync(
            incapable, daemon.Client, Parameters(), ["iot-macvlan"], CancellationToken.None);

        refused.Succeeded.Should().BeFalse();
        refused.FailureKind.Should().Be(DockerFailureKind.Rejected);
        refused.Message.Should().Contain("iot-macvlan");
        daemon.Calls.Should().BeEmpty("nothing is created, so nothing can run unattached");

        var reconcileRefused = await DockerAttachmentSequence.ReconcileAttachmentsAsync(
            incapable, daemon.Client, ContainerId, ["iot-macvlan"], CancellationToken.None);
        reconcileRefused.Succeeded.Should().BeFalse();
        reconcileRefused.FailureKind.Should().Be(DockerFailureKind.Rejected);

        var allowed = await DockerAttachmentSequence.CreateConnectAndStartAsync(
            incapable, daemon.Client, Parameters(), [], CancellationToken.None);
        allowed.Succeeded.Should().BeTrue();
        daemon.Calls.Should().Equal("create", "start");
    }

    private static WorkloadDesiredSpec CredentialSpec() => Spec([], WorkloadNetworkMode.Cluster) with
    {
        Env =
        [
            new ResolvedEnvVar { Name = "WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "wl-matter-secret", CredentialId = "tok" },
            new ResolvedEnvVar { Name = "FOO_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "wl-matter-secret", CredentialId = "foo" },
            new ResolvedEnvVar { Name = "LOG_LEVEL", Kind = ResolvedEnvKind.Literal, Value = "info" },
        ],
        ProvisionedCredentials = [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")],
    };

    [Fact]
    public async Task a_benign_reapply_reads_each_credential_back_and_asks_once_per_id_so_nothing_rotates()
    {
        var daemon = new FakeDaemon
        {
            Observed = [Listed("wl-matter", "stale0000", "running")],
            HeldEnv = ["REEVE_WORKLOAD_TOKEN=held-tok", "WORKLOAD_TOKEN=held-tok", "FOO_TOKEN=held-foo", "LOG_LEVEL=info"],
        };
        var port = new RecordingCredentialPort { Live = { "held-tok", "held-foo" } };
        var recreate = new CapturingRecreate();
        var provider = BuildProvider(daemon, _sut, port, recreate);

        var result = await provider.ApplyAsync(CredentialSpec(), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        port.Asks.Should().Equal((InstanceId, "tok", "held-tok"), (InstanceId, "foo", "held-foo"));
        port.Minted.Should().BeEmpty("the live values were handed back, so the host had nothing to mint");
        recreate.Desired!.Env.Should().Contain(["REEVE_WORKLOAD_TOKEN=held-tok", "WORKLOAD_TOKEN=held-tok", "FOO_TOKEN=held-foo"]);
    }

    [Fact]
    public async Task a_reapply_after_the_held_credential_was_revoked_puts_the_fresh_one_in_a_new_container()
    {
        var deployed = await DeployedWith(new RecordingCredentialPort { Answer = (_, id) => $"first-{id}" });
        var daemon = new FakeDaemon { Observed = [AsListed(deployed)], HeldEnv = [.. deployed.Env] };
        var port = new RecordingCredentialPort();
        var provider = BuildProvider(daemon, _sut, port, new DockerJournaledRecreate(_sut));

        var result = await provider.ApplyAsync(CredentialSpec(), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        port.Minted.Should().HaveCount(2);
        daemon.Created.Should().ContainSingle("a fresh credential can only reach a container that is created with it")
            .Which.Env.Should().Contain([$"REEVE_WORKLOAD_TOKEN={port.Minted[0]}", $"FOO_TOKEN={port.Minted[1]}"]);
    }

    [Fact]
    public async Task a_reapply_whose_held_credentials_are_still_valid_creates_nothing()
    {
        var deployed = await DeployedWith(new RecordingCredentialPort { Answer = (_, id) => $"first-{id}" });
        var daemon = new FakeDaemon { Observed = [AsListed(deployed)], HeldEnv = [.. deployed.Env] };
        var port = new RecordingCredentialPort { Live = { "first-tok", "first-foo" } };
        var provider = BuildProvider(daemon, _sut, port, new DockerJournaledRecreate(_sut));

        var result = await provider.ApplyAsync(CredentialSpec(), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        daemon.Created.Should().BeEmpty("the live container already carries the credentials the host accepts");
    }

    [Fact]
    public async Task a_container_built_before_credentials_were_digested_is_recreated_when_its_held_credential_was_revoked()
    {
        var deployed = await DeployedWith(new RecordingCredentialPort { Answer = (_, id) => $"first-{id}" });
        var listed = AsListed(deployed);
        listed.Labels.Remove(DockerProviderConstants.CredentialDigestLabelKey);
        var daemon = new FakeDaemon { Observed = [listed], HeldEnv = [.. deployed.Env] };
        var provider = BuildProvider(daemon, _sut, new RecordingCredentialPort(), new DockerJournaledRecreate(_sut));

        (await provider.ApplyAsync(CredentialSpec(), progress: null)).Succeeded.Should().BeTrue();

        daemon.Created.Should().ContainSingle("a container held at upgrade carries a revoked credential and no label to show it");
    }

    [Fact]
    public async Task a_host_that_issues_no_credential_keeps_its_container()
    {
        var none = new RecordingCredentialPort { Answer = (_, _) => null };
        var deployed = await DeployedWith(none);
        var daemon = new FakeDaemon { Observed = [AsListed(deployed)], HeldEnv = [.. deployed.Env] };
        var provider = BuildProvider(daemon, _sut, none, new DockerJournaledRecreate(_sut));

        (await provider.ApplyAsync(CredentialSpec(), progress: null)).Succeeded.Should().BeTrue();

        daemon.Created.Should().BeEmpty("with nothing issued there is nothing to re-mint");
    }

    private async Task<CreateContainerParameters> DeployedWith(RecordingCredentialPort port)
    {
        var daemon = new FakeDaemon();
        (await BuildProvider(daemon, _sut, port).ApplyAsync(CredentialSpec(), progress: null)).Succeeded.Should().BeTrue();
        return daemon.Created.Single();
    }

    private static ContainerListResponse AsListed(CreateContainerParameters created) => new()
    {
        ID = ContainerId,
        Names = [$"/{created.Name}"],
        State = "running",
        Labels = new Dictionary<string, string>(created.Labels, StringComparer.Ordinal),
    };

    [Fact]
    public async Task a_first_deploy_mints_each_credential_once_and_writes_it_under_every_name_carrying_its_id()
    {
        var daemon = new FakeDaemon();
        var port = new RecordingCredentialPort();
        var provider = BuildProvider(daemon, _sut, port);

        var result = await provider.ApplyAsync(CredentialSpec(), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        port.Asks.Should().Equal((InstanceId, "tok", null), (InstanceId, "foo", null));
        daemon.Created.Single().Env.Should().Contain(
            ["REEVE_WORKLOAD_TOKEN=minted-tok-1", "WORKLOAD_TOKEN=minted-tok-1", "FOO_TOKEN=minted-foo-2"]);
    }

    [Fact]
    public async Task an_env_flagged_out_of_the_hash_reaches_the_container_like_any_literal()
    {
        var daemon = new FakeDaemon();
        var provider = BuildProvider(daemon, _sut, new RecordingCredentialPort());
        var spec = Spec([], WorkloadNetworkMode.Cluster) with
        {
            Env =
            [
                new ResolvedEnvVar { Name = "EXCLUDED", Kind = ResolvedEnvKind.Literal, Value = "current", ExcludedFromSpecHash = true },
            ],
        };

        var result = await provider.ApplyAsync(spec, progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        daemon.Created.Single().Env.Should().Contain("EXCLUDED=current");
    }

    [Fact]
    public async Task a_reapply_with_only_an_unhashed_value_changed_puts_the_new_value_in_a_container()
    {
        var live = Listed("wl-matter", TargetSpecHash, "running");
        live.Labels[DockerProviderConstants.UnhashedEnvDigestLabelKey] = DockerProviderConstants.UnhashedEnvDigest(
            [new ResolvedEnvVar { Name = "EXCLUDED", Kind = ResolvedEnvKind.Literal, Value = "old", ExcludedFromSpecHash = true }])!;
        var daemon = new FakeDaemon { Observed = [live], HeldEnv = ["EXCLUDED=old"] };
        var provider = BuildProvider(daemon, _sut, new RecordingCredentialPort(), new DockerJournaledRecreate(_sut));

        var result = await provider.ApplyAsync(UnhashedSpec("new"), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        daemon.Created.Should().ContainSingle("the new value can only reach a container that is created with it")
            .Which.Env.Should().Contain("EXCLUDED=new");
    }

    [Fact]
    public async Task a_reapply_with_the_unhashed_value_unchanged_creates_nothing()
    {
        var live = Listed("wl-matter", TargetSpecHash, "running");
        live.Labels[DockerProviderConstants.UnhashedEnvDigestLabelKey] = DockerProviderConstants.UnhashedEnvDigest(
            [new ResolvedEnvVar { Name = "EXCLUDED", Kind = ResolvedEnvKind.Literal, Value = "same", ExcludedFromSpecHash = true }])!;
        var daemon = new FakeDaemon { Observed = [live], HeldEnv = ["EXCLUDED=same"] };
        var provider = BuildProvider(daemon, _sut, new RecordingCredentialPort(), new DockerJournaledRecreate(_sut));

        var result = await provider.ApplyAsync(UnhashedSpec("same"), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        daemon.Created.Should().BeEmpty("a container already built with this value is the target");
    }

    [Fact]
    public async Task a_container_built_before_any_unhashed_env_existed_is_the_target_of_a_spec_with_none()
    {
        var daemon = new FakeDaemon { Observed = [Listed("wl-matter", TargetSpecHash, "running")] };
        var provider = BuildProvider(daemon, _sut, new RecordingCredentialPort(), new DockerJournaledRecreate(_sut));

        var result = await provider.ApplyAsync(Spec([], WorkloadNetworkMode.Cluster), progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        daemon.Created.Should().BeEmpty();
    }

    [Fact]
    public void the_container_labels_never_carry_an_unhashed_value()
    {
        var labels = DockerProviderConstants.BuildResourceLabels(UnhashedSpec("hunter2-value"));

        labels.Values.Should().NotContain(v => v.Contains("hunter2-value", StringComparison.Ordinal));
        labels.Should().ContainKey(DockerProviderConstants.UnhashedEnvDigestLabelKey);
    }

    private static WorkloadDesiredSpec UnhashedSpec(string value) => Spec([], WorkloadNetworkMode.Cluster) with
    {
        Env = [new ResolvedEnvVar { Name = "EXCLUDED", Kind = ResolvedEnvKind.Literal, Value = value, ExcludedFromSpecHash = true }],
    };

    [Fact]
    public async Task the_provisioned_name_answers_before_a_declared_env_carrying_the_same_id()
    {
        var daemon = new FakeDaemon
        {
            Observed = [Listed("wl-matter", "stale0000", "running")],
            HeldEnv = ["WORKLOAD_TOKEN=from-declared", "REEVE_WORKLOAD_TOKEN=from-provisioned", "FOO_TOKEN=held-foo"],
        };
        var port = new RecordingCredentialPort();
        var provider = BuildProvider(daemon, _sut, port, new CapturingRecreate());

        await provider.ApplyAsync(CredentialSpec(), progress: null);

        port.Asks.Should().Contain((InstanceId, "tok", "from-provisioned"));
    }

    [Fact]
    public async Task a_credential_only_declared_is_read_back_under_its_declared_env()
    {
        var daemon = new FakeDaemon
        {
            Observed = [Listed("wl-matter", "stale0000", "running")],
            HeldEnv = ["FOO_TOKEN=held-foo"],
        };
        var port = new RecordingCredentialPort();
        var provider = BuildProvider(daemon, _sut, port, new CapturingRecreate());

        await provider.ApplyAsync(CredentialSpec() with { ProvisionedCredentials = [] }, progress: null);

        port.Asks.Should().Contain((InstanceId, "foo", "held-foo"));
    }

    [Fact]
    public async Task a_host_that_provisions_nothing_is_asked_nothing_and_gets_no_token_env()
    {
        var daemon = new FakeDaemon();
        var port = new RecordingCredentialPort();
        var provider = BuildProvider(daemon, _sut, port);
        var spec = Spec([], WorkloadNetworkMode.Cluster);

        var result = await provider.ApplyAsync(spec, progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        port.Asks.Should().BeEmpty();
        daemon.Created.Single().Env.Should().NotContain(e => e.StartsWith("REEVE_WORKLOAD_TOKEN=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task a_provisioned_credential_rides_the_container_env_but_never_the_spec_hash()
    {
        var daemon = new FakeDaemon();
        var provider = BuildProvider(daemon, _sut, new RecordingCredentialPort());
        var spec = CredentialSpec();

        await provider.ApplyAsync(spec, progress: null);

        daemon.Created.Single().Env.Should().Contain("REEVE_WORKLOAD_TOKEN=minted-tok-1");
        SpecHasher.Compute(spec).Should().Be(SpecHasher.Compute(spec with
        {
            ProvisionedCredentials = [],
            Env = [.. spec.Env.Select(e => e with { CredentialId = null })],
        }));
    }

    [Fact]
    public async Task a_declared_credential_under_a_name_of_its_own_applies()
    {
        var daemon = new FakeDaemon();
        var provider = BuildProvider(daemon, _sut, new RecordingCredentialPort());

        var result = await provider.ApplyAsync(CredentialSpec() with { ProvisionedCredentials = [] }, progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        daemon.Created.Single().Env.Should().Contain("FOO_TOKEN=minted-foo-2");
    }

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void the_desired_attachment_list_equals_the_networking_plans_attachment_half(
        WorkloadNetworkMode mode, string[] attachments)
    {
        var spec = Spec(attachments, mode);

        var planned = new DockerNetworkManager().RenderNetworking(spec, Bridge).Attachments;
        var desired = DockerAttachmentSequence.DesiredAttachments(spec);

        desired.Should().Equal(planned,
            "the renderer is the producer of the attachment list; the recreate seam must re-read the SAME value in "
            + "the SAME order, not a sorted or de-duplicated re-derivation");
    }

    public static TheoryData<WorkloadNetworkMode, string[]> ParityCases() => new()
    {
        { WorkloadNetworkMode.OperatorNetwork, ["zeta-net", "mid-net", "alpha-net"] },
        { WorkloadNetworkMode.OperatorNetwork, ["iot-macvlan"] },
        { WorkloadNetworkMode.Cluster, [] },
        { WorkloadNetworkMode.HostNetwork, [] },
    };

    private static CreateContainerParameters Parameters() => new()
    {
        Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
        Name = "wl-matter",
        Labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DockerProviderConstants.SpecHashLabelKey] = TargetSpecHash,
        },
        HostConfig = new HostConfig { NetworkMode = Bridge },
    };

    private static DockerContainerRef Ref(string name, string specHash, bool running) => new()
    {
        Id = ContainerId,
        Name = name,
        SpecHash = specHash,
        RestartNonce = null,
        IsRunning = running,
    };

    private static ContainerListResponse Listed(string name, string specHash, string state) => new()
    {
        ID = ContainerId,
        Names = [$"/{name}"],
        State = state,
        Labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DockerProviderConstants.InstanceLabelKey] = InstanceId.ToString("D"),
            [DockerProviderConstants.SpecHashLabelKey] = specHash,
        },
    };

    [Fact]
    public async Task a_held_empty_value_counts_as_none_so_the_next_owned_name_answers()
    {
        var daemon = new FakeDaemon
        {
            Observed = [Listed("wl-matter", "stale0000", "running")],
            HeldEnv = ["REEVE_WORKLOAD_TOKEN=", "WORKLOAD_TOKEN=held-tok", "FOO_TOKEN="],
        };
        var port = new RecordingCredentialPort();
        var provider = BuildProvider(daemon, _sut, port, new CapturingRecreate());

        await provider.ApplyAsync(CredentialSpec(), progress: null);

        port.Asks.Should().Equal((InstanceId, "tok", "held-tok"), (InstanceId, "foo", null));
    }

    [Fact]
    public async Task a_declared_literal_keeps_a_provisioned_name_and_the_credential_left_with_no_name_is_not_asked()
    {
        var daemon = new FakeDaemon
        {
            Observed = [Listed("wl-matter", "stale0000", "running")],
            HeldEnv = ["REEVE_WORKLOAD_TOKEN=declared"],
        };
        var port = new RecordingCredentialPort();
        var recreate = new CapturingRecreate();
        var provider = BuildProvider(daemon, _sut, port, recreate);
        var spec = Spec([], WorkloadNetworkMode.Cluster) with
        {
            Env = [new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.Literal, Value = "declared" }],
            ProvisionedCredentials = [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")],
        };

        var result = await provider.ApplyAsync(spec, progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        port.Asks.Should().BeEmpty("the declared env owns the only name the credential had");
        recreate.Desired!.Env.Where(e => e.StartsWith("REEVE_WORKLOAD_TOKEN=", StringComparison.Ordinal))
            .Should().Equal("REEVE_WORKLOAD_TOKEN=declared");
    }

    [Fact]
    public async Task a_declared_literal_under_a_provisioned_name_is_never_handed_to_the_port_as_existing()
    {
        var daemon = new FakeDaemon
        {
            Observed = [Listed("wl-matter", "stale0000", "running")],
            HeldEnv = ["REEVE_WORKLOAD_TOKEN=declared"],
        };
        var port = new RecordingCredentialPort();
        var recreate = new CapturingRecreate();
        var provider = BuildProvider(daemon, _sut, port, recreate);
        var spec = CredentialSpec() with
        {
            Env = [.. CredentialSpec().Env, new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.Literal, Value = "declared" }],
        };

        await provider.ApplyAsync(spec, progress: null);

        port.Asks.Should().Equal((InstanceId, "tok", null), (InstanceId, "foo", null));
        recreate.Desired!.Env.Should().Contain(["REEVE_WORKLOAD_TOKEN=declared", "WORKLOAD_TOKEN=minted-tok-1"]);
        recreate.Desired.Env.Should().ContainSingle(e => e.StartsWith("REEVE_WORKLOAD_TOKEN=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task secret_material_under_a_credential_name_wins_and_is_never_handed_to_the_port_as_existing()
    {
        var daemon = new FakeDaemon
        {
            Observed = [Listed("wl-matter", "stale0000", "running")],
            HeldEnv = ["WORKLOAD_TOKEN=from-material", "FOO_TOKEN=held-foo"],
        };
        var port = new RecordingCredentialPort();
        var recreate = new CapturingRecreate();
        var provider = BuildProvider(daemon, _sut, port, recreate);
        var spec = CredentialSpec() with
        {
            SecretMaterial = new Dictionary<string, string>(StringComparer.Ordinal) { ["WORKLOAD_TOKEN"] = "from-material" },
        };

        await provider.ApplyAsync(spec, progress: null);

        port.Asks.Should().Equal((InstanceId, "tok", null), (InstanceId, "foo", "held-foo"));
        recreate.Desired!.Env.Should().Contain(["WORKLOAD_TOKEN=from-material", "REEVE_WORKLOAD_TOKEN=minted-tok-1"]);
    }

    [Fact]
    public async Task two_credentials_on_one_name_are_refused_before_either_is_asked_for()
    {
        var daemon = new FakeDaemon();
        var port = new RecordingCredentialPort();
        var provider = BuildProvider(daemon, _sut, port);
        var spec = CredentialSpec() with
        {
            ProvisionedCredentials = [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN"), new ProvisionedCredential("foo", "WORKLOAD_TOKEN")],
        };

        var result = await provider.ApplyAsync(spec, progress: null);

        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("WORKLOAD_TOKEN");
        port.Asks.Should().BeEmpty();
        daemon.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task a_secret_reference_nothing_backs_is_refused_before_any_credential_is_asked_for()
    {
        var daemon = new FakeDaemon();
        var port = new RecordingCredentialPort();
        var provider = BuildProvider(daemon, _sut, port);
        var spec = CredentialSpec() with
        {
            Env = [.. CredentialSpec().Env, new ResolvedEnvVar { Name = "DB_PASSWORD", Kind = ResolvedEnvKind.SecretRef, SecretRef = "wl-matter-secret" }],
        };

        var result = await provider.ApplyAsync(spec, progress: null);

        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("DB_PASSWORD");
        port.Asks.Should().BeEmpty();
        daemon.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Reeves_own_shape_asks_once_with_the_held_value_and_writes_one_entry()
    {
        var daemon = new FakeDaemon
        {
            Observed = [Listed("wl-matter", "stale0000", "running")],
            HeldEnv = ["REEVE_WORKLOAD_TOKEN=held-tok"],
        };
        var port = new RecordingCredentialPort { Live = { "held-tok" } };
        var recreate = new CapturingRecreate();
        var provider = BuildProvider(daemon, _sut, port, recreate);
        var spec = Spec([], WorkloadNetworkMode.Cluster) with
        {
            Env = [new ResolvedEnvVar { Name = "REEVE_WORKLOAD_TOKEN", Kind = ResolvedEnvKind.SecretRef, SecretRef = "wl-matter-secret", CredentialId = "tok" }],
            ProvisionedCredentials = [new ProvisionedCredential("tok", "REEVE_WORKLOAD_TOKEN")],
        };

        var result = await provider.ApplyAsync(spec, progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        port.Asks.Should().Equal((InstanceId, "tok", "held-tok"));
        recreate.Desired!.Env.Where(e => e.StartsWith("REEVE_WORKLOAD_TOKEN=", StringComparison.Ordinal))
            .Should().Equal("REEVE_WORKLOAD_TOKEN=held-tok");
    }

    [Fact]
    public async Task A_literal_holding_an_id_is_never_read_back_or_handed_to_the_port()
    {
        var daemon = new FakeDaemon
        {
            Observed = [Listed("wl-matter", "stale0000", "running")],
            HeldEnv = ["FEED_URL=v"],
        };
        var port = new RecordingCredentialPort();
        var recreate = new CapturingRecreate();
        var provider = BuildProvider(daemon, _sut, port, recreate);
        var spec = Spec([], WorkloadNetworkMode.Cluster) with
        {
            Env = [new ResolvedEnvVar { Name = "FEED_URL", Kind = ResolvedEnvKind.Literal, Value = "v", CredentialId = "feed" }],
        };

        var result = await provider.ApplyAsync(spec, progress: null);

        result.Succeeded.Should().BeTrue(result.Message);
        port.Asks.Should().BeEmpty();
        recreate.Desired!.Env.Where(e => e.StartsWith("FEED_URL=", StringComparison.Ordinal)).Should().Equal("FEED_URL=v");
    }

    [Fact]
    public async Task A_credential_entry_whose_name_a_literal_also_fills_is_refused_before_any_ask()
    {
        var daemon = new FakeDaemon();
        var port = new RecordingCredentialPort();
        var provider = BuildProvider(daemon, _sut, port);
        var spec = CredentialSpec() with
        {
            Env =
            [
                .. CredentialSpec().Env,
                new ResolvedEnvVar { Name = "FEED_URL", Kind = ResolvedEnvKind.Literal, Value = "declared" },
                new ResolvedEnvVar { Name = "FEED_URL", Kind = ResolvedEnvKind.SecretRef, SecretRef = "wl-matter-secret", CredentialId = "tok" },
            ],
        };

        var result = await provider.ApplyAsync(spec, progress: null);

        result.Reason.Should().Be(WorkloadApplyReason.Rejected);
        result.Message.Should().Contain("FEED_URL");
        port.Asks.Should().BeEmpty();
        daemon.Created.Should().BeEmpty();
    }

    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static WorkloadDesiredSpec Spec(
        IReadOnlyList<string> attachments,
        WorkloadNetworkMode mode = WorkloadNetworkMode.OperatorNetwork) => new()
        {
            GroupInstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            GroupId = "com.reeve.matter",
            WorkloadId = "matter",
            InstanceId = InstanceId,
            Inst8 = "11111111",
            Provider = DockerProvider.Id,
            ResourceName = "wl-matter",
            SecretName = "wl-matter-secret",
            Labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                     {
                         InstanceId = InstanceId,
                         InstallationId = "install-1",
                         SpecHash = TargetSpecHash,
                     }),
            Image = "ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Kind = WorkloadKind.SingleNode,
            Network = new ResolvedNetwork
            {
                HostNetwork = mode == WorkloadNetworkMode.HostNetwork,
                Mode = mode,
                Attachments = attachments,
            },
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = TargetSpecHash,
        };

    private static DockerWorkloadProvider BuildProvider(
        FakeDaemon daemon,
        IDockerContainerManager containers,
        IWorkloadCredentialPort? credentialPort = null,
        IDockerJournaledRecreate? recreate = null)
    {
        var settingsService = Substitute.For<IWorkloadOrchestratorSecretReader>();
        settingsService.GetDecryptedSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkloadOrchestratorSettings()));

        var credentials = credentialPort ?? Substitute.For<IWorkloadCredentialPort>();
        if (credentialPort is null)
        {
            credentials.ResolveOrMintAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<string?>("rwt_test"));
        }

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IWorkloadOrchestratorSecretReader)).Returns(settingsService);
        serviceProvider.GetService(typeof(IWorkloadCredentialPort)).Returns(credentials);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var clientFactory = Substitute.For<IDockerClientFactory>();
        clientFactory.CreateClient(Arg.Any<string>()).Returns(daemon.Client);

        var validator = Substitute.For<IDockerDeploymentValidator>();
        validator.Validate(Arg.Any<WorkloadDesiredSpec>()).Returns(Array.Empty<PreflightCheck>());

        var gate = Substitute.For<IDockerDaemonGate>();
        gate.EvaluateAsync(Arg.Any<IDockerClient>(), Arg.Any<WorkloadDesiredSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PreflightCheck>>(Array.Empty<PreflightCheck>()));

        var puller = Substitute.For<IDockerImagePuller>();
        puller.PullAsync(
                Arg.Any<IDockerClient>(), Arg.Any<string>(), Arg.Any<DockerProviderSettings>(),
                Arg.Any<IProgress<DockerPullProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerOperationResult.Ok()));

        var real = new DockerNetworkManager();
        var networks = Substitute.For<IDockerNetworkManager>();
        networks.EnsureSharedNetworkAsync(
                Arg.Any<IDockerClient>(), Arg.Any<DockerProviderSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerNetworkEnsureResult.Ok(Bridge)));
        networks.RenderNetworking(Arg.Any<WorkloadDesiredSpec>(), Arg.Any<string>())
            .Returns(call => real.RenderNetworking(call.Arg<WorkloadDesiredSpec>(), call.ArgAt<string>(1)));

        var volumes = Substitute.For<IDockerVolumeManager>();
        volumes.EnsureVolumesAsync(
                Arg.Any<IDockerClient>(), Arg.Any<WorkloadDesiredSpec>(), Arg.Any<DockerProviderSettings>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DockerOperationResult.Ok()));

        var privilege = Substitute.For<IDockerPrivilegeRenderer>();
        privilege.RenderPrivileges(Arg.Any<WorkloadDesiredSpec>())
            .Returns(new DockerPrivilegeRendering { HostConfig = new HostConfig() });

        var resources = Substitute.For<IDockerResourceRenderer>();
        resources.RenderResources(Arg.Any<WorkloadDesiredSpec>())
            .Returns(new DockerResourceRendering
            {
                HostConfig = new HostConfig(),
                MetricsLabels = new Dictionary<string, string>(StringComparer.Ordinal),
            });

        return new DockerWorkloadProvider(
            clientFactory,
            puller,
            networks,
            volumes,
            containers,
            privilege,
            resources,
            validator,
            gate,
            recreate ?? Substitute.For<IDockerJournaledRecreate>(),
            Substitute.For<IDockerLogReader>(),
            Substitute.For<IDockerRollbackCoordinator>(),
            Substitute.For<IDockerStatusMapper>(),
            scopeFactory,
            NullLogger<DockerWorkloadProvider>.Instance);
    }

    private sealed class AttachmentBlindContainerManager(IDockerContainerManager inner) : IDockerContainerManager
    {
        public CreateContainerParameters BuildContainerParameters(DockerContainerRenderContext context)
            => inner.BuildContainerParameters(context);

        public Task<DockerContainerRef?> FindContainerAsync(IDockerClient c, WorkloadRef r, CancellationToken ct)
            => inner.FindContainerAsync(c, r, ct);

        public Task<IReadOnlyList<DockerContainerRef>> FindContainersAsync(IDockerClient c, WorkloadRef r, CancellationToken ct)
            => inner.FindContainersAsync(c, r, ct);

        public Task<IReadOnlyDictionary<string, string>> ReadHeldEnvAsync(
            IDockerClient c, DockerContainerRef r, IReadOnlyCollection<string> names, CancellationToken ct)
            => inner.ReadHeldEnvAsync(c, r, names, ct);

        public Task<DockerContainerMutationResult> CreateAndStartAsync(
            IDockerClient c, CreateContainerParameters p, CancellationToken ct)
            => inner.CreateAndStartAsync(c, p, ct);

        public Task<DockerOperationResult> StartContainerAsync(IDockerClient c, string id, CancellationToken ct)
            => inner.StartContainerAsync(c, id, ct);

        public Task<DockerOperationResult> StopContainerAsync(IDockerClient c, string id, CancellationToken ct)
            => inner.StopContainerAsync(c, id, ct);

        public Task<DockerOperationResult> RemoveContainerAsync(IDockerClient c, string id, CancellationToken ct)
            => inner.RemoveContainerAsync(c, id, ct);
    }

    private sealed class FakeDaemon
    {
        public FakeDaemon()
        {
            var networks = Substitute.For<INetworkOperations>();
            networks.ConnectNetworkAsync(
                    Arg.Any<string>(), Arg.Any<NetworkConnectParameters>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var name = call.Arg<string>();
                    Calls.Add($"connect:{name}");
                    ConnectedContainerIds.Add(call.Arg<NetworkConnectParameters>().Container);
                    if (string.Equals(name, FailConnectTo, StringComparison.Ordinal))
                    {
                        return Task.FromException(ConnectFailure ?? new Exception("connect failed"));
                    }

                    Connected.Add(name);
                    return Task.CompletedTask;
                });

            var containers = Substitute.For<IContainerOperations>();
            containers.CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Calls.Add("create");
                    Created.Add(call.Arg<CreateContainerParameters>());
                    return Task.FromResult(new CreateContainerResponse { ID = ContainerId });
                });
            containers.StartContainerAsync(
                    Arg.Any<string>(), Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Calls.Add("start");
                    return Task.FromResult(true);
                });
            containers.StopContainerAsync(
                    Arg.Any<string>(), Arg.Any<ContainerStopParameters>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Calls.Add("stop");
                    return Task.FromResult(true);
                });
            containers.RemoveContainerAsync(
                    Arg.Any<string>(), Arg.Any<ContainerRemoveParameters>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Calls.Add("remove");
                    Removed.Add(call.Arg<string>());
                    return Task.CompletedTask;
                });
            containers.RenameContainerAsync(
                    Arg.Any<string>(), Arg.Any<ContainerRenameParameters>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Calls.Add("rename");
                    return Task.CompletedTask;
                });
            containers.ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<IList<ContainerListResponse>>([.. Observed]));
            containers.InspectContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new ContainerInspectResponse
                {
                    State = new State { Running = true, Status = "running" },
                    RestartCount = 0,
                    Config = new ContainerConfig { Env = [.. HeldEnv] },
                }));

            Client = Substitute.For<IDockerClient>();
            Client.Networks.Returns(networks);
            Client.Containers.Returns(containers);
        }

        public IDockerClient Client { get; }

        public string? FailConnectTo { get; init; }

        public Exception? ConnectFailure { get; init; }

        public IReadOnlyList<ContainerListResponse> Observed { get; init; } = [];

        public List<string> Calls { get; } = [];

        public List<string> Connected { get; } = [];

        public List<string> ConnectedContainerIds { get; } = [];

        public List<string> Removed { get; } = [];

        public IReadOnlyList<string> HeldEnv { get; init; } = [];

        public List<CreateContainerParameters> Created { get; } = [];
    }

    private sealed class CapturingRecreate : IDockerJournaledRecreate
    {
        public CreateContainerParameters? Desired { get; private set; }

        public Task<WorkloadApplyResult> RecreateAsync(
            IDockerClient client, WorkloadDesiredSpec spec, DockerContainerRef currentContainer,
            CreateContainerParameters desiredContainer, IDockerRollbackCoordinator rollbackCoordinator,
            CancellationToken cancellationToken)
        {
            Desired = desiredContainer;
            return Task.FromResult(WorkloadApplyResult.Applied(spec.SpecHash));
        }
    }
}
