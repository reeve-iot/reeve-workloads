using FluentAssertions;
using NSubstitute;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadOrchestratorSettingsJudgeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private readonly SwitchableSettingsSource source = new();
    private readonly List<WorkloadInstance> instances = [];
    private readonly List<RetainedVolume> volumes = [];
    private readonly IWorkloadInstanceRepository instanceRepository = Substitute.For<IWorkloadInstanceRepository>();
    private readonly IRetainedVolumeRepository volumeRepository = Substitute.For<IRetainedVolumeRepository>();
    private WorkloadOrchestratorSettingsJudge judge;

    public WorkloadOrchestratorSettingsJudgeTests()
    {
        instanceRepository.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<WorkloadInstance>>([.. instances]));
        volumeRepository.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<RetainedVolume>>([.. volumes]));
        judge = Build(SettingsKit.ShippedCodecs());
    }

    private WorkloadOrchestratorSettingsJudge Build(IEnumerable<IWorkloadProviderSettingsCodec> codecs)
        => new(
            source,
            instanceRepository,
            volumeRepository,
            SettingsKit.ShippedCatalog(),
            codecs,
            Array.Empty<IWorkloadProviderAutoDetect>());

    private static WorkloadSettingsHostFacts NoFacts => new();

    private WorkloadInstance RunOn(WorkloadProviderId? provider, string workload = "controller", bool terminating = false)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "acme.zwave", workload, Now);
        if (provider is { } bound)
        {
            instance.SetProvider(bound, Now);
            instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        }

        if (terminating)
        {
            instance.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Provisioning, null, Now);
        }

        instances.Add(instance);
        return instance;
    }

    private RetainedVolume RetainOn(WorkloadProviderId provider, string volumeId = "data")
    {
        var volume = RetainedVolume.Record(
            Guid.NewGuid(), "acme.zwave", "controller", volumeId, $"reeve-{volumeId}", "1Gi", VolumeCriticality.Recoverable,
            null, provider, RetainedVolumeSchemeId.Docker, Guid.NewGuid(), Guid.NewGuid(), Now);
        volumes.Add(volume);
        return volume;
    }

    private Task<WorkloadSettingsVerdict> Judge(WorkloadOrchestratorSettings proposed, WorkloadSettingsHostFacts? facts = null)
        => judge.JudgeAsync(proposed, facts ?? NoFacts);

    private static WorkloadOrchestratorSettings Explicit(WorkloadProviderId provider) => new() { Provider = provider };

    [Fact]
    public async Task A_provider_change_with_an_instance_bound_to_the_current_provider_is_blocked_naming_the_instance()
    {
        source.Document = Explicit(KubernetesProvider.Id);
        var bound = RunOn(KubernetesProvider.Id, "controller");

        var verdict = await Judge(Explicit(ManualProvider.Id));

        verdict.IsApplicable.Should().BeFalse();
        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_switch_blocked");
        block.Instances.Select(i => i.Id).Should().Equal(bound.Id);
        block.Reason.Should().Contain("controller").And.Contain(KubernetesProvider.Id.Value);
    }

    [Fact]
    public async Task The_same_document_is_accepted_once_the_blocking_instance_is_gone()
    {
        source.Document = Explicit(KubernetesProvider.Id);
        var bound = RunOn(KubernetesProvider.Id);
        (await Judge(Explicit(ManualProvider.Id))).IsApplicable.Should().BeFalse();

        instances.Remove(bound);

        (await Judge(Explicit(ManualProvider.Id))).IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task A_proposal_that_keeps_the_provider_is_not_blocked_by_instances_bound_to_another_provider()
    {
        source.Document = Explicit(DockerProvider.Id);
        RunOn(KubernetesProvider.Id);

        var verdict = await Judge(Explicit(DockerProvider.Id));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task A_provider_change_is_blocked_only_by_instances_on_the_current_provider()
    {
        source.Document = Explicit(KubernetesProvider.Id);
        var onCurrent = RunOn(KubernetesProvider.Id);
        RunOn(DockerProvider.Id);

        var verdict = await Judge(Explicit(ManualProvider.Id));

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_switch_blocked");
        block.Provider.Should().Be(KubernetesProvider.Id);
        block.Instances.Select(i => i.Id).Should().Equal(onCurrent.Id);
    }

    [Fact]
    public async Task A_provider_change_with_instances_only_on_a_third_provider_is_accepted()
    {
        source.Document = Explicit(ManualProvider.Id);
        RunOn(DockerProvider.Id);

        var verdict = await Judge(Explicit(KubernetesProvider.Id));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Picking_the_provider_automatic_resolves_to_while_instances_stay_on_docker_is_accepted()
    {
        var autoJudge = AutoDetecting(KubernetesProvider.Id);
        source.Document = new WorkloadOrchestratorSettings();
        RunOn(DockerProvider.Id);

        var verdict = await autoJudge.JudgeAsync(Explicit(KubernetesProvider.Id), NoFacts);

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Staying_on_automatic_that_resolves_to_the_current_provider_is_accepted_whatever_is_bound()
    {
        var autoJudge = AutoDetecting(KubernetesProvider.Id);
        source.Document = new WorkloadOrchestratorSettings();
        RunOn(KubernetesProvider.Id);
        RunOn(DockerProvider.Id);

        (await autoJudge.JudgeAsync(new WorkloadOrchestratorSettings(), NoFacts)).IsApplicable.Should().BeTrue();
        (await autoJudge.JudgeAsync(Explicit(KubernetesProvider.Id), NoFacts)).IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Automatic_resolving_to_another_provider_than_the_explicit_one_is_blocked_by_instances_on_the_explicit_one()
    {
        var autoJudge = AutoDetecting(KubernetesProvider.Id);
        source.Document = Explicit(DockerProvider.Id);
        var bound = RunOn(DockerProvider.Id);
        RunOn(ManualProvider.Id);

        var verdict = await autoJudge.JudgeAsync(new WorkloadOrchestratorSettings(), NoFacts);

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_switch_blocked");
        block.Provider.Should().Be(DockerProvider.Id);
        block.Instances.Select(i => i.Id).Should().Equal(bound.Id);
    }

    [Fact]
    public async Task Automatic_resolving_to_the_explicit_provider_keeps_it_and_is_accepted()
    {
        var autoJudge = AutoDetecting(KubernetesProvider.Id);
        source.Document = Explicit(KubernetesProvider.Id);
        RunOn(KubernetesProvider.Id);

        var verdict = await autoJudge.JudgeAsync(new WorkloadOrchestratorSettings(), NoFacts);

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task With_no_provider_in_force_instances_on_other_providers_do_not_block_adding_a_provider_on_automatic()
    {
        var autoJudge = AutoDetecting(KubernetesProvider.Id);
        source.Document = new WorkloadOrchestratorSettings { ConfiguredProviders = [] };
        RunOn(DockerProvider.Id);
        RunOn(ManualProvider.Id);

        var verdict = await autoJudge.JudgeAsync(
            new WorkloadOrchestratorSettings { ConfiguredProviders = [DockerProvider.Id, KubernetesProvider.Id] }, NoFacts);

        verdict.ActiveProvider.IsEmpty.Should().BeTrue();
        verdict.RequestedProvider.Should().Be(KubernetesProvider.Id);
        verdict.Blocks.Should().NotContain(b => b.Code == "provider_switch_blocked");
    }

    [Fact]
    public async Task With_no_provider_in_force_and_none_requested_nothing_is_blocked()
    {
        source.Document = new WorkloadOrchestratorSettings { ConfiguredProviders = [] };
        RunOn(DockerProvider.Id);

        var verdict = await judge.JudgeAsync(new WorkloadOrchestratorSettings { ConfiguredProviders = [] }, NoFacts);

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task A_served_provider_that_is_not_compiled_in_still_blocks_a_change_through_its_instances()
    {
        source.Document = Explicit(WorkloadProviderId.Parse("nomad"));
        var onNomad = RunOn(WorkloadProviderId.Parse("nomad"));

        var verdict = await Judge(Explicit(DockerProvider.Id));

        var block = verdict.Blocks.Should().ContainSingle(b => b.Code == "provider_switch_blocked").Which;
        block.Instances.Select(i => i.Id).Should().Equal(onNomad.Id);
    }

    [Fact]
    public async Task A_switch_to_no_resolvable_provider_names_none_in_the_reason()
    {
        source.Document = Explicit(DockerProvider.Id);
        RunOn(DockerProvider.Id);

        var verdict = await NoCurrentProvider().JudgeAsync(new WorkloadOrchestratorSettings(), NoFacts);

        verdict.Blocks.Should().ContainSingle(b => b.Code == "provider_switch_blocked")
            .Which.Reason.Should().Contain("to no provider").And.NotContain("''");
    }

    [Fact]
    public async Task Terminating_and_never_deployed_instances_do_not_block_a_provider_change()
    {
        source.Document = Explicit(KubernetesProvider.Id);
        RunOn(KubernetesProvider.Id, terminating: true);
        RunOn(provider: null);

        var verdict = await Judge(Explicit(ManualProvider.Id).WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings()));

        verdict.Blocks.Should().NotContain(b => b.Code == "provider_switch_blocked");
    }

    [Fact]
    public async Task Dropping_the_entry_of_the_active_provider_is_refused()
    {
        source.Document = SettingsKit.Kubernetes();

        var verdict = await Judge(Explicit(KubernetesProvider.Id));

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_in_use");
        block.Provider.Should().Be(KubernetesProvider.Id);
        block.Reason.Should().Contain("drop").And.Contain("active provider");
    }

    [Fact]
    public async Task Dropping_an_entry_bound_to_a_terminating_instance_is_refused_naming_it()
    {
        source.Document = SettingsKit.Kubernetes().WithProvider(DockerProvider.Id, new DockerProviderSettings());
        var teardown = RunOn(DockerProvider.Id, "worker", terminating: true);

        var verdict = await Judge(SettingsKit.Kubernetes());

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_in_use");
        block.Instances.Select(i => i.Id).Should().Equal(teardown.Id);
        block.Reason.Should().Contain("worker");
    }

    [Fact]
    public async Task Dropping_an_entry_named_by_an_active_retained_volume_is_refused_naming_the_volume()
    {
        source.Document = SettingsKit.Kubernetes().WithProvider(DockerProvider.Id, new DockerProviderSettings());
        var retained = RetainOn(DockerProvider.Id, "zwave-data");

        var verdict = await Judge(SettingsKit.Kubernetes());

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_in_use");
        block.Volumes.Select(v => v.Id).Should().Equal(retained.Id);
        block.Reason.Should().Contain("zwave-data");
    }

    [Fact]
    public async Task Dropping_an_entry_nothing_uses_is_accepted()
    {
        source.Document = SettingsKit.Kubernetes().WithProvider(DockerProvider.Id, new DockerProviderSettings());

        (await Judge(SettingsKit.Kubernetes())).IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task A_purged_retained_volume_no_longer_holds_its_entry()
    {
        source.Document = SettingsKit.Kubernetes().WithProvider(DockerProvider.Id, new DockerProviderSettings());
        var retained = RetainOn(DockerProvider.Id);
        (await Judge(SettingsKit.Kubernetes())).IsApplicable.Should().BeFalse();

        volumes.Remove(retained);

        (await Judge(SettingsKit.Kubernetes())).IsApplicable.Should().BeTrue();
    }

    private static KubernetesProviderSettings Remote(string? kubeconfig = null, string? ns = "reeve", string? context = null)
        => new()
        {
            Namespace = ns,
            ConnectionMode = KubernetesConnectionMode.RemoteKubeconfig,
            Kubeconfig = kubeconfig ?? SettingsKit.Kubeconfig(),
            KubeconfigContext = context,
        };

    private async Task<WorkloadSettingsVerdict> JudgeKubernetesMove(KubernetesProviderSettings current, KubernetesProviderSettings moved)
    {
        source.Document = Explicit(KubernetesProvider.Id).WithProvider(KubernetesProvider.Id, current);
        RunOn(KubernetesProvider.Id);
        return await Judge(Explicit(KubernetesProvider.Id).WithProvider(KubernetesProvider.Id, moved));
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("connectionMode")]
    [InlineData("server")]
    public async Task Relocating_a_kubernetes_entry_in_use_is_refused_naming_the_field(string field)
    {
        var current = Remote();
        var moved = field switch
        {
            "namespace" => current with { Namespace = "elsewhere" },
            "connectionMode" => current with { ConnectionMode = KubernetesConnectionMode.InCluster },
            _ => current with { Kubeconfig = SettingsKit.Kubeconfig(server: "https://10.0.0.2:6443") },
        };

        var verdict = await JudgeKubernetesMove(current, moved);

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_in_use");
        block.Reason.Should().Contain(field).And.NotContain("10.0.0.1").And.NotContain("token-one");
    }

    [Fact]
    public async Task Rotating_the_credential_of_a_kubeconfig_is_not_a_relocation()
    {
        var verdict = await JudgeKubernetesMove(Remote(), Remote(SettingsKit.Kubeconfig(token: "token-two")));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Renaming_the_context_to_one_resolving_to_the_same_server_is_not_a_relocation()
    {
        var verdict = await JudgeKubernetesMove(
            Remote(SettingsKit.Kubeconfig(context: "old-name")),
            Remote(SettingsKit.Kubeconfig(context: "new-name"), context: "new-name"));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Replacing_the_kubeconfig_with_another_resolving_to_the_same_server_is_not_a_relocation()
    {
        var other = SettingsKit.Kubeconfig(server: "https://10.0.0.1:6443/", context: "other", token: "elsewhere");

        var verdict = await JudgeKubernetesMove(Remote(), Remote(other));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Pointing_the_context_at_a_cluster_with_another_server_is_a_relocation()
    {
        var two = SettingsKit.Kubeconfig(server: "https://10.0.0.2:6443", context: "second");

        var verdict = await JudgeKubernetesMove(Remote(SettingsKit.Kubeconfig(context: "first")), Remote(two, context: "second"));

        verdict.Blocks.Should().ContainSingle().Which.Reason.Should().Contain("server");
    }

    [Fact]
    public async Task Leftover_kubeconfig_of_an_in_cluster_entry_is_not_a_relocation()
    {
        var inCluster = new KubernetesProviderSettings { Namespace = "reeve" };

        var verdict = await JudgeKubernetesMove(inCluster, inCluster with { Kubeconfig = SettingsKit.Kubeconfig() });

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Relocating_the_docker_socket_of_an_entry_in_use_is_refused()
    {
        source.Document = SettingsKit.Docker(socket: "unix:///var/run/docker.sock");
        RunOn(DockerProvider.Id);

        var verdict = await Judge(SettingsKit.Docker(socket: "tcp://other:2375"));

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_in_use");
        block.Reason.Should().Contain("socketUri");
    }

    [Fact]
    public async Task Changing_a_field_that_does_not_relocate_an_entry_in_use_is_accepted()
    {
        source.Document = SettingsKit.Docker();
        RunOn(DockerProvider.Id);

        var verdict = await Judge(SettingsKit.Docker(token: "rotated") with { AllowUnpinnedImages = true });

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Relocating_the_namespace_of_an_empty_active_kubernetes_entry_is_accepted()
    {
        source.Document = Explicit(KubernetesProvider.Id).WithProvider(KubernetesProvider.Id, Remote());

        var verdict = await Judge(Explicit(KubernetesProvider.Id).WithProvider(KubernetesProvider.Id, Remote(ns: "elsewhere")));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Pointing_an_empty_active_kubernetes_entry_at_another_cluster_is_accepted()
    {
        source.Document = Explicit(KubernetesProvider.Id).WithProvider(KubernetesProvider.Id, Remote());

        var verdict = await Judge(Explicit(KubernetesProvider.Id)
            .WithProvider(KubernetesProvider.Id, Remote(SettingsKit.Kubeconfig(server: "https://10.0.0.2:6443"))));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Relocating_the_socket_of_an_empty_active_docker_entry_is_accepted()
    {
        source.Document = SettingsKit.Docker(socket: "unix:///var/run/docker.sock");

        var verdict = await Judge(SettingsKit.Docker(socket: "tcp://other:2375"));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Relocating_the_active_entry_with_a_bound_instance_is_refused_naming_the_instance_not_the_activity()
    {
        source.Document = Explicit(KubernetesProvider.Id).WithProvider(KubernetesProvider.Id, Remote());
        var running = RunOn(KubernetesProvider.Id, "controller");

        var verdict = await Judge(Explicit(KubernetesProvider.Id).WithProvider(KubernetesProvider.Id, Remote(ns: "elsewhere")));

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_in_use");
        block.Instances.Select(i => i.Id).Should().Equal(running.Id);
        block.Reason.Should().Contain("namespace").And.Contain("controller").And.NotContain("active provider");
    }

    [Fact]
    public async Task Relocating_the_active_entry_is_accepted_once_its_instance_is_uninstalled()
    {
        source.Document = Explicit(KubernetesProvider.Id).WithProvider(KubernetesProvider.Id, Remote());
        var running = RunOn(KubernetesProvider.Id);
        var moved = Explicit(KubernetesProvider.Id).WithProvider(KubernetesProvider.Id, Remote(ns: "elsewhere"));
        (await Judge(moved)).IsApplicable.Should().BeFalse();

        instances.Remove(running);

        (await Judge(moved)).IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Relocating_the_active_entry_with_a_terminating_instance_is_refused()
    {
        source.Document = SettingsKit.Docker(socket: "unix:///var/run/docker.sock");
        var teardown = RunOn(DockerProvider.Id, "worker", terminating: true);

        var verdict = await Judge(SettingsKit.Docker(socket: "tcp://other:2375"));

        verdict.Blocks.Should().ContainSingle().Which.Instances.Select(i => i.Id).Should().Equal(teardown.Id);
    }

    [Fact]
    public async Task Relocating_the_active_entry_with_a_retained_volume_is_refused_naming_the_volume()
    {
        source.Document = Explicit(KubernetesProvider.Id).WithProvider(KubernetesProvider.Id, Remote());
        var retained = RetainOn(KubernetesProvider.Id, "zwave-data");

        var verdict = await Judge(Explicit(KubernetesProvider.Id).WithProvider(KubernetesProvider.Id, Remote(ns: "elsewhere")));

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_in_use");
        block.Volumes.Select(v => v.Id).Should().Equal(retained.Id);
        block.Reason.Should().Contain("zwave-data").And.NotContain("active provider");
    }

    [Fact]
    public async Task Relocating_an_entry_nothing_uses_is_accepted()
    {
        source.Document = SettingsKit.Docker(socket: "unix:///var/run/docker.sock")
            .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings { Namespace = "a" });

        var verdict = await Judge(SettingsKit.Docker(socket: "unix:///var/run/docker.sock")
            .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings { Namespace = "b" }));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task A_target_the_host_says_is_unavailable_is_refused_with_the_host_reason()
    {
        var facts = new WorkloadSettingsHostFacts
        {
            UnavailableTargets = new Dictionary<WorkloadProviderId, string>
            {
                [DockerProvider.Id] = "Docker cannot run on a clustered installation.",
            },
        };

        var verdict = await Judge(Explicit(DockerProvider.Id), facts);

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("target_not_configured");
        block.Provider.Should().Be(DockerProvider.Id);
        block.Reason.Should().Contain("clustered");
    }

    [Fact]
    public async Task A_target_that_is_not_listed_as_unavailable_is_accepted()
    {
        var facts = new WorkloadSettingsHostFacts
        {
            UnavailableTargets = new Dictionary<WorkloadProviderId, string> { [DockerProvider.Id] = "not declared" },
        };

        (await Judge(Explicit(KubernetesProvider.Id), facts)).IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task An_unavailable_provider_that_is_not_the_target_does_not_block()
    {
        var facts = new WorkloadSettingsHostFacts
        {
            UnavailableTargets = new Dictionary<WorkloadProviderId, string> { [DockerProvider.Id] = "not declared" },
        };

        (await Judge(new WorkloadOrchestratorSettings(), facts)).IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Every_violated_rule_is_reported_in_one_verdict()
    {
        source.Document = SettingsKit.Kubernetes();
        RunOn(KubernetesProvider.Id);
        var facts = new WorkloadSettingsHostFacts
        {
            UnavailableTargets = new Dictionary<WorkloadProviderId, string> { [ManualProvider.Id] = "not declared" },
        };

        var verdict = await Judge(Explicit(ManualProvider.Id), facts);

        verdict.Blocks.Select(b => b.Code).Should().BeEquivalentTo(
            "provider_switch_blocked", "provider_in_use", "target_not_configured");
    }

    [Fact]
    public async Task Relocating_a_kubernetes_entry_the_served_document_does_not_have_is_refused()
    {
        source.Document = Explicit(KubernetesProvider.Id);
        RunOn(KubernetesProvider.Id);

        var verdict = await Judge(Explicit(KubernetesProvider.Id)
            .WithProvider(KubernetesProvider.Id, new KubernetesProviderSettings { Namespace = "other" }));

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_in_use");
        block.Reason.Should().Contain("namespace");
    }

    [Fact]
    public async Task Relocating_the_docker_socket_of_an_entry_the_served_document_does_not_have_is_refused()
    {
        source.Document = Explicit(DockerProvider.Id);
        RunOn(DockerProvider.Id);

        var verdict = await Judge(Explicit(DockerProvider.Id)
            .WithProvider(DockerProvider.Id, new DockerProviderSettings { SocketUri = "tcp://elsewhere:2375" }));

        verdict.Blocks.Should().ContainSingle().Which.Code.Should().Be("provider_in_use");
    }

    [Fact]
    public async Task An_added_entry_that_keeps_the_defaults_is_not_a_relocation()
    {
        source.Document = Explicit(DockerProvider.Id);
        RunOn(DockerProvider.Id);

        var verdict = await Judge(Explicit(DockerProvider.Id).WithProvider(DockerProvider.Id, new DockerProviderSettings()));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task An_entry_whose_provider_has_no_codec_is_never_judged_relocated()
    {
        var nomad = WorkloadProviderId.Parse("nomad");
        source.Document = Explicit(nomad).WithProvider(nomad, System.Text.Json.JsonDocument.Parse("""{"a":1}""").RootElement);
        RunOn(nomad);

        var verdict = await Judge(Explicit(nomad).WithProvider(nomad, System.Text.Json.JsonDocument.Parse("""{"a":2}""").RootElement));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task A_placement_field_the_served_side_does_not_know_is_not_a_move()
    {
        var nomad = WorkloadProviderId.Parse("nomad");
        var codec = new ScriptedCodec(nomad, entry => entry.TryGetProperty("extra", out _)
            ? new Dictionary<string, string> { ["address"] = "a", ["extra"] = "b" }
            : new Dictionary<string, string> { ["address"] = "a" });
        judge = Build([codec]);
        source.Document = new WorkloadOrchestratorSettings { Provider = nomad }
            .WithProvider(nomad, System.Text.Json.JsonDocument.Parse("""{}""").RootElement);
        RunOn(nomad);

        var verdict = await Judge(new WorkloadOrchestratorSettings { Provider = nomad }
            .WithProvider(nomad, System.Text.Json.JsonDocument.Parse("""{"extra":1}""").RootElement));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task An_automatic_target_is_never_refused_as_unavailable()
    {
        var facts = new WorkloadSettingsHostFacts
        {
            UnavailableTargets = new Dictionary<WorkloadProviderId, string> { [ManualProvider.Id] = "not declared" },
        };

        (await Judge(new WorkloadOrchestratorSettings(), facts)).Blocks.Should().NotContain(b => b.Code == "target_not_configured");
    }

    [Fact]
    public async Task Instances_bound_to_the_proposed_provider_do_not_block_it()
    {
        source.Document = Explicit(DockerProvider.Id);
        RunOn(DockerProvider.Id);

        (await Judge(Explicit(DockerProvider.Id))).IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task A_block_names_every_instance_and_volume_that_holds_the_entry()
    {
        source.Document = SettingsKit.Kubernetes().WithProvider(DockerProvider.Id, new DockerProviderSettings());
        var first = RunOn(DockerProvider.Id, "a", terminating: true);
        var second = RunOn(DockerProvider.Id, "b", terminating: true);
        var volume = RetainOn(DockerProvider.Id);
        RetainOn(KubernetesProvider.Id, "other");

        var block = (await Judge(SettingsKit.Kubernetes())).Blocks.Should().ContainSingle().Which;

        block.Instances.Select(i => i.Id).Should().BeEquivalentTo([first.Id, second.Id]);
        block.Instances.Select(i => (i.GroupId, i.WorkloadId)).Should().BeEquivalentTo([("acme.zwave", "a"), ("acme.zwave", "b")]);
        block.Volumes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new WorkloadSettingsBlockingVolume { Id = volume.Id, GroupId = "acme.zwave", WorkloadId = "controller", VolumeId = "data" });
    }

    [Fact]
    public async Task A_judge_refuses_a_null_document_or_null_facts()
    {
        await FluentActions.Awaiting(() => judge.JudgeAsync(null!, NoFacts)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => judge.JudgeAsync(new WorkloadOrchestratorSettings(), null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    private static WorkloadOrchestratorSettings Configured(WorkloadOrchestratorSettings settings, params WorkloadProviderId[] providers)
        => settings with { ConfiguredProviders = providers };

    [Fact]
    public async Task A_target_outside_the_configured_set_is_refused()
    {
        source.Document = Configured(new WorkloadOrchestratorSettings(), DockerProvider.Id);

        var verdict = await Judge(Explicit(KubernetesProvider.Id));

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("target_not_configured");
        block.Provider.Should().Be(KubernetesProvider.Id);
    }

    [Fact]
    public async Task A_target_inside_the_configured_set_is_accepted()
    {
        source.Document = Configured(new WorkloadOrchestratorSettings(), DockerProvider.Id, KubernetesProvider.Id);

        (await Judge(Explicit(KubernetesProvider.Id))).IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task Without_a_configured_set_every_compiled_in_provider_is_a_target()
    {
        source.Document = new WorkloadOrchestratorSettings();

        (await Judge(Explicit(KubernetesProvider.Id))).IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task An_empty_configured_set_refuses_every_explicit_target()
    {
        source.Document = Configured(new WorkloadOrchestratorSettings());

        (await Judge(Explicit(DockerProvider.Id))).Blocks.Should().ContainSingle().Which.Code.Should().Be("target_not_configured");
    }

    [Fact]
    public async Task An_unconfigured_target_the_host_also_lists_as_unavailable_is_reported_once()
    {
        source.Document = Configured(new WorkloadOrchestratorSettings(), DockerProvider.Id);
        var facts = new WorkloadSettingsHostFacts
        {
            UnavailableTargets = new Dictionary<WorkloadProviderId, string> { [KubernetesProvider.Id] = "clustered" },
        };

        (await Judge(Explicit(KubernetesProvider.Id), facts)).Blocks.Should().ContainSingle();
    }

    [Fact]
    public async Task A_placement_field_the_proposed_side_lost_counts_as_moved()
    {
        var nomad = WorkloadProviderId.Parse("nomad");
        var codec = new ScriptedCodec(nomad, entry => entry.TryGetProperty("extra", out _)
            ? new Dictionary<string, string> { ["address"] = "a", ["extra"] = "b" }
            : new Dictionary<string, string> { ["address"] = "a" });
        judge = Build([codec]);
        source.Document = new WorkloadOrchestratorSettings { Provider = nomad }
            .WithProvider(nomad, System.Text.Json.JsonDocument.Parse("""{"extra":1}""").RootElement);
        RunOn(nomad);

        var verdict = await Judge(new WorkloadOrchestratorSettings { Provider = nomad }
            .WithProvider(nomad, System.Text.Json.JsonDocument.Parse("""{}""").RootElement));

        verdict.Blocks.Should().ContainSingle().Which.Code.Should().Be("provider_in_use");
    }

    [Theory]
    [InlineData("")]
    [InlineData("::: not a kubeconfig [")]
    public async Task Re_entering_a_valid_kubeconfig_over_one_that_cannot_be_resolved_is_not_a_relocation(string unresolved)
    {
        var verdict = await JudgeKubernetesMove(Remote(unresolved), Remote(SettingsKit.Kubeconfig()));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task A_server_spelled_with_another_case_userinfo_or_trailing_slash_is_not_a_relocation()
    {
        var verdict = await JudgeKubernetesMove(
            Remote(SettingsKit.Kubeconfig(server: "https://api.example:6443")),
            Remote(SettingsKit.Kubeconfig(server: "HTTPS://user:pass@API.example:6443/")));

        verdict.IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task A_proposed_configured_set_that_makes_auto_detect_pick_another_provider_is_blocked_by_the_bound_instances()
    {
        var detect = new FixedAutoDetect(KubernetesProvider.Id, IWorkloadProviderAutoDetect.InClusterPriority);
        var autoJudge = new WorkloadOrchestratorSettingsJudge(
            source, instanceRepository, volumeRepository, SettingsKit.ShippedCatalog(), SettingsKit.ShippedCodecs(),
            [detect, new FixedAutoDetect(DockerProvider.Id, IWorkloadProviderAutoDetect.LocalRuntimePriority)]);
        source.Document = new WorkloadOrchestratorSettings { ConfiguredProviders = [DockerProvider.Id] };
        var bound = RunOn(DockerProvider.Id);

        var verdict = await autoJudge.JudgeAsync(
            new WorkloadOrchestratorSettings { ConfiguredProviders = [DockerProvider.Id, KubernetesProvider.Id] }, NoFacts);

        var block = verdict.Blocks.Should().ContainSingle().Which;
        block.Code.Should().Be("provider_switch_blocked");
        block.Instances.Select(i => i.Id).Should().Equal(bound.Id);
    }

    [Fact]
    public async Task A_proposal_without_a_configured_set_keeps_the_served_one_when_auto_detecting()
    {
        var autoJudge = new WorkloadOrchestratorSettingsJudge(
            source, instanceRepository, volumeRepository, SettingsKit.ShippedCatalog(), SettingsKit.ShippedCodecs(),
            [new FixedAutoDetect(KubernetesProvider.Id, IWorkloadProviderAutoDetect.InClusterPriority),
             new FixedAutoDetect(DockerProvider.Id, IWorkloadProviderAutoDetect.LocalRuntimePriority)]);
        source.Document = new WorkloadOrchestratorSettings { ConfiguredProviders = [DockerProvider.Id] };
        RunOn(DockerProvider.Id);

        (await autoJudge.JudgeAsync(new WorkloadOrchestratorSettings(), NoFacts)).IsApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task The_verdict_carries_the_resolved_requested_and_active_providers()
    {
        var autoJudge = AutoDetecting(KubernetesProvider.Id);
        source.Document = Explicit(DockerProvider.Id);

        var explicitChange = await autoJudge.JudgeAsync(Explicit(ManualProvider.Id), NoFacts);
        var toAutomatic = await autoJudge.JudgeAsync(new WorkloadOrchestratorSettings(), NoFacts);
        var kept = await autoJudge.JudgeAsync(Explicit(DockerProvider.Id), NoFacts);

        explicitChange.RequestedProvider.Should().Be(ManualProvider.Id);
        explicitChange.ActiveProvider.Should().Be(DockerProvider.Id);
        toAutomatic.RequestedProvider.Should().Be(KubernetesProvider.Id);
        toAutomatic.ActiveProvider.Should().Be(DockerProvider.Id);
        kept.RequestedProvider.Should().Be(DockerProvider.Id);
        kept.ActiveProvider.Should().Be(DockerProvider.Id);
    }

    [Fact]
    public async Task The_verdict_reports_an_empty_active_provider_when_the_served_document_resolves_to_none()
    {
        source.Document = new WorkloadOrchestratorSettings();

        var verdict = await NoCurrentProvider().JudgeAsync(Explicit(DockerProvider.Id), NoFacts);

        verdict.RequestedProvider.Should().Be(DockerProvider.Id);
        verdict.ActiveProvider.IsEmpty.Should().BeTrue();
    }

    private WorkloadOrchestratorSettingsJudge NoCurrentProvider()
        => new(
            source, instanceRepository, volumeRepository, new CompiledProviderCatalog(ProviderRegistryFakes.RegistryWith()),
            SettingsKit.ShippedCodecs(), Array.Empty<IWorkloadProviderAutoDetect>());

    private WorkloadOrchestratorSettingsJudge AutoDetecting(WorkloadProviderId resolvesTo)
        => new(
            source, instanceRepository, volumeRepository, SettingsKit.ShippedCatalog(), SettingsKit.ShippedCodecs(),
            [new FixedAutoDetect(resolvesTo, IWorkloadProviderAutoDetect.InClusterPriority)]);

    private sealed class FixedAutoDetect(WorkloadProviderId provider, int priority) : IWorkloadProviderAutoDetect
    {
        public WorkloadProviderId Provider => provider;

        public int Priority => priority;

        public Task<bool> IsAvailableAsync(System.Text.Json.JsonElement providerSettings, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    [Fact]
    public async Task The_reason_of_an_active_entry_names_what_holds_it_and_nothing_else()
    {
        source.Document = SettingsKit.Docker();
        RunOn(DockerProvider.Id, "controller");
        RetainOn(DockerProvider.Id, "datavol");

        var reason = (await Judge(new WorkloadOrchestratorSettings { Provider = DockerProvider.Id })).Blocks.Single().Reason;

        reason.Should().Contain("active").And.Contain("controller").And.Contain("datavol").And.Contain("retained");
    }

    [Fact]
    public async Task The_reason_of_an_idle_entry_with_only_volumes_does_not_claim_it_is_active_or_has_instances()
    {
        source.Document = Explicit(KubernetesProvider.Id).WithProvider(DockerProvider.Id, new DockerProviderSettings());
        RetainOn(DockerProvider.Id, "datavol");

        var reason = (await Judge(Explicit(KubernetesProvider.Id))).Blocks.Single().Reason;

        reason.Should().Contain("datavol").And.NotContain("active").And.NotContain("instance");
    }

    [Fact]
    public async Task The_reason_of_an_idle_entry_with_only_instances_does_not_mention_volumes()
    {
        source.Document = Explicit(KubernetesProvider.Id).WithProvider(DockerProvider.Id, new DockerProviderSettings());
        RunOn(DockerProvider.Id, "controller");

        var reason = (await Judge(Explicit(KubernetesProvider.Id))).Blocks.First(b => b.Code == "provider_in_use").Reason;

        reason.Should().Contain("controller").And.NotContain("retained");
    }

    [Fact]
    public async Task The_moved_fields_are_listed_in_alphabetical_order()
    {
        var current = Remote();
        var moved = current with { Namespace = "elsewhere", ConnectionMode = KubernetesConnectionMode.InCluster };

        var reason = (await JudgeKubernetesMove(current, moved)).Blocks.Single().Reason;

        reason.IndexOf("connectionMode", StringComparison.Ordinal).Should().BePositive()
            .And.BeLessThan(reason.IndexOf("namespace", StringComparison.Ordinal));
    }
}
