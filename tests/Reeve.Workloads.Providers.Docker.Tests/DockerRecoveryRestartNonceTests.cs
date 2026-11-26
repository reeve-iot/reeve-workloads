using System.Collections.Generic;
using FluentAssertions;
using Reeve.Workloads.Providers.Docker;
using Xunit;

namespace Reeve.Workloads.Providers.Docker.Tests;

public sealed class DockerRecoveryRestartNonceTests
{
    private const string Canonical = "reeve-wl-acme-zwave-controller-abcdef12";
    private const string SpecHash = "hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh";

    private static DockerContainerRef Container(
        string name, string? specHash, string? restartNonce, bool running)
        => new()
        {
            Id = name,
            Name = name,
            SpecHash = specHash,
            RestartNonce = restartNonce,
            IsRunning = running,
        };

    [Fact]
    public void A_running_container_at_the_target_spec_hash_but_a_stale_nonce_forces_a_full_swap_not_converged()
    {
        var live = Container(Canonical, SpecHash, restartNonce: null, running: true);

        var decision = DockerRecoveryDecisionTable.Decide(new List<DockerContainerRef> { live }, SpecHash, targetRestartNonce: "1");

        decision.Action.Should().Be(DockerRecoveryAction.RunFullSwap,
            "a restart-nonce bump at an otherwise-converged spec-hash must force a recreate, not a silent no-op");
        decision.PreviousContainer.Should().Be(live, "the stale-nonce live container is the old to swap out");
        decision.NewContainer.Should().BeNull("no container yet carries the target nonce");
    }

    [Fact]
    public void A_running_container_matching_both_spec_hash_and_nonce_is_converged()
    {
        var live = Container(Canonical, SpecHash, restartNonce: "1", running: true);

        var decision = DockerRecoveryDecisionTable.Decide(new List<DockerContainerRef> { live }, SpecHash, targetRestartNonce: "1");

        decision.Action.Should().Be(DockerRecoveryAction.Converged);
        decision.NewContainer.Should().Be(live);
    }

    [Fact]
    public void Nonce_zero_target_matches_a_container_with_no_nonce_label()
    {
        var live = Container(Canonical, SpecHash, restartNonce: null, running: true);

        DockerRecoveryDecisionTable.Decide(new List<DockerContainerRef> { live }, SpecHash, targetRestartNonce: "0")
            .Action.Should().Be(DockerRecoveryAction.Converged);
    }

    [Fact]
    public void Crash_mid_restart_new_running_plus_renamed_stale_old_resumes_at_remove_previous()
    {
        var newC = Container(Canonical, SpecHash, restartNonce: "1", running: true);
        var renamedOld = Container($"{Canonical}{DockerRecoveryDecisionTable.RenamedSuffixMarker}hhhhhhhh", SpecHash, restartNonce: null, running: false);

        var decision = DockerRecoveryDecisionTable.Decide(
            new List<DockerContainerRef> { newC, renamedOld }, SpecHash, targetRestartNonce: "1");

        decision.Action.Should().Be(DockerRecoveryAction.FinishRemovePrevious);
        decision.NewContainer.Should().Be(newC);
        decision.PreviousContainer.Should().Be(renamedOld);
    }

    [Fact]
    public void The_nonce_agnostic_overload_keeps_matching_purely_on_spec_hash()
    {
        var live = Container(Canonical, SpecHash, restartNonce: null, running: true);

        DockerRecoveryDecisionTable.Decide(new List<DockerContainerRef> { live }, SpecHash)
            .Action.Should().Be(DockerRecoveryAction.Converged);
    }
}
