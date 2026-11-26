using NSubstitute;
using Reeve.Workloads.Core;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core.Groups;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Providers.Docker;
using Reeve.Workloads.Providers.Manual;

namespace Reeve.Workloads.Core.Tests.Groups;


public sealed class GroupManifestReconcileInputsProviderTests
{
    private const string GroupId = "acme.zwave";
    private const string WorkloadId = "controller";
    private static readonly DateTimeOffset At = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryWorkloadGroupRepository modules = new();

    [Fact]
    public async Task The_declaration_at_the_recorded_version_is_the_one_reconciled()
    {
        var manifests = Read(new InstalledGroupManifestRead
        {
            InstalledVersion = "1.2.0",
            AtInstalledVersion = Manifest(),
            HighestAvailable = Manifest(),
        });

        var inputs = (await Provider(manifests).GetInputsAsync(Instance())).Inputs;

        inputs!.WorkloadManifest.Id.Should().Be(WorkloadId);
        inputs.InstalledGroupVersion.Should().Be("1.2.0");
    }

    [Fact]
    public async Task Shared_read_inputs_preserve_protected_storage_for_the_deploy_boundary()
    {
        var workload = Manifest().Workloads[0] with
        {
            Parameters = [new WorkloadParameter { Name = "dbPassword", Type = WorkloadParameterType.Password }],
        };
        var manifest = new DeploymentManifest
        {
            SchemaVersion = 1,
            Workloads = [workload],
        };
        var codec = new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider());
        var stored = codec.Protect(workload, new Dictionary<string, string?> { ["dbPassword"] = "clear" });
        var instance = Instance();
        instance.SetResolvedParameters(stored, At);
        var inputs = (await Provider(Read(new InstalledGroupManifestRead
        {
            InstalledVersion = "1.2.0",
            AtInstalledVersion = manifest,
            HighestAvailable = manifest,
        })).GetInputsAsync(instance)).Inputs;

        inputs!.ResolvedParameters["dbPassword"].Should().Be(stored["dbPassword"]);
        codec.Unprotect(workload, inputs.ResolvedParameters)["dbPassword"].Should().Be("clear");
    }

    [Fact]
    public async Task With_no_recorded_version_it_HOLDS_where_the_edit_form_serves_the_highest()
    {
        var read = new InstalledGroupManifestRead { HighestAvailable = Manifest() };

        (await Provider(Read(read)).GetInputsAsync(Instance())).Should().Be(WorkloadReconcileInputsResult.NotDeclared);

        GroupManifestDeploymentContextSource.ForTheEditForm(read).Should().NotBeNull(
            "the two forks are asserted TOGETHER: apart, either could be changed to match the other and stay green.");
    }

    [Fact]
    public async Task A_recorded_version_with_no_declaration_HOLDS_and_never_falls_back()
    {
        var manifests = Read(new InstalledGroupManifestRead
        {
            InstalledVersion = "1.2.0",
            HighestAvailable = Manifest(),
        });

        (await Provider(manifests).GetInputsAsync(Instance())).Should().Be(WorkloadReconcileInputsResult.NotDeclared);
    }

    [Fact]
    public async Task A_bundled_module_reconciles_on_the_factory_declaration_and_a_sentinel_version()
    {
        var manifests = Read(new InstalledGroupManifestRead { Bundled = Manifest() });

        var inputs = (await Provider(manifests).GetInputsAsync(Instance())).Inputs;

        inputs!.InstalledGroupVersion.Should().Be(GroupManifestReconcileInputsProvider.BundledGroupVersion);
        inputs.Bootstrap.AppVersion.Should().Be(GroupManifestReconcileInputsProvider.BundledGroupVersion);
    }

    [Fact]
    public async Task A_bundled_module_whose_component_has_a_tag_reports_the_tag()
    {
        var images = new ProductImageOptions
        {
            Components = new Dictionary<string, ProductImageEntry>(StringComparer.Ordinal)
            {
                ["controller"] = new() { Ref = "registry.example.com/reeve/controller", Tag = "3.4.5" },
            },
        };

        var inputs = (await Provider(Read(new InstalledGroupManifestRead { Bundled = Manifest() }), images)
            .GetInputsAsync(Instance())).Inputs;

        inputs!.InstalledGroupVersion.Should().Be("3.4.5");
    }

    [Fact]
    public async Task A_refused_declaration_HOLDS_rather_than_falling_through_to_the_bundled_one()
    {
        var manifests = Read(new InstalledGroupManifestRead { Refused = true, Bundled = Manifest() });

        (await Provider(manifests).GetInputsAsync(Instance())).Should().Be(WorkloadReconcileInputsResult.NotDeclared);
    }

    [Fact]
    public async Task A_declaration_that_no_longer_names_this_workload_HOLDS()
    {
        var manifests = Read(new InstalledGroupManifestRead { Bundled = Manifest("something-else") });

        (await Provider(manifests).GetInputsAsync(Instance())).Should().Be(WorkloadReconcileInputsResult.NotDeclared);
    }

    [Fact]
    public async Task The_bootstrap_carries_the_librarys_own_keys_beside_the_hosts_unchanged()
    {
        var instance = Instance();

        var inputs = (await Provider(Read(new InstalledGroupManifestRead { Bundled = Manifest() }))
            .GetInputsAsync(instance)).Inputs;

        inputs!.Bootstrap.Values.Keys.Should().BeEquivalentTo(
            "gatewayUrl", "backendBaseUrl", "instanceId", "workloadToken",
            "mqttBrokerUrl", "mqttUsername", "mqttPassword", "specHash");
        inputs.Bootstrap.Values["instanceId"].Value.Should().Be(instance.Id.ToString("D"));
        inputs.Bootstrap.Values["specHash"].Should().BeSameAs(BootstrapValue.SpecHash);
        inputs.Bootstrap.Values["mqttBrokerUrl"].Value.Should().Be("mqtt://broker:1883");
        inputs.Bootstrap.Values["gatewayUrl"].Value.Should().Be($"https://reeve.example/ws/workloads/{instance.Id:D}");
        inputs.Bootstrap.Values["workloadToken"].CredentialId.Should().Be(FakeHostBootstrapSource.TokenCredentialId);
    }

    [Fact]
    public async Task The_broker_password_rides_a_secret_reference_and_never_a_literal()
    {
        var inputs = (await Provider(Read(new InstalledGroupManifestRead { Bundled = Manifest() }))
            .GetInputsAsync(Instance())).Inputs;

        var password = inputs!.Bootstrap.Values["mqttPassword"];
        password.Value.Should().BeNull();
        password.SecretRefName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_empty_host_renders_only_the_librarys_own_keys()
    {
        var inputs = (await Provider(
                Read(new InstalledGroupManifestRead { Bundled = Manifest() }),
                bootstrap: new EmptyWorkloadHostBootstrapSource())
            .GetInputsAsync(Instance())).Inputs;

        inputs!.Bootstrap.Values.Keys.Should().BeEquivalentTo("instanceId", "specHash");
        inputs.Bootstrap.HostEnv.Should().BeEmpty();
        inputs.Bootstrap.ProvisionedCredentials.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unbound_instance_takes_its_names_from_the_namer_of_the_effective_provider()
    {
        var manual = new TaggedResourceNamer(ManualProvider.Id, "manual-namer");
        var instance = Instance();
        var host = new FakeHostBootstrapSource();

        var inputs = (await Provider(
                Read(new InstalledGroupManifestRead { Bundled = Manifest() }),
                bootstrap: host,
                namers: [new TaggedResourceNamer(DockerProvider.Id, "decoy"), manual])
            .GetInputsAsync(instance)).Inputs;

        var secret = $"manual-namer-secret-{GroupId}-{WorkloadId}-{instance.Inst8}";
        inputs!.Namer.Should().BeSameAs(manual);
        inputs.EffectiveProvider.Id.Should().Be(ManualProvider.Id);
        host.Requests.Should().ContainSingle().Which.SecretName.Should().Be(secret);
        inputs.Bootstrap.SecretName.Should().Be(secret);
        inputs.Bootstrap.Values["mqttPassword"].SecretRefName.Should().Be(secret);
    }

    [Fact]
    public async Task An_unbound_instance_joins_the_provider_its_group_is_already_bound_to()
    {
        var docker = new TaggedResourceNamer(DockerProvider.Id, "docker-namer");
        var instance = Instance();
        var sibling = WorkloadInstance.Create(Guid.NewGuid(), instance.GroupInstanceId, GroupId, "sibling", At);
        sibling.SetProvider(DockerProvider.Id, At);
        var host = new FakeHostBootstrapSource();

        var inputs = (await Provider(
                Read(new InstalledGroupManifestRead { Bundled = Manifest() }),
                bootstrap: host,
                namers: [docker, new TaggedResourceNamer(ManualProvider.Id, "decoy")],
                groupRows: [instance, sibling])
            .GetInputsAsync(instance)).Inputs;

        inputs!.Namer.Should().BeSameAs(docker,
            "a workload of a group already deployed on Docker joins it, whatever auto-detect answers for its first apply");
        inputs.EffectiveProvider.Id.Should().Be(DockerProvider.Id);
        host.Requests.Should().ContainSingle().Which.Provider.Should().Be(DockerProvider.Id);
    }

    [Fact]
    public async Task An_unbound_instance_of_a_group_bound_to_different_providers_is_refused()
    {
        var instance = Instance();
        var onDocker = WorkloadInstance.Create(Guid.NewGuid(), instance.GroupInstanceId, GroupId, "a", At);
        onDocker.SetProvider(DockerProvider.Id, At);
        var onKubernetes = WorkloadInstance.Create(Guid.NewGuid(), instance.GroupInstanceId, GroupId, "b", At);
        onKubernetes.SetProvider(KubernetesProvider.Id, At);

        var answer = await Provider(
                Read(new InstalledGroupManifestRead { Bundled = Manifest() }),
                groupRows: [instance, onDocker, onKubernetes])
            .GetInputsAsync(instance);

        answer.IsProviderBindingConflict.Should().BeTrue();
        answer.Inputs.Should().BeNull();
        answer.Refusal.Should().Contain("different providers",
            "every reader that names a refusal shows the operator why the workload cannot deploy");
    }

    [Fact]
    public async Task A_bound_instance_is_reconciled_on_its_bound_provider_whatever_the_effective_one_is()
    {
        var docker = new TaggedResourceNamer(DockerProvider.Id, "docker-namer");
        var instance = Instance();
        instance.SetProvider(DockerProvider.Id, At);
        var host = new FakeHostBootstrapSource();

        var inputs = (await Provider(
                Read(new InstalledGroupManifestRead { Bundled = Manifest() }),
                bootstrap: host,
                namers: [docker, new TaggedResourceNamer(ManualProvider.Id, "decoy")])
            .GetInputsAsync(instance)).Inputs;

        var secret = $"docker-namer-secret-{GroupId}-{WorkloadId}-{instance.Inst8}";
        inputs!.Namer.Should().BeSameAs(docker,
            "an auto-detect answer for the installation must not move a deployed workload to another provider's names");
        inputs.EffectiveProvider.Id.Should().Be(DockerProvider.Id);
        var request = host.Requests.Should().ContainSingle().Subject;
        request.Provider.Should().Be(DockerProvider.Id);
        request.SecretName.Should().Be(secret);
    }

    [Fact]
    public async Task The_host_is_asked_about_this_workload_on_the_effective_provider()
    {
        var instance = Instance();
        var host = new FakeHostBootstrapSource();
        var workload = Manifest().Workloads[0] with
        {
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Http },
            Env =
            [
                new WorkloadEnv { Name = "BROKER", From = "bootstrap:mqttBrokerUrl" },
                new WorkloadEnv { Name = "ID", From = "bootstrap:instanceId" },
                new WorkloadEnv { Name = "PLAIN", From = "literal:x" },
            ],
        };

        await Provider(
                Read(new InstalledGroupManifestRead { Bundled = new DeploymentManifest { SchemaVersion = 1, Workloads = [workload] } }),
                bootstrap: host)
            .GetInputsAsync(instance);

        var request = host.Requests.Should().ContainSingle().Subject;
        request.InstanceId.Should().Be(instance.Id);
        request.Provider.Should().Be(ManualProvider.Id);
        request.WorkloadName.Should().Be(WorkloadId);
        request.Extensions.Should().BeSameAs(workload.Extensions);
        request.HealthMode.Should().Be(WorkloadHealthMode.Http);
        request.DeclaredKeys.Should().BeEquivalentTo("mqttBrokerUrl", "instanceId");
        request.Audience.Should().Be(WorkloadBootstrapAudience.Runtime);
    }

    [Fact]
    public async Task The_hosts_forced_env_and_provisioned_credentials_reach_the_context()
    {
        var forced = new ResolvedEnvVar { Name = "HOST_FORCED", Kind = ResolvedEnvKind.Literal, Value = "1" };
        var provisioned = new ProvisionedCredential("tok", "TOKEN_ENV");
        var host = new FakeHostBootstrapSource { HostEnv = [forced], ProvisionedCredentials = [provisioned] };

        var inputs = (await Provider(Read(new InstalledGroupManifestRead { Bundled = Manifest() }), bootstrap: host)
            .GetInputsAsync(Instance())).Inputs;

        inputs!.Bootstrap.HostEnv.Should().Equal(forced);
        inputs.Bootstrap.ProvisionedCredentials.Should().Equal(provisioned);
    }

    [Fact]
    public async Task A_host_refusal_is_answered_as_a_refusal_carrying_the_hosts_reason()
    {
        var host = new FakeHostBootstrapSource { Refusal = "host says no" };

        var answer = await Provider(Read(new InstalledGroupManifestRead { Bundled = Manifest() }), bootstrap: host)
            .GetInputsAsync(Instance());

        answer.Refusal.Should().Be("host says no");
        answer.Inputs.Should().BeNull();
        answer.Should().NotBe(WorkloadReconcileInputsResult.NotDeclared);
    }

    [Fact]
    public async Task An_absent_workload_is_not_declared_and_the_host_is_not_asked()
    {
        var host = new FakeHostBootstrapSource { Refusal = "host says no" };

        var answer = await Provider(Read(new InstalledGroupManifestRead { Bundled = Manifest("something-else") }), bootstrap: host)
            .GetInputsAsync(Instance());

        answer.Should().Be(WorkloadReconcileInputsResult.NotDeclared);
        host.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_present_workload_is_declared_with_its_inputs()
    {
        var answer = await Provider(Read(new InstalledGroupManifestRead { Bundled = Manifest() }))
            .GetInputsAsync(Instance());

        answer.Refusal.Should().BeNull();
        answer.Inputs.Should().NotBeNull();
        answer.Inputs!.WorkloadManifest.Id.Should().Be(WorkloadId);
    }

    private GroupManifestReconcileInputsProvider Provider(
        FakeManifests manifests,
        ProductImageOptions? images = null,
        IWorkloadHostBootstrapSource? bootstrap = null,
        IReadOnlyList<IWorkloadResourceNamer>? namers = null,
        IReadOnlyList<WorkloadInstance>? groupRows = null)
    {
        var instances = Substitute.For<IWorkloadInstanceRepository>();
        instances.ListByPluginInstanceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(groupRows ?? (IReadOnlyList<WorkloadInstance>)[]));
        return new(
            manifests,
            modules,
            new DefaultSettings(),
            new NoIdentity(),
            new WorkloadProviderRegistry([]),
            new WorkloadResourceNamerRegistry(namers ?? []),
            bootstrap ?? new FakeHostBootstrapSource(),
            Options.Create(images ?? new ProductImageOptions()),
            NullLogger<GroupManifestReconcileInputsProvider>.Instance,
            new WorkloadParameterSecretCodec(new EphemeralDataProtectionProvider()),
            new NoWorkloadParameterReferenceResolver(),
            instances);
    }

    private FakeManifests Read(InstalledGroupManifestRead read) => new FakeManifests().Install(modules, GroupId, read);

    private static WorkloadInstance Instance() =>
        WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, At);

    private static DeploymentManifest Manifest(string workloadId = WorkloadId) => new()
    {
        SchemaVersion = 1,
        Workloads =
        [
            new WorkloadManifest
            {
                Id = workloadId,
                DisplayName = workloadId,
                Image = new WorkloadImage { Component = "controller" },
                Kind = WorkloadKind.Service,
                Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            },
        ],
    };
}
