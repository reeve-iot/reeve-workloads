using Microsoft.AspNetCore.DataProtection;
using Reeve.Workloads.Providers.Docker;
using AuditEntry = Reeve.Workloads.Abstractions.WorkloadAuditEntry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Core;
using Reeve.Workloads.Core.Providers;
using Reeve.Workloads.Core.Tests;
using Reeve.Workloads.Core.Tests.Groups;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Providers.Kubernetes;
using Reeve.Workloads.Providers.Manual;
using Reeve.Workloads.Testing;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadLifecycleServiceTests
{
    private const string GroupId = "zwave";
    private const string WorkloadId = "zwave-js";


    [Fact]
    public async Task Materialize_creates_one_instance_per_manifest_workload()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(
            Workload("zwave-js"),
            Workload("zwave-ui")));
        var svc = h.Build();

        await h.MaterializeAsync(svc, pluginInstanceId);

        var rows = await h.Repository.ListByPluginInstanceAsync(pluginInstanceId);
        rows.Should().HaveCount(2);
        rows.Select(r => r.WorkloadId).Should().BeEquivalentTo(new[] { "zwave-js", "zwave-ui" });
        rows.Should().OnlyContain(r => r.GroupId == GroupId, "each row is stamped with the plugin type id");
    }

    [Fact]
    public async Task Materialize_is_idempotent_and_never_duplicates_rows()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var svc = h.Build();

        await h.MaterializeAsync(svc, pluginInstanceId);
        var firstId = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single().Id;

        await h.MaterializeAsync(svc, pluginInstanceId);

        var rows = await h.Repository.ListByPluginInstanceAsync(pluginInstanceId);
        rows.Should().ContainSingle("re-materializing the same live instance is a no-op (no duplicate rows)");
        rows.Single().Id.Should().Be(firstId, "the existing row's identity is preserved");
    }

    [Fact]
    public async Task Materialize_publishes_a_reconcile_nudge()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var svc = h.Build();

        await h.MaterializeAsync(svc, pluginInstanceId);

        h.Bus.Published.Should().Contain(p => p.Channel == WorkloadClusterChannels.ReconcileRequest,
            "materialize nudges the reconciler so the leader converges the new rows promptly");
    }

    [Fact]
    public async Task Materialize_nudges_the_reconciler_in_this_process_too()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var svc = h.Build();

        await h.MaterializeAsync(svc, pluginInstanceId);

        h.Bus.LocalTrigger.Requests.Should().Equal(((Guid?)null, "workload-materialize"));
    }

    [Fact]
    public async Task Materialize_for_a_plugin_with_no_deployment_creates_nothing_and_does_not_nudge()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var svc = h.Build();

        var result = await h.MaterializeAsync(svc, pluginInstanceId);

        result.Outcome.Should().Be(WorkloadMaterializeOutcome.NotDeclared);
        result.Message.Should().NotBeNullOrWhiteSpace();
        (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Should().BeEmpty();
        h.Modules.Rows.Should().BeEmpty("a refusal writes nothing, the module row included");
        h.Bus.Published.Should().BeEmpty("a plugin with no deployment section materializes nothing and nudges nothing");
    }


    [Fact]
    public async Task SetParameters_stores_the_values_advances_and_nudges()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var workload = Workload("zwave-js", parameters: new[] { RequiredParam("devicePath") });
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(workload));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        row.State.Should().Be(WorkloadInstanceState.PendingConfiguration);
        h.Bus.Published.Clear();

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["devicePath"] = "/dev/ttyUSB0" });

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.GetResolvedParameters().Should().ContainKey("devicePath").WhoseValue.Should().Be("/dev/ttyUSB0");
        stored.State.Should().Be(WorkloadInstanceState.PendingConfiguration,
            "still PendingConfiguration but now ready — the reconciler applies it (all required params present)");
        h.Bus.Published.Should().Contain(p => p.Channel == WorkloadClusterChannels.ReconcileRequest,
            "SetParameters nudges the reconciler");
    }

    [Fact]
    public async Task SetParameters_on_a_provider_without_user_namespace_support_drops_the_value()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload(WorkloadId)));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        row.SetProvider(DockerProvider.Id, h.Now);
        await h.Repository.UpdateAsync(row);

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["security.userNamespace"] = "true" });

        (await h.Repository.GetByIdAsync(row.Id))!.GetResolvedParameters()
            .Should().NotContainKey("security.userNamespace");
    }

    [Fact]
    public async Task SetParameters_installs_a_mixed_deployment_with_one_flat_user_namespace_value()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var hostNetworked = Workload("host-net") with { Network = new WorkloadNetwork { HostNetwork = true } };
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(hostNetworked, Workload("cluster-net")));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var flat = new Dictionary<string, string?> { ["security.userNamespace"] = "true" };

        foreach (var row in await h.Repository.ListByPluginInstanceAsync(pluginInstanceId))
        {
            await svc.SetParametersAsync(row.Id, flat);
            (await h.Repository.GetByIdAsync(row.Id))!.GetResolvedParameters()
                .Should().ContainKey("security.userNamespace").WhoseValue.Should().Be("true");
        }
    }

    [Fact]
    public async Task SetParameters_that_changes_a_password_on_a_deployed_instance_bumps_the_restart_generation()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var workload = Workload("zwave-js", parameters: new[]
        {
            new WorkloadParameter { Name = "apiPassword", Type = WorkloadParameterType.Password },
        });
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(workload));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        row.SetProvider(KubernetesProvider.Id, h.Now);
        row.SetAppliedSpecHash("sha256:applied", h.Now);
        row.SetResolvedParameters(h.ParameterSecrets.Protect(workload, new Dictionary<string, string?> { ["apiPassword"] = "old" }), h.Now);
        await h.Repository.UpdateAsync(row);
        var before = row.RestartGeneration;

        await svc.SetParametersAsync(row.Id,
            new Dictionary<string, string?> { ["apiPassword"] = "new" });

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.RestartGeneration.Should().Be(before + 1);
        stored.AppliedSpecHash.Should().BeNull();
    }

    [Fact]
    public async Task SetParameters_that_changes_a_password_during_an_active_update_does_not_bump_the_restart_generation()
    {
        var (h, row, svc) = await SeedDeployedPasswordInstanceAsync();
        h.IntentProvider.ActiveIntentId = Guid.NewGuid();
        var before = row.RestartGeneration;
        var appliedHash = row.AppliedSpecHash;

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["apiPassword"] = "new" });

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.RestartGeneration.Should().Be(before);
        stored.AppliedSpecHash.Should().Be(appliedHash);
        stored.GetResolvedParameters()["apiPassword"].Should().StartWith(WorkloadParameterSecretCodec.Prefix)
            .And.NotContain("new");
        h.ParameterSecrets.Unprotect(Workload("zwave-js", [new WorkloadParameter { Name = "apiPassword", Type = WorkloadParameterType.Password }]),
            stored.GetResolvedParameters())["apiPassword"].Should().Be("new");
    }

    [Fact]
    public async Task SetParameters_that_changes_a_password_on_a_frozen_instance_does_not_bump_the_restart_generation()
    {
        var (h, row, svc) = await SeedDeployedPasswordInstanceAsync();
        row.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, h.Now);
        row.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, h.Now);
        row.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, h.Now);
        await h.Repository.UpdateAsync(row);
        var before = row.RestartGeneration;
        var appliedHash = row.AppliedSpecHash;

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["apiPassword"] = "new" });

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.RestartGeneration.Should().Be(before);
        stored.AppliedSpecHash.Should().Be(appliedHash);
        stored.GetResolvedParameters()["apiPassword"].Should().StartWith(WorkloadParameterSecretCodec.Prefix)
            .And.NotContain("new");
        h.ParameterSecrets.Unprotect(Workload("zwave-js", [new WorkloadParameter { Name = "apiPassword", Type = WorkloadParameterType.Password }]),
            stored.GetResolvedParameters())["apiPassword"].Should().Be("new");
    }

    [Fact]
    public async Task SetParameters_that_changes_a_password_on_a_connected_manual_instance_does_not_roll_it()
    {
        var (h, row, svc) = await SeedDeployedPasswordInstanceAsync();
        row.SetProvider(ManualProvider.Id, h.Now);
        row.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, h.Now);
        row.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, h.Now);
        await h.Repository.UpdateAsync(row);
        var before = row.RestartGeneration;
        var appliedHash = row.AppliedSpecHash;

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["apiPassword"] = "new" });

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.RestartGeneration.Should().Be(before, "Reeve cannot restart the operator's deployment");
        stored.AppliedSpecHash.Should().Be(appliedHash);
        h.Issues.Raised.Should().BeEmpty("the operator sets the new value in their own deployment");
    }

    private static async Task<(Harness Harness, WorkloadInstance Row, WorkloadLifecycleService Service)> SeedDeployedPasswordInstanceAsync()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var workload = Workload("zwave-js", parameters: new[]
        {
            new WorkloadParameter { Name = "apiPassword", Type = WorkloadParameterType.Password },
        });
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(workload));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        row.SetProvider(KubernetesProvider.Id, h.Now);
        row.SetAppliedSpecHash("sha256:applied", h.Now);
        row.SetResolvedParameters(h.ParameterSecrets.Protect(workload, new Dictionary<string, string?> { ["apiPassword"] = "old" }), h.Now);
        await h.Repository.UpdateAsync(row);
        return (h, row, svc);
    }

    [Fact]
    public async Task SetParameters_with_the_same_password_does_not_bump_the_restart_generation()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var workload = Workload("zwave-js", parameters: new[]
        {
            new WorkloadParameter { Name = "apiPassword", Type = WorkloadParameterType.Password },
        });
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(workload));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        row.SetProvider(KubernetesProvider.Id, h.Now);
        row.SetResolvedParameters(h.ParameterSecrets.Protect(workload, new Dictionary<string, string?> { ["apiPassword"] = "same" }), h.Now);
        await h.Repository.UpdateAsync(row);
        var before = row.RestartGeneration;

        await svc.SetParametersAsync(row.Id,
            new Dictionary<string, string?> { ["apiPassword"] = "same" });

        (await h.Repository.GetByIdAsync(row.Id))!.RestartGeneration.Should().Be(before);
    }

    [Fact]
    public async Task The_first_SetParameters_on_a_new_instance_does_not_bump_the_restart_generation()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var workload = Workload("zwave-js", parameters: new[]
        {
            new WorkloadParameter { Name = "apiPassword", Type = WorkloadParameterType.Password },
        });
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(workload));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        var before = row.RestartGeneration;

        await svc.SetParametersAsync(row.Id,
            new Dictionary<string, string?> { ["apiPassword"] = "first" });

        (await h.Repository.GetByIdAsync(row.Id))!.RestartGeneration.Should().Be(before);
    }

    [Fact]
    public async Task SetParameters_stores_the_bound_devices_owning_group_id_for_a_group_auto_slot()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Inventory.With("/dev/dri/renderD128", 993);
        var workload = WorkloadWithDeviceSlot("transcoder", "acc", WorkloadDeviceGroup.Auto);
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(workload));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["acc"] = "/dev/dri/renderD128" });

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.GetResolvedParameters().Should().ContainKey("acc:gid").WhoseValue.Should().Be("993");
    }

    [Fact]
    public async Task SetParameters_leaves_no_group_id_when_the_bound_device_becomes_unknown()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Inventory.With("/dev/dri/renderD128", 993);
        var workload = WorkloadWithDeviceSlot("transcoder", "acc", WorkloadDeviceGroup.Auto);
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(workload));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["acc"] = "/dev/dri/renderD128" });
        (await h.Repository.GetByIdAsync(row.Id))!.GetResolvedParameters().Should().ContainKey("acc:gid");

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["acc"] = "/dev/dri/renderD200" });

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("acc:gid");
    }

    [Fact]
    public async Task SetParameters_rejects_a_device_path_outside_dev_and_never_persists_it()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var workload = new WorkloadManifest
        {
            Id = WorkloadId,
            DisplayName = WorkloadId,
            Image = new WorkloadImage { Ref = "registry.example/x", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Parameters = new[] { new WorkloadParameter { Name = "devicePath", Type = WorkloadParameterType.DevicePath, Required = true } },
            Privileges = new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "devicePath" } } },
        };
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(workload));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        var act = () => svc.SetParametersAsync(row.Id,
            new Dictionary<string, string?> { ["devicePath"] = "/etc/shadow" });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "a device path outside /dev/** would widen the mount and wedge the reconcile engine");
        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("devicePath",
            "the invalid value must never reach persistence");
    }

    [Fact]
    public async Task SetParameters_accepts_a_valid_device_path_under_dev()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var workload = new WorkloadManifest
        {
            Id = WorkloadId,
            DisplayName = WorkloadId,
            Image = new WorkloadImage { Ref = "registry.example/x", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
            Parameters = new[] { new WorkloadParameter { Name = "devicePath", Type = WorkloadParameterType.DevicePath, Required = true } },
            Privileges = new WorkloadPrivileges { Devices = new[] { new WorkloadDevice { Parameter = "devicePath" } } },
        };
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(workload));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["devicePath"] = "/dev/ttyUSB0" });

        (await h.Repository.GetByIdAsync(row.Id))!.GetResolvedParameters()
            .Should().ContainKey("devicePath").WhoseValue.Should().Be("/dev/ttyUSB0");
    }

    [Fact]
    public async Task SetParameters_rejects_a_node_selector_that_is_not_a_legal_kubernetes_label()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload(WorkloadId)));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        var act = () => svc.SetParametersAsync(row.Id,
            new Dictionary<string, string?> { ["nodeSelector"] = "intel.feature.node.kubernetes.io/gpu='true'" });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "the quotes are part of the label value, and no Kubernetes API server will ever accept them");
        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.GetResolvedParameters().Should().NotContainKey("nodeSelector",
            "a placement that could never apply must not reach persistence");
    }

    [Fact]
    public async Task SetParameters_accepts_a_legal_node_selector()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload(WorkloadId)));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        await svc.SetParametersAsync(row.Id,
            new Dictionary<string, string?> { ["nodeSelector"] = "intel.feature.node.kubernetes.io/gpu=true" });

        (await h.Repository.GetByIdAsync(row.Id))!.GetResolvedParameters()
            .Should().ContainKey("nodeSelector").WhoseValue.Should().Be("intel.feature.node.kubernetes.io/gpu=true");
    }

    [Fact]
    public async Task SetParameters_rejects_an_undeclared_parameter_name()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var workload = Workload("zwave-js", parameters: new[] { RequiredParam("devicePath") });
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(workload));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        var act = () => svc.SetParametersAsync(row.Id, new Dictionary<string, string?>
        {
            ["devicePath"] = "/dev/ttyUSB0",
            ["notAParam"] = "whatever",
        });

        (await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "an undeclared, non-reserved parameter name is a validation error (matches the method's doc)"))
            .Which.Message.Should().Be(
                "Unknown parameter(s) for workload 'zwave-js': notAParam. "
                + "Only the workload's declared and reserved parameters may be set.");
        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.GetResolvedParameters().Should().BeEmpty("the rejected edit never reaches persistence");
    }

    private static WorkloadManifest AcceleratorWorkload(string id) => new()
    {
        Id = id,
        DisplayName = id,
        Image = new WorkloadImage { Ref = "registry.example/" + id, Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
        Privileges = new WorkloadPrivileges
        {
            Devices = new[] { new WorkloadDevice { Parameter = "acc", MaxCount = 2, Group = WorkloadDeviceGroup.Auto } },
        },
        Parameters = new[] { new WorkloadParameter { Name = "acc", Type = WorkloadParameterType.DevicePath } },
    };

    private async Task<(Harness Harness, WorkloadInstance Row, WorkloadLifecycleService Service)> SeedAcceleratorAsync()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(AcceleratorWorkload("accel")));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        return (h, (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single(), svc);
    }

    [Theory]
    [InlineData("acc:gid")]
    [InlineData("acc[0]:gid")]
    public async Task SetParameters_refuses_a_colon_namespaced_name_even_though_it_IS_in_the_resolved_map(string submitted)
    {
        var (h, row, svc) = await SeedAcceleratorAsync();

        var act = () => svc.SetParametersAsync(row.Id, new Dictionary<string, string?>
        {
            ["acc[0]"] = "/dev/dri/renderD128",
            [submitted] = "999",
        });

        (await act.Should().ThrowAsync<WorkloadParameterValidationException>())
            .Which.Message.Should().Be(
                $"Reserved parameter name(s) for workload 'accel': {submitted}. "
                + "A name containing ':' carries a server-derived value and can never be set by a client.");
        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.GetResolvedParameters().Should().BeEmpty("fail-closed — the legitimate acc[0] beside it is rejected too");
    }

    [Fact]
    public async Task SetParameters_still_accepts_an_indexed_device_key_and_a_declared_name()
    {
        var (h, row, svc) = await SeedAcceleratorAsync();

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?>
        {
            ["acc"] = "/dev/dri/renderD127",
            ["acc[0]"] = "/dev/dri/renderD128",
            ["acc[1]"] = "/dev/dri/renderD129",
        });

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.GetResolvedParameters()["acc[0]"].Should().Be("/dev/dri/renderD128");
        stored.GetResolvedParameters()["acc[1]"].Should().Be("/dev/dri/renderD129");
        stored.GetResolvedParameters()["acc"].Should().Be("/dev/dri/renderD127");
    }

    [Fact]
    public async Task SetParameters_is_rejected_while_the_instance_is_Terminating()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        var svc = h.Build();

        var act = () => svc.SetParametersAsync(instance.Id, new Dictionary<string, string?> { ["x"] = "y" });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "no configuration mutations while tearing down (CanMutate)");
    }

    [Fact]
    public async Task SetParameters_with_a_valid_restore_existingClaim_persists_it_but_does_NOT_reattach_at_save()
    {
        const string retainedPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var retainedRowId = Guid.NewGuid();

        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(retainedRowId, GroupId, WorkloadId, "state", retainedPvc);
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });

        h.Retained.Reattached.Should().BeEmpty(
            "the retained row is marked reattached only on a successful adoption APPLY, never at parameter-save " +
            "(a restore that never deploys must leave the retained row ACTIVE)");
        (await h.Repository.GetByIdAsync(row.Id))!.GetResolvedParameters()
            .Should().ContainKey("existingClaim.state").WhoseValue.Should().Be(retainedPvc,
            "the existingClaim parameter is still persisted (the desired spec binds the volume to it)");
    }

    [Fact]
    public async Task A_restore_binding_is_refused_when_the_effective_provider_cannot_be_read()
    {
        const string retainedPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(Guid.NewGuid(), GroupId, WorkloadId, "state", retainedPvc);
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        h.Settings.Throws = true;

        var save = () => svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });

        var thrown = (await save.Should().ThrowAsync<WorkloadParameterValidationException>()).Which;
        thrown.Message.Should().Contain("effective provider (unknown)")
            .And.NotContain("()")
            .And.NotContain("WorkloadProviderDescriptor {");
    }

    [Fact]
    public async Task SetParameters_without_a_restore_existingClaim_marks_nothing_reattached()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(Guid.NewGuid(), GroupId, WorkloadId, "state", "reeve-wl-zwave-zwave-js-state-deadbeef");
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["existingClaim.state"] = "" });

        h.Retained.Reattached.Should().BeEmpty("a fresh install (no restore) reattaches no retained rows");
    }


    [Fact]
    public async Task SetParameters_rejects_an_existingClaim_that_names_no_active_retained_volume()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        var act = () => svc.SetParametersAsync(row.Id,
            new Dictionary<string, string?> { ["existingClaim.state"] = "reeve-wl-zwave-zwave-js-state-gone1234" });

        var thrown = (await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "an existingClaim that names no active retained volume is rejected fail-closed, not silently deployed fresh")).Which;
        thrown.Message.Should().Contain("effective provider (Kubernetes)")
            .And.NotContain("WorkloadProviderDescriptor {")
            .And.NotContain("DisplayName =");
        (await h.Repository.GetByIdAsync(row.Id))!.GetResolvedParameters()
            .Should().NotContainKey("existingClaim.state", "the rejected value never reaches persistence");
    }

    [Fact]
    public async Task SetParameters_rejects_an_existingClaim_that_names_another_plugins_retained_volume()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        const string foreignPvc = "reeve-wl-other-plugin-state-deadbeef";
        h.Retained.SeedActive(Guid.NewGuid(), "other-plugin", "other-wl", "state", foreignPvc);
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        var act = () => svc.SetParametersAsync(row.Id,
            new Dictionary<string, string?> { ["existingClaim.state"] = foreignPvc });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "a foreign-plugin retained volume must not be adopted (fail-closed)");
    }

    [Fact]
    public async Task SetParameters_rejects_a_syntactically_invalid_existingClaim_value()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        var act = () => svc.SetParametersAsync(row.Id,
            new Dictionary<string, string?> { ["existingClaim.state"] = "Not A Valid Name!" });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "a value that is not a valid DNS-1123 subdomain is rejected at save");
    }

    [Fact]
    public async Task SetParameters_accepts_a_valid_active_existingClaim()
    {
        const string retainedPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(Guid.NewGuid(), GroupId, WorkloadId, "state", retainedPvc);
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });

        (await h.Repository.GetByIdAsync(row.Id))!.GetResolvedParameters()
            .Should().ContainKey("existingClaim.state").WhoseValue.Should().Be(retainedPvc);
    }


    [Fact]
    public async Task Materialize_with_a_restore_binding_preset_holds_the_row_so_the_drift_pass_cannot_deploy_fresh()
    {
        const string retainedPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(Guid.NewGuid(), GroupId, WorkloadId, "state", retainedPvc);
        var svc = h.Build();

        await h.MaterializeAsync(svc,
            pluginInstanceId,
            suppressReconcileNudge: true,
            installDeploymentParameters: new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });

        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        row.RestoreBindingUnresolved.Should().BeTrue(
            "a restore-bound install pre-sets the hold at materialize so the drift pass holds the row until the bind lands");
    }

    [Fact]
    public async Task Materialize_without_a_restore_binding_does_not_preset_the_hold()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        var svc = h.Build();

        await h.MaterializeAsync(svc,
            pluginInstanceId,
            suppressReconcileNudge: true,
            installDeploymentParameters: new Dictionary<string, string?> { ["existingClaim.state"] = "" });

        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        row.RestoreBindingUnresolved.Should().BeFalse("a fresh install (blank existingClaim) is never held");
    }

    [Fact]
    public async Task A_successful_bind_clears_the_preset_restore_hold_so_the_deploy_can_proceed()
    {
        const string retainedPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(Guid.NewGuid(), GroupId, WorkloadId, "state", retainedPvc);
        var svc = h.Build();

        await h.MaterializeAsync(svc,
            pluginInstanceId,
            suppressReconcileNudge: true,
            installDeploymentParameters: new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        row.RestoreBindingUnresolved.Should().BeTrue("pre-set at materialize");

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });

        (await h.Repository.GetByIdAsync(row.Id))!.RestoreBindingUnresolved.Should().BeFalse(
            "the successful, verified+claimed bind is the only legitimate non-uninstall clear of the hold");
    }

    [Fact]
    public async Task A_rejected_bind_leaves_the_preset_restore_hold_set()
    {
        const string presentPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        var svc = h.Build();
        await h.MaterializeAsync(svc,
            pluginInstanceId,
            suppressReconcileNudge: true,
            installDeploymentParameters: new Dictionary<string, string?> { ["existingClaim.state"] = presentPvc });
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        row.RestoreBindingUnresolved.Should().BeTrue("pre-set at materialize");

        var act = () => svc.SetParametersAsync(row.Id,
            new Dictionary<string, string?> { ["existingClaim.state"] = "reeve-wl-zwave-zwave-js-state-gone1234" });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>();
        (await h.Repository.GetByIdAsync(row.Id))!.RestoreBindingUnresolved.Should().BeTrue(
            "a rejected bind never clears the hold — the row stays held, never deploys fresh");
    }

    [Fact]
    public async Task A_manifest_miss_with_a_restore_binding_keeps_the_hold_and_does_NOT_persist_unvalidated()
    {
        const string retainedPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var retainedRowId = Guid.NewGuid();
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(retainedRowId, GroupId, WorkloadId, "state", retainedPvc);
        var svc = h.Build();
        await h.MaterializeAsync(svc,
            pluginInstanceId,
            suppressReconcileNudge: true,
            installDeploymentParameters: new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        row.RestoreBindingUnresolved.Should().BeTrue("pre-set at materialize");

        h.Context.Forget(pluginInstanceId);

        var act = () => svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "a manifest-miss carrying a restore binding must PARK (throw), never silently persist unvalidated");
        var after = (await h.Repository.GetByIdAsync(row.Id))!;
        after.RestoreBindingUnresolved.Should().BeTrue(
            "the hold stays SET — the unvalidated bind never cleared it");
        after.GetResolvedParameters().Should().NotContainKey("existingClaim.state",
            "nothing is persisted on a manifest-miss + restore binding (the bind was never validated/claimed)");
        h.Retained.Row(retainedRowId)!.ClaimedByWorkloadInstanceId.Should().BeNull(
            "the retained row is never claimed by an unvalidated bind");
    }

    [Fact]
    public async Task After_a_manifest_scan_recovers_the_restore_binds_validates_and_clears_the_hold_normally()
    {
        const string retainedPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var retainedRowId = Guid.NewGuid();
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(retainedRowId, GroupId, WorkloadId, "state", retainedPvc);
        var svc = h.Build();
        await h.MaterializeAsync(svc,
            pluginInstanceId,
            suppressReconcileNudge: true,
            installDeploymentParameters: new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        h.Context.Forget(pluginInstanceId);
        await ((Func<Task>)(() => svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc })))
            .Should().ThrowAsync<WorkloadParameterValidationException>();
        (await h.Repository.GetByIdAsync(row.Id))!.RestoreBindingUnresolved.Should().BeTrue("still held during the miss");

        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });

        var after = (await h.Repository.GetByIdAsync(row.Id))!;
        after.RestoreBindingUnresolved.Should().BeFalse("after recovery the verified+claimed bind clears the hold");
        after.GetResolvedParameters().Should().ContainKey("existingClaim.state").WhoseValue.Should().Be(retainedPvc);
        h.Retained.Row(retainedRowId)!.ClaimedByWorkloadInstanceId.Should().Be(row.Id,
            "after recovery the retained row is atomically claimed by this install");
    }

    [Fact]
    public async Task A_manifest_miss_WITHOUT_a_restore_binding_still_persists_best_effort()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload(WorkloadId, parameters: new[] { RequiredParam("devicePath") })));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        h.Context.Forget(pluginInstanceId);

        await svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["devicePath"] = "/dev/ttyUSB0" });

        (await h.Repository.GetByIdAsync(row.Id))!.GetResolvedParameters()
            .Should().ContainKey("devicePath").WhoseValue.Should().Be("/dev/ttyUSB0",
            "a non-restore edit during a manifest-miss still persists best-effort (unchanged behaviour)");
    }

    [Fact]
    public async Task SetParameters_rejects_a_late_existingClaim_on_an_already_deployed_instance()
    {
        const string retainedPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(Guid.NewGuid(), GroupId, WorkloadId, "state", retainedPvc);
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        row.SetProvider(KubernetesProvider.Id, h.Now);
        row.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, h.Now);
        row.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, h.Now);
        await h.Repository.UpdateAsync(row);

        var act = () => svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "existingClaim is install-time only — rejected once the instance has been deployed");
    }


    [Fact]
    public async Task Two_installs_racing_to_restore_the_same_row_exactly_one_wins_the_other_is_rejected()
    {
        const string retainedPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var retainedRowId = Guid.NewGuid();
        var h = new Harness();

        var pluginInstanceA = Guid.NewGuid();
        var pluginInstanceB = Guid.NewGuid();
        h.Context.Seed(pluginInstanceA, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Context.Seed(pluginInstanceB, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(retainedRowId, GroupId, WorkloadId, "state", retainedPvc);
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceA);
        await h.MaterializeAsync(svc, pluginInstanceB);
        var rowA = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceA)).Single();
        var rowB = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceB)).Single();

        await svc.SetParametersAsync(rowA.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });

        var actB = () => svc.SetParametersAsync(rowB.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });
        await actB.Should().ThrowAsync<WorkloadParameterValidationException>(
            "a retained row can be adopted by at most ONE instance — the loser is rejected fail-closed");

        h.Retained.Row(retainedRowId)!.ClaimedByWorkloadInstanceId.Should().Be(rowA.Id,
            "the row stays claimed by the winner; the loser never overwrites the claim");
        (await h.Repository.GetByIdAsync(rowB.Id))!.GetResolvedParameters()
            .Should().NotContainKey("existingClaim.state", "the losing install's binding never persists");
    }

    [Fact]
    public async Task Deleting_the_winning_restore_before_deploy_releases_the_claim_so_the_row_is_offerable_again()
    {
        const string retainedPvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var retainedRowId = Guid.NewGuid();
        var h = new Harness();

        var pluginInstanceA = Guid.NewGuid();
        h.Context.Seed(pluginInstanceA, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(retainedRowId, GroupId, WorkloadId, "state", retainedPvc);
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceA);
        var rowA = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceA)).Single();

        await svc.SetParametersAsync(rowA.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedPvc });
        h.Retained.Row(retainedRowId)!.ClaimedByWorkloadInstanceId.Should().Be(rowA.Id, "precondition: the row is claimed");

        await svc.BeginUninstallAsync(pluginInstanceA, WorkloadRetention.PurgeAll);
        var terminating = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceA)).Single();
        await svc.CompleteTeardownAsync(terminating);

        h.Retained.Row(retainedRowId)!.ClaimedByWorkloadInstanceId.Should().BeNull(
            "an abandoned restore releases the claim so the retained row is offerable again");
        (await h.Retained.ListOfferableByPluginTypeAsync(GroupId)).Should().ContainSingle()
            .Which.Id.Should().Be(retainedRowId, "the released row is offerable to the wizard again");
    }

    [Fact]
    public async Task A_multi_volume_restore_releases_the_first_claim_when_a_later_claim_fails_all_or_nothing()
    {
        const string statePvc = "reeve-wl-zwave-zwave-js-state-deadbeef";
        const string logsPvc = "reeve-wl-zwave-zwave-js-logs-cafef00d";
        var stateRowId = Guid.NewGuid();
        var logsRowId = Guid.NewGuid();
        var h = new Harness();
        var pluginInstanceId = Guid.NewGuid();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithTwoVolumes(WorkloadId)));
        h.Retained.SeedActive(stateRowId, GroupId, WorkloadId, "state", statePvc);
        h.Retained.SeedActive(logsRowId, GroupId, WorkloadId, "logs", logsPvc);
        h.Retained.SeedClaimedByOther(logsRowId, Guid.NewGuid());
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        var act = () => svc.SetParametersAsync(row.Id, new Dictionary<string, string?>
        {
            ["existingClaim.state"] = statePvc,
            ["existingClaim.logs"] = logsPvc,
        });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "a later claim losing rejects the whole multi-volume restore fail-closed");
        h.Retained.Row(stateRowId)!.ClaimedByWorkloadInstanceId.Should().BeNull(
            "all-or-nothing: the first claim taken this request is RELEASED when a later claim fails — never stranded");
        (await h.Repository.GetByIdAsync(row.Id))!.GetResolvedParameters()
            .Should().NotContainKey("existingClaim.state", "the rejected restore persists nothing");
    }


    [Fact]
    public async Task SetParameters_rejects_an_existingClaim_row_whose_provider_differs_from_the_effective_provider()
    {
        const string retainedName = "reeve-zwave-state-deadbeef";
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(Guid.NewGuid(), GroupId, WorkloadId, "state", retainedName, DockerProvider.Id);
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        var act = () => svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedName });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "a retained row must match the EFFECTIVE provider (a docker-scheme row under a k8s install = data-less restore)");
    }

    [Fact]
    public async Task SetParameters_rejects_an_existingClaim_bound_to_a_row_of_a_different_volume_id()
    {
        const string retainedName = "reeve-wl-zwave-zwave-js-config-deadbeef";
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));
        h.Retained.SeedActive(Guid.NewGuid(), GroupId, WorkloadId, "config", retainedName);
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        var act = () => svc.SetParametersAsync(row.Id, new Dictionary<string, string?> { ["existingClaim.state"] = retainedName });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "the retained row's VolumeId must match the target volume the existingClaim key names");
    }

    [Fact]
    public async Task SetParameters_rejects_the_same_existingClaim_value_bound_to_two_keys_in_one_request()
    {
        const string retainedName = "reeve-wl-zwave-zwave-js-state-deadbeef";
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(WorkloadWithTwoVolumes(WorkloadId)));
        h.Retained.SeedActive(Guid.NewGuid(), GroupId, WorkloadId, "state", retainedName);
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        var act = () => svc.SetParametersAsync(row.Id, new Dictionary<string, string?>
        {
            ["existingClaim.state"] = retainedName,
            ["existingClaim.logs"] = retainedName,
        });

        await act.Should().ThrowAsync<WorkloadParameterValidationException>(
            "a single retained value bound to two existingClaim keys is rejected (would duplicate/cross-delete rows)");
    }


    [Fact]
    public async Task BeginUninstall_marks_all_instances_Terminating_persists_retention_and_nudges()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js"), Workload("zwave-ui")));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        h.Bus.Published.Clear();

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.PurgeAll);

        var rows = await h.Repository.ListByPluginInstanceAsync(pluginInstanceId);
        rows.Should().OnlyContain(r => r.State == WorkloadInstanceState.Terminating);
        rows.Should().OnlyContain(r => r.StateReason == WorkloadStateReason.Uninstalling);
        rows.Should().OnlyContain(r => r.Retention == WorkloadRetention.PurgeAll, "the chosen retention is persisted on each row");
        h.Bus.Published.Should().Contain(p => p.Channel == WorkloadClusterChannels.ReconcileRequest,
            "uninstall nudges the reconciler to run the teardown");
    }

    [Fact]
    public async Task BeginUninstall_is_idempotent()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);
        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId))
            .Should().OnlyContain(r => r.State == WorkloadInstanceState.Terminating);
    }

    [Fact]
    public async Task BeginUninstall_snapshots_a_docker_bound_volume_with_the_docker_name_scheme()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId,
            new DeploymentManifest { SchemaVersion = 1, Workloads = new[] { WorkloadWithCriticalVolume(WorkloadId) } });
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var instance = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        instance.SetProvider(DockerProvider.Id, h.Now);
        instance.SetAppliedSpecHash("hash", h.Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, h.Now);
        await h.Repository.UpdateAsync(instance);

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        var snapshot = (await h.Repository.GetByIdAsync(instance.Id))!.GetRetainedVolumesSnapshot();
        snapshot.Should().ContainSingle();
        snapshot.Single().ResourceName.Should().Be(
            DockerResourceNamer.Instance.VolumeName(
                new WorkloadNamingSubject
                {
                    GroupId = GroupId,
                    WorkloadId = string.Empty,
                    Inst8 = instance.Inst8,
                },
                "state"),
            "a docker-bound instance retains a docker named volume (reeve-…, no -wl- segment), not a k8s PVC");
        snapshot.Single().VolumeNamingScheme.Should().Be(RetainedVolumeSchemeId.Docker,
            "the scheme is what a later restore resolves the name under, so it must come from the same namer");
    }

    [Fact]
    public async Task BeginUninstall_snapshots_a_manual_bound_volume_under_the_name_its_compose_artifact_created()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId,
            new DeploymentManifest { SchemaVersion = 1, Workloads = new[] { WorkloadWithCriticalVolume(WorkloadId) } });
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var instance = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        instance.SetProvider(Reeve.Workloads.Providers.Manual.ManualProvider.Id, h.Now);
        instance.SetAppliedSpecHash("hash", h.Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, h.Now);
        await h.Repository.UpdateAsync(instance);

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        var snapshot = (await h.Repository.GetByIdAsync(instance.Id))!.GetRetainedVolumesSnapshot();
        snapshot.Should().ContainSingle();
        snapshot.Single().ResourceName.Should().Be(
            DockerResourceNamer.Instance.VolumeName(
                new WorkloadNamingSubject { GroupId = GroupId, WorkloadId = WorkloadId, Inst8 = instance.Inst8 },
                "state"),
            "every manual artifact names the volume with the docker scheme, so the record must too");
        snapshot.Single().VolumeNamingScheme.Should().Be(RetainedVolumeSchemeId.Docker);
    }

    [Fact]
    public async Task BeginUninstall_of_a_RESTORED_instance_snapshots_the_ADOPTED_physical_name_not_a_fresh_one()
    {
        const string adoptedPvc = "reeve-wl-zwave-zwave-js-state-aaaaaaaa";
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId,
            new DeploymentManifest { SchemaVersion = 1, Workloads = new[] { WorkloadWithCriticalVolume(WorkloadId) } });
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var instance = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        instance.SetProvider(KubernetesProvider.Id, h.Now);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["existingClaim.state"] = adoptedPvc }, h.Now);
        instance.SetAppliedSpecHash("hash", h.Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, h.Now);
        await h.Repository.UpdateAsync(instance);

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        var snapshot = (await h.Repository.GetByIdAsync(instance.Id))!.GetRetainedVolumesSnapshot();
        snapshot.Should().ContainSingle();
        snapshot.Single().ResourceName.Should().Be(adoptedPvc,
            "a restored volume's retained pointer must stay the ADOPTED physical name across generations, never a " +
            "freshly-computed phantom name — so it can be restored again and never orphans the real data");
    }

    [Fact]
    public async Task A_dropped_workload_tears_down_keeping_the_volumes_its_last_declaration_names()
    {
        var h = new Harness();
        var row = h.SeedDeployed(Guid.NewGuid(), WorkloadId);
        var svc = h.Build();

        await svc.BeginTeardownAsync(row, WorkloadWithCriticalVolume(WorkloadId), "unused", "2.0.0");

        var stored = (await h.Repository.GetByIdAsync(row.Id))!;
        using var scope = new FluentAssertions.Execution.AssertionScope();
        stored.State.Should().Be(WorkloadInstanceState.Terminating, "the teardown pass removes the runtime and the row");
        stored.StateMessage.Should().Contain("2.0.0");
        stored.Retention.Should().Be(WorkloadRetention.RetainAll, "a release dropping a workload never deletes its data");
        stored.GetRetainedVolumesSnapshot().Should().ContainSingle().Which.VolumeId.Should().Be("state");
        h.Tokens.RevokedInstances.Should().Contain(row.Id, "a workload no version declares keeps no credential");
    }

    [Fact]
    public async Task A_dropped_workload_Reeve_does_not_own_loses_its_record_and_credential_only()
    {
        var h = new Harness();
        var row = h.SeedDisownedDeployed(Guid.NewGuid());
        var svc = h.Build();

        await svc.BeginTeardownAsync(row, WorkloadWithCriticalVolume(WorkloadId), "unused", "2.0.0");

        (await h.Repository.GetByIdAsync(row.Id)).Should().BeNull();
        h.Tokens.RevokedInstances.Should().Contain(row.Id, "the operator owns its runtime, as on an uninstall");
    }

    [Fact]
    public async Task BeginUninstall_of_a_never_deployed_instance_snapshots_no_retained_volumes()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId,
            new DeploymentManifest { SchemaVersion = 1, Workloads = new[] { WorkloadWithCriticalVolume(WorkloadId) } });
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        (await h.Repository.GetByIdAsync((await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single().Id))!
            .GetRetainedVolumesSnapshot().Should().BeEmpty("a never-deployed instance backs no PVC ⇒ no snapshot");
    }


    [Fact]
    public async Task CompleteTeardown_with_RetainAll_tears_down_retaining_volumes_and_deletes_the_row()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: true);
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeTrue();
        h.Provider.IsTracked(instance.Id).Should().BeFalse("the runtime resources are deleted");
        h.Provider.RetainedVolumes.Should().ContainKey(instance.Id,
            "RetainAll tears the runtime down but RETAINS volumes (retainVolumes:true)");
        (await h.Repository.GetByIdAsync(instance.Id)).Should().BeNull("the row is deleted after a successful teardown");
    }

    [Fact]
    public async Task CompleteTeardown_with_PurgeAll_tears_down_without_retaining_volumes()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.PurgeAll, withCriticalVolume: true);
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeTrue();
        h.Provider.IsTracked(instance.Id).Should().BeFalse();
        h.Provider.RetainedVolumes.Should().BeEmpty("PurgeAll tears the runtime down and DELETES volumes (retainVolumes:false)");
        (await h.Repository.GetByIdAsync(instance.Id)).Should().BeNull();
    }

    [Fact]
    public async Task CompleteTeardown_with_PurgeAll_RETAINS_a_volume_claimed_by_another_instances_restore()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.PurgeAll, withCriticalVolume: true);
        instance.GetRetainedVolumesSnapshot().Should().BeEmpty(
            "a PurgeAll instance carries no snapshot — the guard must NOT read one (Part-C: impossible-state fixture removed)");

        var resourceName = DefaultWorkloadResourceNamer.Instance.VolumeName(
                            new WorkloadNamingSubject
                            {
                                GroupId = GroupId,
                                WorkloadId = WorkloadId,
                                Inst8 = instance.Inst8,
                            },
                            "state");
        h.Retained.SeedActiveBySource(Guid.NewGuid(), GroupId, WorkloadId, "state", resourceName, instance.Id);
        var otherRowId = Guid.NewGuid();
        h.Retained.SeedActive(otherRowId, GroupId, WorkloadId, "state", resourceName);
        h.Retained.SeedClaimedByOther(otherRowId, Guid.NewGuid());
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeTrue("the teardown completes (the instance row is deleted)");
        h.Provider.RetainedVolumes.Should().ContainKey(instance.Id,
            "a PurgeAll teardown of a volume another restore is adopting UPGRADES to retainVolumes:true — never destroys it, derived from X's own recorded rows (not the nulled snapshot)");
        h.Retained.Row(otherRowId)!.IsActive.Should().BeTrue(
            "the claiming restore's row stays active — the physical volume survives for it");
        (await h.Repository.GetByIdAsync(instance.Id)).Should().BeNull("this instance's own row is still deleted");
    }

    [Fact]
    public async Task CompleteTeardown_of_a_mid_restore_instance_releases_its_own_claim_so_the_row_is_offerable_again()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        var claimedRowId = Guid.NewGuid();
        h.Retained.SeedActive(claimedRowId, GroupId, WorkloadId, "state", "reeve-wl-zwave-zwave-js-state-mid00001");
        h.Retained.SeedClaimedByOther(claimedRowId, instance.Id);
        h.Retained.Row(claimedRowId)!.ClaimedByWorkloadInstanceId.Should().Be(instance.Id, "precondition: this instance holds the claim");
        var svc = h.Build();

        (await svc.CompleteTeardownAsync(instance)).Should().BeTrue();

        h.Retained.ReleasedForInstance.Should().Contain(instance.Id,
            "a mid-restore instance's terminal teardown releases the claim it holds");
        h.Retained.Row(claimedRowId)!.ClaimedByWorkloadInstanceId.Should().BeNull(
            "the claimed row is offerable again — no phantom claimed-but-unadopted row survives the teardown");
        h.Retained.Row(claimedRowId)!.IsActive.Should().BeTrue(
            "the row's physical volume was never adopted by this instance and still exists — released, not purged");
    }

    [Fact]
    public async Task CompleteTeardown_C1_of_a_restored_instance_before_reattach_completes_without_livelocking()
    {
        var realRepo = new C1Nf3RetainedVolumeRepository();
        var h = new Harness { RealRetainedRepo = realRepo };
        const string sharedName = "reeve-wl-zwave-zwave-js-state-c1000001";

        var original = Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume.Record(
            Guid.NewGuid(), GroupId, WorkloadId, "state", sharedName, "1Gi",
            VolumeCriticality.Critical, description: null, KubernetesProvider.Id,
            Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolumeSchemeId.Kubernetes,
            sourceGroupInstanceId: Guid.NewGuid(), sourceWorkloadInstanceId: Guid.NewGuid(), h.Now);
        realRepo.Seed(original);

        var pluginInstanceId = Guid.NewGuid();
        var restored = WorkloadInstance.Create(Guid.NewGuid(), pluginInstanceId, GroupId, WorkloadId, h.Now);
        restored.SetProvider(KubernetesProvider.Id, h.Now);
        restored.SetAppliedSpecHash("hash", h.Now);
        restored.SetResolvedParameters(new Dictionary<string, string?> { ["existingClaim.state"] = sharedName }, h.Now);
        restored.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, h.Now);
        restored.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, h.Now);
        restored.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, h.Now);
        restored.SetRetention(WorkloadRetention.RetainAll, h.Now);
        restored.SnapshotRetainedVolumes(new[]
        {
            new RetainedVolumeSnapshotItem
            {
                VolumeId = "state",
                ResourceName = sharedName,
                Size = "1Gi",
                Criticality = VolumeCriticality.Critical,
                Description = "bonds",
            },
        }, h.Now);
        h.Repository.Add(restored);
        (await realRepo.TryClaimForRestoreAsync(original.Id, restored.Id)).Should().BeTrue();

        var svc = h.Build();

        var completed = await svc.CompleteTeardownAsync(restored);

        completed.Should().BeTrue("the teardown COMPLETES — the duplicate active-name record is absorbed, no index violation, no livelock");
        (await h.Repository.GetByIdAsync(restored.Id)).Should().BeNull("the row is deleted (terminal uninstall), not stuck Terminating forever");
        realRepo.Active.Should().ContainSingle("exactly one active row binds the physical volume — the durable data pointer is intact")
            .Which.Id.Should().Be(original.Id);
        realRepo.Active.Single().ClaimedByWorkloadInstanceId.Should().BeNull(
            "the restored instance's restore claim is RELEASED so the row is offerable/purgeable again");
    }

    [Fact]
    public async Task CompleteTeardown_with_RetainAll_records_the_retained_volumes_before_deleting_the_row()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: true);
        var svc = h.Build();

        await svc.CompleteTeardownAsync(instance);

        h.Retained.Recorded.Should().ContainSingle("RetainAll records the retained volumes durably before the row is deleted");
        var (recordedInstance, volumes) = h.Retained.Recorded.Single();
        recordedInstance.Id.Should().Be(instance.Id, "the retained volumes are recorded against the torn-down instance");
        volumes.Should().ContainSingle();
        var volume = volumes.Single();
        volume.VolumeId.Should().Be("state");
        volume.Size.Should().Be("1Gi");
        volume.Criticality.Should().Be(VolumeCriticality.Critical);
        volume.ResourceName.Should().Be(
            DefaultWorkloadResourceNamer.Instance.VolumeName(
                new WorkloadNamingSubject
                {
                    GroupId = GroupId,
                    WorkloadId = WorkloadId,
                    Inst8 = instance.Inst8,
                },
                "state"),
            "the provider PVC name is derived from the resource naming");
    }

    [Fact]
    public async Task CompleteTeardown_with_PurgeAll_records_no_retained_volumes()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.PurgeAll, withCriticalVolume: true);
        var svc = h.Build();

        await svc.CompleteTeardownAsync(instance);

        h.Retained.Recorded.Should().BeEmpty("PurgeAll is a destructive purge — nothing is retained, nothing is recorded");
    }

    [Fact]
    public async Task CompleteTeardown_with_RetainAll_but_no_volumes_records_nothing()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        var svc = h.Build();

        await svc.CompleteTeardownAsync(instance);

        h.Retained.Recorded.Should().BeEmpty("a RetainAll teardown of a no-volume workload records nothing");
    }

    [Fact]
    public async Task CompleteTeardown_clears_the_open_repairs_issues_of_the_row_it_deletes()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        var svc = h.Build();

        await svc.CompleteTeardownAsync(instance);

        h.Issues.Cleared.Should().Contain(WorkloadRepairsScanner.StuckCode(instance.WorkloadId));
    }

    [Fact]
    public async Task CompleteTeardown_clears_every_scanner_code_on_the_rows_plugin_instance_sink()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        var svc = h.Build();

        await svc.CompleteTeardownAsync(instance);

        h.Sinks.CreatedFor.Should().OnlyContain(id => id == instance.GroupInstanceId);
        h.Issues.Cleared.Should().BeEquivalentTo(new[]
        {
            WorkloadRepairsScanner.StuckCode(instance.WorkloadId),
            WorkloadRepairsScanner.CrashLoopCode(instance.WorkloadId),
            WorkloadRepairsScanner.VersionSkewCode(instance.WorkloadId),
            WorkloadRepairsScanner.ParkedCode(instance.WorkloadId),
            WorkloadRepairsScanner.RollbackFailedCode(instance.WorkloadId),
        });
    }

    [Fact]
    public async Task CompleteTeardown_that_retries_clears_nothing()
    {
        var h = new Harness { ThrowOnTeardown = true };
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        var svc = h.Build();

        (await svc.CompleteTeardownAsync(instance)).Should().BeFalse();

        h.Issues.Cleared.Should().BeEmpty("a row kept for retry is still evaluated by the scanner");
    }

    [Fact]
    public async Task CompleteTeardown_is_idempotent_a_second_call_after_deletion_is_a_no_op()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        var svc = h.Build();

        (await svc.CompleteTeardownAsync(instance)).Should().BeTrue();
        (await svc.CompleteTeardownAsync(instance)).Should().BeTrue("a second teardown after the row is deleted is a no-op");
    }

    [Fact]
    public async Task CompleteTeardown_when_the_provider_teardown_throws_does_not_delete_the_row_and_retries()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        h.ThrowOnTeardown = true;
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeFalse("a failed runtime teardown must be retried, never treated as complete");
        (await h.Repository.GetByIdAsync(instance.Id)).Should().NotBeNull(
            "a row whose runtime teardown FAILED is never deleted — the reconciler retries next pass");
        (await h.Repository.GetByIdAsync(instance.Id))!.State.Should().Be(WorkloadInstanceState.Terminating,
            "it stays Terminating so the next pass retries");
    }

    [Theory]
    [InlineData(WorkloadRetention.RetainAll)]
    [InlineData(WorkloadRetention.PurgeAll)]
    public async Task CompleteTeardown_whose_bounded_teardown_times_out_keeps_the_row_and_records_nothing(WorkloadRetention retention)
    {
        var h = new Harness { HangOnTeardownPastTheDeadline = true };
        var instance = h.SeedDeployedTerminating(retention, withCriticalVolume: true);
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeFalse("a teardown that ran past its deadline did not finish");
        (await h.Repository.GetByIdAsync(instance.Id))!.State.Should().Be(WorkloadInstanceState.Terminating);
        h.Retained.Recorded.Should().BeEmpty("the volume is not retained while the runtime may still hold it");
        h.Retained.PurgedForInstance.Should().BeEmpty("the volume is not purged while the provider never removed it");
    }

    [Fact]
    public async Task CompleteTeardown_cancelled_by_something_other_than_the_caller_is_retried_not_thrown()
    {
        var h = new Harness { TeardownFault = new TaskCanceledException("the HTTP client gave up") };
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: true);
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeFalse("only the caller's own cancellation stops the pass; a client's internal timeout is a failed teardown");
        (await h.Repository.GetByIdAsync(instance.Id))!.State.Should().Be(WorkloadInstanceState.Terminating);
        h.Retained.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteTeardown_whose_provider_fails_while_the_caller_cancels_is_retried_not_thrown()
    {
        var h = new Harness { ThrowOnTeardown = true };
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: true);
        var svc = h.Build();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var done = await svc.CompleteTeardownAsync(instance, cancelled.Token);

        done.Should().BeFalse("a provider failure is a failed teardown even when the caller is stopping at the same moment");
        (await h.Repository.GetByIdAsync(instance.Id))!.State.Should().Be(WorkloadInstanceState.Terminating);
        h.Retained.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteTeardown_of_a_never_deployed_instance_deletes_the_row_without_calling_the_provider()
    {
        var h = new Harness();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, h.Now);
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, h.Now);
        instance.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, h.Now);
        h.Repository.Add(instance);
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeTrue();
        (await h.Repository.GetByIdAsync(instance.Id)).Should().BeNull("a never-deployed Terminating row is deleted directly");
    }

    [Fact]
    public async Task CompleteTeardown_of_a_non_Terminating_instance_is_a_no_op_returning_false()
    {
        var h = new Harness();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, h.Now);
        h.Repository.Add(instance);
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeFalse("only a Terminating instance is torn down");
        (await h.Repository.GetByIdAsync(instance.Id)).Should().NotBeNull("a non-Terminating row is never deleted");
    }

    [Fact]
    public async Task CompleteTeardown_with_a_compiled_in_but_unresolvable_docker_provider_leaves_the_row_Terminating()
    {
        var h = new Harness { RegisteredProvider = null, BoundProvider = DockerProvider.Id };
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeFalse("a compiled-in provider whose runtime is unresolvable leaves the row Terminating");
        (await h.Repository.GetByIdAsync(instance.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task BeginUninstall_still_terminates_a_row_whose_deployment_context_is_already_gone()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId,
            new DeploymentManifest { SchemaVersion = 1, Workloads = new[] { WorkloadWithCriticalVolume(WorkloadId) } });
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var instance = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        instance.SetProvider(KubernetesProvider.Id, h.Now);
        instance.SetAppliedSpecHash("hash", h.Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, h.Now);
        instance.TransitionTo(WorkloadInstanceState.Degraded, WorkloadStateReason.HealthLost, null, h.Now);
        await h.Repository.UpdateAsync(instance);

        h.Context.Forget(pluginInstanceId);

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        var after = (await h.Repository.GetByIdAsync(instance.Id))!;
        after.State.Should().Be(WorkloadInstanceState.Terminating,
            "the teardown pass only owns Terminating rows, so a sweep that cannot reach that state does nothing");
        after.Retention.Should().Be(WorkloadRetention.RetainAll);
        after.GetRetainedVolumesSnapshot().Should().BeEmpty(
            "the manifest is unreachable with the instance gone, so the volume it declared cannot be named here");
    }

    [Fact]
    public async Task BeginUninstall_snapshots_retained_volumes_so_CompleteTeardown_records_them_even_after_the_context_is_gone()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId,
            new DeploymentManifest { SchemaVersion = 1, Workloads = new[] { WorkloadWithCriticalVolume(WorkloadId) } });
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var instance = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        instance.SetProvider(KubernetesProvider.Id, h.Now);
        instance.SetAppliedSpecHash("hash", h.Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, h.Now);
        await h.Repository.UpdateAsync(instance);

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        h.Context.Forget(pluginInstanceId);

        var terminating = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();
        var done = await svc.CompleteTeardownAsync(terminating);

        done.Should().BeTrue();
        h.Retained.Recorded.Should().ContainSingle(
            "the retained volume is recorded from the BeginUninstall snapshot, even though the context is now gone");
        var (_, volumes) = h.Retained.Recorded.Single();
        var volume = volumes.Single();
        volume.VolumeId.Should().Be("state");
        volume.Size.Should().Be("1Gi");
        volume.Criticality.Should().Be(VolumeCriticality.Critical);
        volume.ResourceName.Should().Be(
            DefaultWorkloadResourceNamer.Instance.VolumeName(
                new WorkloadNamingSubject
                {
                    GroupId = GroupId,
                    WorkloadId = WorkloadId,
                    Inst8 = terminating.Inst8,
                },
                "state"));
    }

    [Fact]
    public async Task CompleteTeardown_when_recording_the_retained_record_fails_does_not_delete_the_row_and_retries()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: true);
        h.Retained.ThrowOnRecord = true;
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeFalse("a failed durable retained-record write must be retried, never treated as complete");
        var reread = await h.Repository.GetByIdAsync(instance.Id);
        reread.Should().NotBeNull("the only durable retained-volume pointer must not be lost while the PVC still exists");
        reread!.State.Should().Be(WorkloadInstanceState.Terminating, "it stays Terminating so the next pass retries");
    }

    [Fact]
    public async Task CompleteTeardown_of_a_never_deployed_instance_with_a_snapshot_records_nothing()
    {
        var h = new Harness();
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, h.Now);
        instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, h.Now);
        instance.SetRetention(WorkloadRetention.RetainAll, h.Now);
        instance.SnapshotRetainedVolumes(new[]
        {
            new RetainedVolumeSnapshotItem
            {
                VolumeId = "state",
                ResourceName = "reeve-wl-zwave-zwave-js-state-abc12345",
                Size = "1Gi",
                Criticality = VolumeCriticality.Critical,
            },
        }, h.Now);
        instance.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, h.Now);
        h.Repository.Add(instance);
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeTrue("a never-deployed Terminating row is cleaned up");
        h.Retained.Recorded.Should().BeEmpty("a never-deployed instance has no PVC — nothing is recorded (no phantom rows)");
        (await h.Repository.GetByIdAsync(instance.Id)).Should().BeNull();
    }

    [Fact]
    public async Task CompleteTeardown_when_a_compiled_in_provider_is_unresolvable_leaves_the_row_Terminating()
    {
        var h = new Harness { RegisteredProvider = null, BoundProvider = KubernetesProvider.Id };
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: true);
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeFalse("a compiled-in-but-unresolvable provider is a transient skew — retry, never delete");
        (await h.Repository.GetByIdAsync(instance.Id)).Should().NotBeNull(
            "deleting would silently orphan the runtime pods/PVCs");
        h.Retained.Recorded.Should().BeEmpty("no retained record is written when teardown did not actually run");
    }


    [Fact]
    public async Task CompleteTeardown_of_an_uninstall_initiated_row_deletes_it()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: true);
        instance.StateReason.Should().Be(WorkloadStateReason.Uninstalling, "precondition: uninstall-initiated");
        var svc = h.Build();

        await svc.CompleteTeardownAsync(instance);

        (await h.Repository.GetByIdAsync(instance.Id)).Should().BeNull("an uninstall-initiated Terminating row is terminal — deleted after teardown");
    }

    [Fact]
    public async Task Uninstalling_a_disowned_instance_retains_the_operator_runtime_drops_the_row_and_audits_the_retention()
    {
        var h = new Harness();
        var pluginInstanceId = Guid.NewGuid();
        var instance = h.SeedDisownedDeployed(pluginInstanceId);
        h.Provider.IsTracked(instance.Id).Should().BeTrue("the operator-owned runtime is live before uninstall");
        var svc = h.Build();

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        h.Provider.IsTracked(instance.Id).Should().BeTrue(
            "uninstalling a disowned instance NEVER tears down the operator-owned runtime");
        (await h.Repository.GetByIdAsync(instance.Id)).Should().BeNull("Reeve drops its own workload row on uninstall (record only)");
        h.Tokens.RevokedInstances.Should().Contain(instance.Id, "uninstall still revokes the workload token");
        h.Audit.Entries.Should().ContainSingle(e => e.Action == "workloads.uninstall.runtime-retained")
            .Which.EntityId.Should().Be(instance.Id);
    }


    [Fact]
    public async Task Uninstalling_a_disowned_instance_clears_its_open_repairs_issues_on_its_plugin_instance_sink()
    {
        var h = new Harness();
        var pluginInstanceId = Guid.NewGuid();
        var instance = h.SeedDisownedDeployed(pluginInstanceId);
        var svc = h.Build();

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        h.Sinks.CreatedFor.Should().OnlyContain(id => id == pluginInstanceId);
        h.Issues.Cleared.Should().Contain(WorkloadRepairsScanner.StuckCode(instance.WorkloadId));
    }

    [Fact]
    public async Task Uninstalling_a_mid_switch_instance_tears_down_the_runtime_reeve_still_owns()
    {
        var h = new Harness();
        var pluginInstanceId = Guid.NewGuid();
        var instance = h.SeedSwitchingDeployed(pluginInstanceId);
        h.Provider.IsTracked(instance.Id).Should().BeTrue("the mid-switch runtime is live before uninstall");
        var svc = h.Build();

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        var flipped = (await h.Repository.GetByIdAsync(instance.Id))!;
        flipped.State.Should().Be(WorkloadInstanceState.Terminating,
            "a switch that has not committed leaves the runtime Reeve's, so the uninstall tears it down");
        h.Tokens.RevokedInstances.Should().Contain(instance.Id);

        (await svc.CompleteTeardownAsync(flipped)).Should().BeTrue();

        h.Provider.IsTracked(instance.Id).Should().BeFalse("the runtime on the bound provider is removed");
        (await h.Repository.GetByIdAsync(instance.Id)).Should().BeNull();
        h.Audit.Entries.Should().NotContain(e => e.Action == "workloads.uninstall.runtime-retained");
    }

    [Fact]
    public async Task Uninstalling_a_mid_switch_instance_whose_switch_committed_first_drops_only_the_row()
    {
        var h = new Harness();
        var pluginInstanceId = Guid.NewGuid();
        var row = h.SeedSwitchingDeployed(pluginInstanceId);
        h.Repository.RaceWith(row.Id, () => h.StoredCopy(row, WorkloadInstanceState.ExternallyManaged));

        await h.Build().BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        (await h.Repository.GetByIdAsync(row.Id)).Should().BeNull();
        h.Provider.IsTracked(row.Id).Should().BeTrue("the committed switch handed the runtime to the operator");
        h.Audit.Entries.Should().ContainSingle(e => e.Action == "workloads.uninstall.runtime-retained");
    }

    [Fact]
    public async Task Forget_refuses_a_running_workload()
    {
        var h = new Harness();
        var row = h.SeedDeployed(Guid.NewGuid(), WorkloadId);

        var result = await h.Build().ForgetAsync(row.Id, actorUserId: null);

        result.Outcome.Should().Be(WorkloadForgetOutcome.NotTearingDown);
        result.BlockingState.Should().Be(WorkloadInstanceState.Healthy);
        (await h.Repository.GetByIdAsync(row.Id)).Should().NotBeNull();
        h.Tokens.RevokedInstances.Should().BeEmpty();
        h.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Forget_refuses_a_mid_switch_workload_which_an_uninstall_tears_down()
    {
        var h = new Harness();
        var row = h.SeedSwitchingDeployed(Guid.NewGuid());

        var result = await h.Build().ForgetAsync(row.Id, actorUserId: null);

        result.Outcome.Should().Be(WorkloadForgetOutcome.NotTearingDown);
        result.BlockingState.Should().Be(WorkloadInstanceState.Switching);
        (await h.Repository.GetByIdAsync(row.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Forget_refuses_a_teardown_that_has_not_been_failing_past_the_stuck_threshold()
    {
        var h = new Harness();
        var row = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false,
            terminatingSince: h.Now - TimeSpan.FromMinutes(15));

        var result = await h.Build().ForgetAsync(row.Id, actorUserId: null);

        result.Outcome.Should().Be(WorkloadForgetOutcome.TeardownNotStuck);
        result.EligibleAtUtc.Should().Be(h.Now);
        (await h.Repository.GetByIdAsync(row.Id)).Should().NotBeNull();
        h.Tokens.RevokedInstances.Should().BeEmpty();
    }

    [Fact]
    public async Task Forget_refuses_while_an_update_of_the_module_is_in_progress()
    {
        var h = new Harness();
        var row = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false,
            terminatingSince: h.Now - TimeSpan.FromHours(1));
        var intentId = Guid.NewGuid();
        h.IntentProvider.ActiveIntentId = intentId;

        var result = await h.Build().ForgetAsync(row.Id, actorUserId: null);

        result.Outcome.Should().Be(WorkloadForgetOutcome.UpdateInProgress);
        result.ActiveIntentId.Should().Be(intentId);
        (await h.Repository.GetByIdAsync(row.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Forget_drops_a_stuck_teardown_and_reports_what_it_left_on_the_environment()
    {
        var h = new Harness();
        var actor = Guid.NewGuid();
        var since = h.Now - TimeSpan.FromHours(1);
        var row = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: true, terminatingSince: since);
        row.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling,
            "Teardown failed: docker socket gone (test)", h.Now);

        var result = await h.Build().ForgetAsync(row.Id, actor);

        result.Outcome.Should().Be(WorkloadForgetOutcome.Forgotten);
        (await h.Repository.GetByIdAsync(row.Id)).Should().BeNull();
        h.Provider.IsTracked(row.Id).Should().BeTrue("forgetting never calls the provider; the runtime is the operator's now");
        h.Tokens.RevokedInstances.Should().Contain(row.Id);
        h.Retained.Recorded.Should().BeEmpty("a volume a leftover runtime may still mount is never offered to a restore or a purge");
        h.Issues.Cleared.Should().Contain(WorkloadRepairsScanner.StuckCode(row.WorkloadId));

        var left = result.LeftBehind!;
        left.InstanceId.Should().Be(row.Id);
        left.Provider.Should().Be(KubernetesProvider.Id);
        left.Retention.Should().Be(WorkloadRetention.RetainAll);
        left.TerminatingSinceUtc.Should().Be(since);
        left.LastTeardownMessage.Should().Be("Teardown failed: docker socket gone (test)");
        left.Volumes.Should().ContainSingle().Which.VolumeId.Should().Be("state");
        left.CredentialRevokeError.Should().BeNull();

        var audit = h.Audit.Entries.Should().ContainSingle(e => e.Action == "workloads.forget").Which;
        audit.ActorUserId.Should().Be(actor);
        audit.EntityId.Should().Be(row.Id);
    }

    [Fact]
    public async Task Forget_keeps_the_restore_claims_and_lists_them_as_left_behind()
    {
        var h = new Harness();
        var row = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false,
            terminatingSince: h.Now - TimeSpan.FromHours(1));
        var claimedId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        h.Retained.SeedActive(claimedId, GroupId, WorkloadId, "state", "reeve-wl-claimed-state");
        h.Retained.SeedActive(otherId, GroupId, WorkloadId, "data", "reeve-wl-unclaimed-data");
        (await h.Retained.TryClaimForRestoreAsync(claimedId, row.Id)).Should().BeTrue();

        var result = await h.Build().ForgetAsync(row.Id, actorUserId: null);

        result.Outcome.Should().Be(WorkloadForgetOutcome.Forgotten);
        h.Retained.ReleasedForInstance.Should().BeEmpty();
        h.Retained.Row(claimedId)!.ClaimedByWorkloadInstanceId.Should().Be(row.Id);
        result.LeftBehind!.RestoreClaims.Should().Equal("reeve-wl-claimed-state");
    }

    [Fact]
    public async Task Forget_lists_the_declared_volumes_of_a_purging_uninstall_as_left_behind()
    {
        var h = new Harness();
        var row = h.SeedDeployedTerminating(WorkloadRetention.PurgeAll, withCriticalVolume: true,
            terminatingSince: h.Now - TimeSpan.FromHours(1));
        h.Context.Seed(row.GroupInstanceId, GroupId, DeploymentWith(WorkloadWithCriticalVolume(WorkloadId)));

        var result = await h.Build().ForgetAsync(row.Id, actorUserId: null);

        result.Outcome.Should().Be(WorkloadForgetOutcome.Forgotten);
        result.LeftBehind!.Volumes.Should().ContainSingle().Which.VolumeId.Should().Be("state");
        h.Retained.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Forget_drops_the_row_when_the_revoke_fails_and_reports_the_credentials_left()
    {
        var h = new Harness();
        var row = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false,
            terminatingSince: h.Now - TimeSpan.FromHours(1));
        h.Tokens.Throws = true;

        var result = await h.Build().ForgetAsync(row.Id, actorUserId: null);

        result.Outcome.Should().Be(WorkloadForgetOutcome.Forgotten);
        (await h.Repository.GetByIdAsync(row.Id)).Should().BeNull();
        result.LeftBehind!.CredentialRevokeError.Should().Be("credential plane unavailable (test)");
    }

    [Fact]
    public async Task Forget_of_a_forgotten_workload_answers_not_found_and_does_nothing()
    {
        var h = new Harness();
        var row = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false,
            terminatingSince: h.Now - TimeSpan.FromHours(1));
        var svc = h.Build();
        await svc.ForgetAsync(row.Id, actorUserId: null);

        var again = await svc.ForgetAsync(row.Id, actorUserId: null);

        again.Outcome.Should().Be(WorkloadForgetOutcome.NotFound);
        again.LeftBehind.Should().BeNull();
        h.Audit.Entries.Should().ContainSingle(e => e.Action == "workloads.forget");
    }

    [Fact]
    public async Task Forget_re_reads_a_row_a_teardown_pass_rewrote_and_drops_the_stored_row()
    {
        var h = new Harness();
        var row = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false,
            terminatingSince: h.Now - TimeSpan.FromHours(1));
        h.Repository.RaceWith(row.Id,
            () => h.TerminatingCopy(row, h.Now - TimeSpan.FromHours(1), "Teardown failed: retried (test)"));

        var result = await h.Build().ForgetAsync(row.Id, actorUserId: null);

        result.Outcome.Should().Be(WorkloadForgetOutcome.Forgotten);
        result.LeftBehind!.LastTeardownMessage.Should().Be("Teardown failed: retried (test)");
        (await h.Repository.GetByIdAsync(row.Id)).Should().BeNull();
        h.Repository.Reloaded.Should().Contain(row.Id);
    }

    [Fact]
    public async Task CompleteTeardown_revokes_the_workload_token()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        var svc = h.Build();

        await svc.CompleteTeardownAsync(instance);

        h.Tokens.RevokedInstances.Should().Contain(instance.Id, "uninstall teardown ⇒ revoke the workload token + close the session");
    }

    [Fact]
    public async Task CompleteTeardown_keeps_the_row_until_the_revoke_lands()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        h.Tokens.Throws = true;
        var svc = h.Build();

        var failed = await svc.CompleteTeardownAsync(instance);

        failed.Should().BeFalse();
        var held = (await h.Repository.GetByIdAsync(instance.Id))!;
        held.State.Should().Be(WorkloadInstanceState.Terminating);
        held.StateMessage.Should().Contain("credentials", "the operator sees why the removal does not finish")
            .And.Contain("credential plane unavailable (test)", "the revoke's own error is shown, as on the teardown path");

        h.Tokens.Throws = false;
        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeTrue();
        h.Tokens.RevokedInstances.Should().Contain(instance.Id);
        (await h.Repository.GetByIdAsync(instance.Id)).Should().BeNull();
    }

    [Fact]
    public async Task CompleteTeardown_revokes_even_when_the_provider_teardown_fails()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.RetainAll, withCriticalVolume: false);
        h.ThrowOnTeardown = true;
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeFalse();
        h.Tokens.RevokedInstances.Should().Contain(instance.Id);
        var stored = (await h.Repository.GetByIdAsync(instance.Id))!;
        stored.State.Should().Be(WorkloadInstanceState.Terminating);
        stored.StateMessage.Should().StartWith("Teardown failed:", "the operator sees why the removal does not finish");
    }

    [Fact]
    public async Task CompleteTeardown_touches_neither_the_runtime_nor_its_volumes_until_the_revoke_lands()
    {
        var h = new Harness();
        var instance = h.SeedDeployedTerminating(WorkloadRetention.PurgeAll, withCriticalVolume: true);
        h.Tokens.Throws = true;
        var svc = h.Build();

        var done = await svc.CompleteTeardownAsync(instance);

        done.Should().BeFalse();
        h.Provider.IsTracked(instance.Id).Should().BeTrue("a purge before the revoke lands would be repeated on the retry");
        h.Retained.PurgedForInstance.Should().BeEmpty();
    }

    [Fact]
    public async Task Uninstalling_a_frozen_instance_whose_revoke_fails_keeps_its_row_and_throws()
    {
        var h = new Harness();
        var pluginInstanceId = Guid.NewGuid();
        var instance = h.SeedDisownedDeployed(pluginInstanceId);
        h.Tokens.Throws = true;
        var svc = h.Build();

        await FluentActions.Awaiting(() => svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{instance.Id}*");

        (await h.Repository.GetByIdAsync(instance.Id)).Should().NotBeNull();
        h.Audit.Entries.Should().NotContain(e => e.Action == "workloads.uninstall.runtime-retained");

        h.Tokens.Throws = false;
        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        (await h.Repository.GetByIdAsync(instance.Id)).Should().BeNull();
        h.Tokens.RevokedInstances.Should().Contain(instance.Id);
    }

    [Fact]
    public async Task BeginUninstall_revokes_the_workload_token_immediately_even_when_the_provider_is_unreachable()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var instance = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Single();

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        h.Tokens.RevokedInstances.Should().Contain(instance.Id,
            "the token is revoked at BeginUninstall — not deferred to a teardown that a provider outage could block indefinitely");
    }

    [Fact]
    public async Task BeginUninstall_tolerates_a_token_service_fault_and_still_marks_the_row_Terminating()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        h.Tokens.Throws = true;

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId))
            .Should().OnlyContain(r => r.State == WorkloadInstanceState.Terminating,
                "a token-service fault is logged, never fatal to the uninstall — every row is still flipped Terminating");
    }


    [Fact]
    public async Task BeginUninstall_re_reads_a_row_a_health_write_raced_and_revokes_only_once_Terminating_is_saved()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var row = h.SeedDeployed(pluginInstanceId, WorkloadId);
        row.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, h.Now);
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload(WorkloadId)));
        h.Repository.RaceWith(row.Id, () => h.StoredCopy(row, WorkloadInstanceState.Healthy));
        var storedAtRevoke = new List<WorkloadInstanceState>();
        h.Tokens.OnRevoke = id => storedAtRevoke.Add(h.Repository.PersistedStates[id]);

        await h.Build().BeginUninstallAsync(pluginInstanceId, WorkloadRetention.PurgeAll);

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.State.Should().Be(WorkloadInstanceState.Terminating, "the lost write is re-applied on the stored row");
        stored.StateReason.Should().Be(WorkloadStateReason.Uninstalling);
        stored.Retention.Should().Be(WorkloadRetention.PurgeAll);
        h.Repository.PersistedStates[row.Id].Should().Be(WorkloadInstanceState.Terminating);
        storedAtRevoke.Should().Equal([WorkloadInstanceState.Terminating],
            "the credential dies only once the row that owns it is saved Terminating");
    }

    [Fact]
    public async Task BeginUninstall_leaves_the_credential_alone_when_the_Terminating_write_keeps_losing()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var row = h.SeedDeployed(pluginInstanceId, WorkloadId);
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload(WorkloadId)));
        h.Repository.RaceWith(row.Id, () => h.StoredCopy(row, WorkloadInstanceState.Healthy), writes: 10);

        await h.Build().BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        h.Repository.PersistedStates[row.Id].Should().Be(WorkloadInstanceState.Healthy);
        h.Tokens.RevokedInstances.Should().NotContain(row.Id,
            "a row that is still live must keep a working credential; the host retries the uninstall");
        h.Repository.Reloaded.Should().Equal([row.Id, row.Id, row.Id],
            "the last lost write is reloaded too, or its stale copy fails the next save in the unit of work");
    }

    [Fact]
    public async Task BeginUninstall_of_a_row_deleted_meanwhile_ends_without_revoking()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var row = h.SeedDeployed(pluginInstanceId, WorkloadId);
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload(WorkloadId)));
        h.Repository.RaceWith(row.Id, () => null);

        await h.Build().BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        (await h.Repository.GetByIdAsync(row.Id)).Should().BeNull();
        h.Tokens.RevokedInstances.Should().NotContain(row.Id, "whoever deleted the row owns its credential");
        h.Repository.Reloaded.Should().Equal([row.Id]);
    }

    [Fact]
    public async Task BeginUninstall_of_a_row_another_uninstall_already_flipped_saves_it_once_and_revokes()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var row = h.SeedDeployed(pluginInstanceId, WorkloadId);
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload(WorkloadId)));
        h.Repository.RaceWith(row.Id, () => h.StoredCopy(row, WorkloadInstanceState.Terminating));

        await h.Build().BeginUninstallAsync(pluginInstanceId, WorkloadRetention.PurgeAll);

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.State.Should().Be(WorkloadInstanceState.Terminating);
        stored.Retention.Should().Be(WorkloadRetention.PurgeAll);
        h.Repository.Updated.Should().Equal([row.Id], "only the re-read copy is saved");
        h.Tokens.RevokedInstances.Should().Equal([row.Id]);
    }

    [Fact]
    public async Task Uninstalling_a_frozen_row_a_health_write_raced_deletes_the_stored_row()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var row = h.SeedDisownedDeployed(pluginInstanceId);
        h.Repository.RaceWith(row.Id, () => h.StoredCopy(row, WorkloadInstanceState.ExternallyManaged));

        await h.Build().BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        (await h.Repository.GetByIdAsync(row.Id)).Should().BeNull("the delete is retried on the stored row");
        h.Tokens.RevokedInstances.Should().Contain(row.Id);
        h.Audit.Entries.Should().ContainSingle(e => e.Action == "workloads.uninstall.runtime-retained");
    }

    [Fact]
    public async Task Suspend_re_reads_a_row_a_health_write_raced_and_holds_it()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var row = h.SeedDeployed(pluginInstanceId, WorkloadId);
        h.Repository.RaceWith(row.Id, () => h.StoredCopy(row, WorkloadInstanceState.Degraded));

        await h.Build().SuspendAsync(pluginInstanceId);

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.State.Should().Be(WorkloadInstanceState.Suspended);
        stored.IsHeldByHost.Should().BeTrue();
        h.Repository.PersistedStates[row.Id].Should().Be(WorkloadInstanceState.Suspended);
        h.Provider.IsSuspended(row.Id).Should().BeTrue();
    }

    [Fact]
    public async Task Resume_re_reads_a_row_a_concurrent_write_raced_and_releases_it()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var row = h.SeedDeployed(pluginInstanceId, WorkloadId);
        row.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, null, h.Now);
        h.Repository.RaceWith(row.Id, () =>
        {
            var copy = h.StoredCopy(row, WorkloadInstanceState.Suspended);
            copy.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, "Re-asserted.", h.Now);
            return copy;
        });

        await h.Build().ResumeAsync(pluginInstanceId);

        var stored = await h.Repository.GetByIdAsync(row.Id);
        stored!.State.Should().Be(WorkloadInstanceState.Deploying);
        h.Repository.PersistedStates[row.Id].Should().Be(WorkloadInstanceState.Deploying);
    }

    [Fact]
    public async Task BeginUninstall_continues_past_a_single_instance_failure_and_terminates_the_rest()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js"), Workload("zwave-ui")));
        var svc = h.Build();
        await h.MaterializeAsync(svc, pluginInstanceId);
        var rows = await h.Repository.ListByPluginInstanceAsync(pluginInstanceId);
        h.Repository.ThrowOnUpdateFor = rows.OrderBy(r => r.WorkloadId, StringComparer.Ordinal).First().Id;

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.RetainAll);

        var after = await h.Repository.ListByPluginInstanceAsync(pluginInstanceId);
        after.Should().Contain(r => r.State == WorkloadInstanceState.Terminating,
            "a mid-loop failure must not strand the remaining rows un-Terminating (they would become permanent ghosts)");
    }


    private static DeploymentManifest DeploymentWith(params WorkloadManifest[] workloads)
        => new() { SchemaVersion = 1, Workloads = workloads };

    private static WorkloadManifest Workload(string id, IReadOnlyList<WorkloadParameter>? parameters = null) => new()
    {
        Id = id,
        DisplayName = id,
        Image = new WorkloadImage { Ref = "registry.example/" + id, Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
        Parameters = parameters ?? Array.Empty<WorkloadParameter>(),
    };

    private static WorkloadManifest WorkloadWithDeviceSlot(string id, string parameter, WorkloadDeviceGroup group)
    {
        var manifest = Workload(id, parameters: new[]
        {
            new WorkloadParameter { Name = parameter, Type = WorkloadParameterType.DevicePath },
        });
        return manifest with
        {
            Privileges = new WorkloadPrivileges
            {
                Devices = new[] { new WorkloadDevice { Parameter = parameter, Group = group } },
            },
        };
    }

    private static WorkloadParameter RequiredParam(string name) => new()
    {
        Name = name,
        Type = WorkloadParameterType.DevicePath,
        Required = true,
    };

    private static WorkloadManifest WorkloadWithCriticalVolume(string id) => new()
    {
        Id = id,
        DisplayName = id,
        Image = new WorkloadImage { Ref = "registry.example/" + id, Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
        Volumes = new[]
        {
            new WorkloadVolume
            {
                Id = "state",
                MountPath = "/data",
                Size = "1Gi",
                Criticality = VolumeCriticality.Critical,
                Description = "Z-Wave network + device bonds",
            },
        },
    };

    private static WorkloadManifest WorkloadWithTwoVolumes(string id) => new()
    {
        Id = id,
        DisplayName = id,
        Image = new WorkloadImage { Ref = "registry.example/" + id, Digest = "sha256:" + new string('a', 64) },
        Kind = WorkloadKind.SingleNode,
        Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
        Volumes = new[]
        {
            new WorkloadVolume { Id = "state", MountPath = "/data", Size = "1Gi", Criticality = VolumeCriticality.Critical },
            new WorkloadVolume { Id = "logs", MountPath = "/logs", Size = "512Mi", Criticality = VolumeCriticality.Cache },
        },
    };

    [Fact]
    public async Task Adopting_a_module_with_no_row_records_it()
    {
        var harness = new Harness();

        var outcome = await harness.Build().AdoptGroupAsync(" zigbee2mqtt ", "repo-1", "1.2.0");

        outcome.Should().Be(WorkloadGroupAdoptOutcome.Created);
        var row = harness.Modules.Rows.Should().ContainSingle().Subject;
        row.GroupId.Should().Be("zigbee2mqtt");
        row.SourceKey.Should().Be("repo-1");
        row.InstalledVersion.Should().Be("1.2.0");
        row.CreatedAtUtc.Should().Be(harness.Now);
    }

    [Fact]
    public async Task Adopting_a_module_without_a_version_records_none()
    {
        var harness = new Harness();

        (await harness.Build().AdoptGroupAsync("zigbee2mqtt", "repo-1", installedVersion: null))
            .Should().Be(WorkloadGroupAdoptOutcome.Created);

        harness.Modules.Rows.Should().ContainSingle().Which.InstalledVersion.Should().BeNull();
    }

    [Theory]
    [InlineData("zigbee2mqtt", "repo-1", "1.2.0")]
    [InlineData("ZIGBEE2MQTT", " repo-1 ", " 1.2.0 ")]
    public async Task Adopting_again_with_the_same_values_writes_nothing(string groupId, string sourceKey, string version)
    {
        var harness = new Harness();
        harness.Modules.Seed("zigbee2mqtt", "repo-1", "1.2.0");

        (await harness.Build().AdoptGroupAsync(groupId, sourceKey, version))
            .Should().Be(WorkloadGroupAdoptOutcome.Unchanged, "every boot adopts every installed module again");

        harness.Modules.Saves.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1.3.0")]
    [InlineData(null)]
    public async Task Adopting_another_version_is_refused_and_writes_nothing(string? version)
    {
        var harness = new Harness();
        var row = harness.Modules.Seed("zigbee2mqtt", "repo-1", "1.2.0");

        (await harness.Build().AdoptGroupAsync("zigbee2mqtt", "repo-1", version))
            .Should().Be(WorkloadGroupAdoptOutcome.VersionMismatch,
                "moving a version is an update, and an adopt that moved it would skip the update's gates");

        row.InstalledVersion.Should().Be("1.2.0");
        harness.Modules.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Adopting_a_version_over_a_row_that_records_none_is_refused()
    {
        var harness = new Harness();
        harness.Modules.Seed("zigbee2mqtt", "repo-1", installedVersion: null);

        (await harness.Build().AdoptGroupAsync("zigbee2mqtt", "repo-1", "1.2.0"))
            .Should().Be(WorkloadGroupAdoptOutcome.VersionMismatch);
    }

    [Fact]
    public async Task Adopting_from_another_source_is_refused_and_writes_nothing()
    {
        var harness = new Harness();
        var row = harness.Modules.Seed("zigbee2mqtt", "repo-1", "1.2.0");

        (await harness.Build().AdoptGroupAsync("zigbee2mqtt", "repo-2", "1.2.0"))
            .Should().Be(WorkloadGroupAdoptOutcome.SourceMismatch);

        row.SourceKey.Should().Be("repo-1");
        harness.Modules.Saves.Should().BeEmpty();
    }

    [Fact]
    public async Task Adopting_after_another_replica_won_the_insert_compares_with_its_row()
    {
        var harness = new Harness();
        harness.Modules.LoseNextInsertTo = WorkloadGroup.Record("zigbee2mqtt", "repo-1", "1.2.0", harness.Now);

        (await harness.Build().AdoptGroupAsync("zigbee2mqtt", "repo-1", "1.2.0"))
            .Should().Be(WorkloadGroupAdoptOutcome.Unchanged,
                "every replica adopts at boot, and losing the insert to the same values is not a refusal");
    }

    [Theory]
    [InlineData(null, "repo-1")]
    [InlineData(" ", "repo-1")]
    [InlineData("zigbee2mqtt", null)]
    [InlineData("zigbee2mqtt", " ")]
    public async Task Adopting_needs_a_module_and_a_source(string? groupId, string? sourceKey)
    {
        var act = () => new Harness().Build().AdoptGroupAsync(groupId!, sourceKey!, "1.2.0");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static WorkloadAttachmentPin IotPin => new()
    {
        Mode = WorkloadNetworkMode.OperatorNetwork,
        AttachmentNames = ["iot", "cam"],
    };

    private static WorkloadInstance PinnableInstance(Harness harness)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, "gateway", harness.Now.AddDays(-1));
        harness.Repository.Add(instance);
        return instance;
    }

    [Fact]
    public async Task Pinning_an_attachment_saves_it_and_nudges_the_reconciler()
    {
        var harness = new Harness();
        var instance = PinnableInstance(harness);

        (await harness.Build().PinAttachmentAsync(instance.Id, IotPin)).Should().BeTrue();

        instance.ReadAttachmentPin().Value!.AttachmentNames.Should().Equal("iot", "cam");
        instance.UpdatedAtUtc.Should().Be(harness.Now);
        harness.Repository.Updated.Should().Equal(instance.Id);
        harness.Bus.Published.Should().ContainSingle().Which.Payload.Should().BeOfType<WorkloadReconcileRequestMessage>()
            .Which.InstanceId.Should().Be(instance.Id, "a pin can release a parked apply, so the reconciler looks again now");
        harness.Bus.Published.Single().Channel.Should().Be(WorkloadClusterChannels.ReconcileRequest);
    }

    [Fact]
    public async Task Pinning_null_clears_the_pin_and_saves()
    {
        var harness = new Harness();
        var instance = PinnableInstance(harness);
        instance.PinAttachment(IotPin, harness.Now.AddDays(-1));

        (await harness.Build().PinAttachmentAsync(instance.Id, pin: null)).Should().BeTrue();

        instance.AttachmentPinDocument.Should().BeNull();
        harness.Repository.Updated.Should().Equal(instance.Id);
        harness.Bus.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task Pinning_an_unknown_instance_answers_false_and_touches_nothing()
    {
        var harness = new Harness();

        (await harness.Build().PinAttachmentAsync(Guid.NewGuid(), IotPin)).Should().BeFalse();

        harness.Repository.Updated.Should().BeEmpty();
        harness.Bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_pin_is_refused_before_anything_is_saved()
    {
        var harness = new Harness();
        var instance = PinnableInstance(harness);

        var act = () => harness.Build().PinAttachmentAsync(instance.Id, IotPin with { AttachmentNames = [" "] });

        await act.Should().ThrowAsync<ArgumentException>();
        instance.AttachmentPinDocument.Should().BeNull();
        harness.Repository.Updated.Should().BeEmpty();
        harness.Bus.Published.Should().BeEmpty();
    }


    [Fact]
    public async Task Materialize_records_the_module_row_and_new_rows_wait_for_their_configuration()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));

        var result = await h.MaterializeAsync(h.Build(), pluginInstanceId);

        result.Should().Be(WorkloadMaterializeResult.Materialized);
        var module = h.Modules.Rows.Should().ContainSingle().Subject;
        (module.GroupId, module.SourceKey, module.InstalledVersion).Should().Be((GroupId, FakeRequirements.SourceKey, "1.0.0"));
        var row = (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Should().ContainSingle().Subject;
        row.State.Should().Be(WorkloadInstanceState.PendingConfiguration, "nobody's consent is awaited any more");
        row.StateReason.Should().Be(WorkloadStateReason.ConfigRequired);
        row.AttachmentPinDocument.Should().BeNull("a workload without a pin is not checked");
    }

    [Fact]
    public async Task Materialize_reads_the_requirements_of_exactly_what_is_installed()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var values = new Dictionary<string, string?> { ["serialPort"] = "/dev/ttyUSB0" };

        await h.Build().MaterializeAsync(h.Install(pluginInstanceId) with { GroupId = "  ZWave ", Parameters = values });

        var query = h.Requirements.Queries.Should().ContainSingle().Subject;
        query.GroupId.Should().Be("ZWave", "the id the row records, trimmed");
        query.SourceKey.Should().Be(FakeRequirements.SourceKey);
        query.Version.Should().Be("1.0.0");
        query.Parameters.Should().BeSameAs(values);
    }

    [Fact]
    public async Task A_bundled_install_carries_no_version_to_the_requirements_or_the_row()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));

        var result = await h.Build().MaterializeAsync(h.Install(pluginInstanceId) with { Version = null });

        result.Outcome.Should().Be(WorkloadMaterializeOutcome.Materialized);
        h.Requirements.Queries.Should().ContainSingle().Which.Version.Should().BeNull();
        h.Modules.Rows.Should().ContainSingle().Which.InstalledVersion.Should().BeNull();
    }

    [Theory]
    [InlineData("0.9.0", FakeRequirements.SourceKey, WorkloadMaterializeOutcome.VersionMismatch)]
    [InlineData(null, FakeRequirements.SourceKey, WorkloadMaterializeOutcome.VersionMismatch)]
    [InlineData("1.0.0", "elsewhere", WorkloadMaterializeOutcome.SourceMismatch)]
    public async Task Materialize_refuses_a_module_recorded_otherwise_before_reading_anything(
        string? recordedVersion, string recordedSource, WorkloadMaterializeOutcome expected)
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        h.Modules.Seed(GroupId, recordedSource, recordedVersion);
        await SeedWorkloadOfAnotherInstanceAsync(h, GroupId);

        var result = await h.MaterializeAsync(h.Build(), pluginInstanceId);

        result.Outcome.Should().Be(expected, "moving a module's version or source under a live row is an update");
        result.Message.Should().Contain(GroupId);
        h.Requirements.Queries.Should().BeEmpty();
        AssertNothingWritten(h, pluginInstanceId);
    }

    [Fact]
    public async Task Materialize_refuses_a_move_while_a_row_of_the_module_is_still_tearing_down()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var row = h.Modules.Seed(GroupId, FakeRequirements.SourceKey, "0.9.0");
        var leaving = await SeedWorkloadOfAnotherInstanceAsync(h, GroupId.ToUpperInvariant());
        leaving.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, h.Now);

        var result = await h.MaterializeAsync(h.Build(), pluginInstanceId);

        result.Outcome.Should().Be(WorkloadMaterializeOutcome.VersionMismatch,
            "a teardown still reads the module at its recorded version");
        row.InstalledVersion.Should().Be("0.9.0");
        AssertNothingWritten(h, pluginInstanceId);
    }

    [Theory]
    [InlineData("0.9.0", FakeRequirements.SourceKey)]
    [InlineData(null, FakeRequirements.SourceKey)]
    [InlineData("1.0.0", "elsewhere")]
    [InlineData("0.9.0", "elsewhere")]
    public async Task Materialize_reinstalls_a_module_no_workload_row_is_left_of(string? recordedVersion, string recordedSource)
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var row = h.Modules.Seed(GroupId, recordedSource, recordedVersion);
        await SeedWorkloadOfAnotherInstanceAsync(h, "another.module");

        var result = await h.MaterializeAsync(h.Build(), pluginInstanceId);

        result.Outcome.Should().Be(WorkloadMaterializeOutcome.Materialized,
            "the last instance was deleted at the old version; nothing runs there for an update to move");
        h.Modules.Rows.Should().ContainSingle().Which.Should().BeSameAs(row);
        row.InstalledVersion.Should().Be("1.0.0");
        row.SourceKey.Should().Be(FakeRequirements.SourceKey);
        row.UpdatedAtUtc.Should().Be(h.Now);
        h.Modules.Saves.Should().ContainSingle().Which.Should().BeSameAs(row);
        (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_reinstall_refused_after_the_module_check_leaves_the_row_where_it_was()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var row = h.Modules.Seed(GroupId, FakeRequirements.SourceKey, "0.9.0");
        var svc = h.Build();
        var judged = h.Install(pluginInstanceId);
        h.Requirements.Fingerprint = "fp-after-the-manifest-moved";

        var result = await svc.MaterializeAsync(judged);

        result.Outcome.Should().Be(WorkloadMaterializeOutcome.PreconditionFailed);
        row.InstalledVersion.Should().Be("0.9.0");
        AssertNothingWritten(h, pluginInstanceId);
    }

    private static async Task<WorkloadInstance> SeedWorkloadOfAnotherInstanceAsync(Harness h, string groupId)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), groupId, "zwave-js", h.Now);
        await h.Repository.AddAsync(instance);
        return instance;
    }

    [Fact]
    public async Task Materialize_refuses_requirements_that_moved_since_the_host_judged_them()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var svc = h.Build();
        var judged = h.Install(pluginInstanceId);
        h.Requirements.Fingerprint = "fp-after-the-manifest-moved";

        var result = await svc.MaterializeAsync(judged);

        result.Outcome.Should().Be(WorkloadMaterializeOutcome.PreconditionFailed);
        result.Message.Should().Contain("changed since they were judged");
        AssertNothingWritten(h, pluginInstanceId);
    }

    [Fact]
    public async Task Materialize_refuses_values_that_cannot_be_resolved_with_the_reason()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        h.Requirements.Status = WorkloadRequirementsStatus.Invalid;

        var result = await h.MaterializeAsync(h.Build(), pluginInstanceId);

        result.Outcome.Should().Be(WorkloadMaterializeOutcome.PreconditionFailed);
        result.Message.Should().Be("the values cannot be resolved (test)");
        AssertNothingWritten(h, pluginInstanceId);
    }

    [Fact]
    public async Task Materialize_refuses_a_pin_for_a_workload_the_module_does_not_declare()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        var pins = new Dictionary<string, WorkloadAttachmentPin>(StringComparer.Ordinal) { ["gateway"] = IotPin };

        var result = await h.Build().MaterializeAsync(h.Install(pluginInstanceId) with { AttachmentPins = pins });

        result.Outcome.Should().Be(WorkloadMaterializeOutcome.NotDeclared);
        result.Message.Should().Contain("'gateway'");
        AssertNothingWritten(h, pluginInstanceId);
    }

    [Fact]
    public async Task Materialize_records_each_pin_on_its_workload_and_a_later_install_replaces_it()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js"), Workload("zwave-ui")));
        var svc = h.Build();
        var pins = new Dictionary<string, WorkloadAttachmentPin>(StringComparer.Ordinal) { ["zwave-js"] = IotPin };

        await svc.MaterializeAsync(h.Install(pluginInstanceId) with { AttachmentPins = pins });

        var rows = await h.Repository.ListByPluginInstanceAsync(pluginInstanceId);
        rows.Single(r => r.WorkloadId == "zwave-js").ReadAttachmentPin().Value!.AttachmentNames.Should().Equal("iot", "cam");
        rows.Single(r => r.WorkloadId == "zwave-ui").AttachmentPinDocument.Should().BeNull();

        var moved = new Dictionary<string, WorkloadAttachmentPin>(StringComparer.Ordinal)
        {
            ["zwave-js"] = IotPin with { AttachmentNames = ["iot-moved", "cam"] },
        };
        await svc.MaterializeAsync(h.Install(pluginInstanceId) with { AttachmentPins = moved });

        (await h.Repository.GetByWorkloadAsync(pluginInstanceId, "zwave-js"))!
            .ReadAttachmentPin().Value!.AttachmentNames.Should().Equal(["iot-moved", "cam"], "re-materializing records the pin judged this time");
        (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_lost_module_insert_to_another_version_refuses_and_creates_no_rows()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        h.Modules.LoseNextInsertTo = WorkloadGroup.Record(GroupId, FakeRequirements.SourceKey, "2.0.0", h.Now);

        var result = await h.MaterializeAsync(h.Build(), pluginInstanceId);

        result.Outcome.Should().Be(WorkloadMaterializeOutcome.VersionMismatch, "another replica installed another version first");
        (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Should().BeEmpty();
        h.Bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lost_module_insert_to_the_same_install_goes_on()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        h.Modules.LoseNextInsertTo = WorkloadGroup.Record(GroupId, FakeRequirements.SourceKey, "1.0.0", h.Now);

        var result = await h.MaterializeAsync(h.Build(), pluginInstanceId);

        result.Outcome.Should().Be(WorkloadMaterializeOutcome.Materialized);
        (await h.Repository.ListByPluginInstanceAsync(pluginInstanceId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Materialize_of_a_module_already_recorded_at_the_same_install_adds_no_second_row()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        h.Context.Seed(pluginInstanceId, GroupId, DeploymentWith(Workload("zwave-js")));
        h.Modules.Seed(GroupId, FakeRequirements.SourceKey, "1.0.0");

        var result = await h.MaterializeAsync(h.Build(), pluginInstanceId);

        result.Outcome.Should().Be(WorkloadMaterializeOutcome.Materialized);
        h.Modules.Rows.Should().ContainSingle();
        h.Modules.Saves.Should().BeEmpty("a recorded row is not inserted again");
    }

    [Fact]
    public async Task Materialize_without_a_fingerprint_or_an_instance_id_is_a_programming_error()
    {
        var h = new Harness();
        var svc = h.Build();
        var install = h.Install(Guid.NewGuid());

        await FluentActions.Awaiting(() => svc.MaterializeAsync(install with { RequirementsFingerprint = " " }))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => svc.MaterializeAsync(install with { GroupInstanceId = Guid.Empty }))
            .Should().ThrowAsync<ArgumentException>();
        h.Requirements.Queries.Should().BeEmpty();
    }

    private static void AssertNothingWritten(Harness h, Guid pluginInstanceId)
    {
        h.Repository.ListByPluginInstanceAsync(pluginInstanceId).Result.Should().BeEmpty("a refusal writes no workload row");
        h.Modules.Saves.Should().BeEmpty("a refusal writes no module row");
        h.Bus.Published.Should().BeEmpty("a refusal nudges nothing");
    }

    [Fact]
    public async Task Suspend_holds_every_row_below_teardown_revokes_its_credential_and_scales_it_down()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var running = h.SeedDeployed(pluginInstanceId, "running");
        var configuring = h.SeedRow(pluginInstanceId, "configuring");
        var paused = h.SeedDeployed(pluginInstanceId, "paused");
        paused.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, null, h.Now);
        var tearingDown = h.SeedRow(pluginInstanceId, "tearing-down");
        tearingDown.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null, h.Now);
        var disowned = h.SeedDisownedDeployed(pluginInstanceId);
        var other = h.SeedDeployed(Guid.NewGuid(), "another-instance");

        await h.Build().SuspendAsync(pluginInstanceId);

        foreach (var held in new[] { running, configuring, paused })
        {
            held.State.Should().Be(WorkloadInstanceState.Suspended);
            held.StateReason.Should().Be(WorkloadStateReason.SuspendedByHost, "the host's hold outranks an operator pause");
            h.Tokens.RevokedInstances.Should().Contain(held.Id, "a withdrawn workload keeps no live credential");
        }

        h.Provider.IsSuspended(running.Id).Should().BeTrue("the runtime stops now, not on the next pass");
        tearingDown.State.Should().Be(WorkloadInstanceState.Terminating, "a teardown already stops it");
        disowned.State.Should().Be(WorkloadInstanceState.Suspended);
        disowned.StateReason.Should().Be(WorkloadStateReason.SuspendedByHostExternallyManaged);
        disowned.IsHeldByHost.Should().BeTrue("an externally managed workload is withdrawn too");
        h.Tokens.RevokedInstances.Should().Contain(disowned.Id, "a withdrawn workload keeps no live credential");
        h.Provider.IsSuspended(disowned.Id).Should().BeFalse("its runtime is the operator's, not the library's to scale down");
        h.Tokens.RevokedInstances.Should().NotContain([tearingDown.Id, other.Id]);
        other.State.Should().Be(WorkloadInstanceState.Healthy);
        h.Repository.Updated.Should().BeEquivalentTo([running.Id, configuring.Id, paused.Id, disowned.Id]);
        h.Bus.Published.Where(p => p.Channel == WorkloadClusterChannels.ReconcileRequest).Should().HaveCount(4);
    }

    [Fact]
    public async Task A_mid_switch_row_is_revoked_at_once_and_marked_for_the_hold()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var switching = h.SeedSwitchingDeployed(pluginInstanceId);

        await h.Build().SuspendAsync(pluginInstanceId);

        h.Tokens.RevokedInstances.Should().Contain(switching.Id);
        switching.State.Should().Be(WorkloadInstanceState.Switching, "the switch owns the row until it completes");
        switching.IsHostHoldPending.Should().BeTrue("the switch holds the row when it completes");
        h.Provider.IsSuspended(switching.Id).Should().BeFalse();
        h.Repository.Updated.Should().Contain(switching.Id);
    }

    [Fact]
    public async Task Resume_lifts_the_hold_mark_a_suspend_left_mid_switch()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var switching = h.SeedSwitchingDeployed(pluginInstanceId);
        var svc = h.Build();
        await svc.SuspendAsync(pluginInstanceId);

        await svc.ResumeAsync(pluginInstanceId);

        switching.State.Should().Be(WorkloadInstanceState.Switching);
        switching.IsHostHoldPending.Should().BeFalse("the switch must not hold a row the host gave back");
        switching.AppliedSpecHash.Should().BeNull("its credential stays revoked, so an unwind back to managed re-applies");
    }

    [Fact]
    public async Task Suspending_a_held_externally_managed_row_again_keeps_its_ownership_and_revokes_again()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var disowned = h.SeedDisownedDeployed(pluginInstanceId);
        var svc = h.Build();
        await svc.SuspendAsync(pluginInstanceId);
        h.Repository.Updated.Clear();

        await svc.SuspendAsync(pluginInstanceId);

        disowned.StateReason.Should().Be(WorkloadStateReason.SuspendedByHostExternallyManaged);
        h.Repository.Updated.Should().BeEmpty();
        h.Provider.IsSuspended(disowned.Id).Should().BeFalse();
        h.Tokens.RevokedInstances.Should().HaveCount(2);
    }

    [Fact]
    public async Task Resume_returns_a_held_externally_managed_row_to_the_operator_without_re_applying_it()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var disowned = h.SeedDisownedDeployed(pluginInstanceId);
        var svc = h.Build();
        await svc.SuspendAsync(pluginInstanceId);

        await svc.ResumeAsync(pluginInstanceId);

        disowned.State.Should().Be(WorkloadInstanceState.ExternallyManaged, "a resume must not re-adopt the operator's runtime");
        disowned.StateReason.Should().Be(WorkloadStateReason.ResumedByHost);
        disowned.IsReconcileFrozen.Should().BeTrue();
        disowned.AppliedSpecHash.Should().Be("hash", "nothing is re-applied to a runtime Reeve does not own");
    }

    [Fact]
    public async Task Uninstalling_a_held_externally_managed_row_retains_the_operators_runtime()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var disowned = h.SeedDisownedDeployed(pluginInstanceId);
        var svc = h.Build();
        await svc.SuspendAsync(pluginInstanceId);

        await svc.BeginUninstallAsync(pluginInstanceId, WorkloadRetention.PurgeAll);

        (await h.Repository.GetByIdAsync(disowned.Id)).Should().BeNull("only Reeve's record is dropped");
        h.Provider.IsTracked(disowned.Id).Should().BeTrue("the hold does not hand the operator's runtime back to the teardown");
    }

    [Fact]
    public async Task Suspending_a_held_row_again_writes_nothing_but_still_stops_it()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var running = h.SeedDeployed(pluginInstanceId, "running");
        var svc = h.Build();
        await svc.SuspendAsync(pluginInstanceId);
        await h.Provider.ResumeAsync(WorkloadRef.FromInstance(running));
        h.Repository.Updated.Clear();

        await svc.SuspendAsync(pluginInstanceId);

        h.Repository.Updated.Should().BeEmpty("the row already records the host's hold");
        h.Provider.IsSuspended(running.Id).Should().BeTrue("a suspend that did not land is issued again");
        h.Tokens.RevokedInstances.Should().HaveCount(2);
    }

    [Fact]
    public async Task Resume_lifts_only_the_hosts_hold_and_re_applies_it()
    {
        var pluginInstanceId = Guid.NewGuid();
        var h = new Harness();
        var hostHeld = h.SeedDeployed(pluginInstanceId, "host-held");
        hostHeld.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, null, h.Now);
        var paused = h.SeedDeployed(pluginInstanceId, "paused");
        paused.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.OperatorPaused, null, h.Now);
        var running = h.SeedDeployed(pluginInstanceId, "running");
        var alsoHeld = h.SeedDeployed(pluginInstanceId, "also-held");
        alsoHeld.TransitionTo(WorkloadInstanceState.Suspended, WorkloadStateReason.SuspendedByHost, null, h.Now);

        await h.Build().ResumeAsync(pluginInstanceId);

        foreach (var resumed in new[] { hostHeld, alsoHeld })
        {
            resumed.State.Should().Be(WorkloadInstanceState.Deploying);
            resumed.StateReason.Should().Be(WorkloadStateReason.ResumedByHost);
            resumed.AppliedSpecHash.Should().BeNull("the suspend revoked the credential, so the next pass must apply again");
        }

        paused.StateReason.Should().Be(WorkloadStateReason.OperatorPaused, "an operator pause is the operator's to lift");
        paused.AppliedSpecHash.Should().Be("hash");
        running.State.Should().Be(WorkloadInstanceState.Healthy);
        running.AppliedSpecHash.Should().Be("hash");
        h.Repository.Updated.Should().BeEquivalentTo([hostHeld.Id, alsoHeld.Id]);
        h.Bus.Published.Where(p => p.Channel == WorkloadClusterChannels.ReconcileRequest).Should().HaveCount(2);
    }

    private sealed class Harness
    {
        public FakeDeploymentContextSource Context { get; } = new();
        public InMemoryRepository Repository { get; } = new();
        public InMemoryWorkloadGroupRepository Modules { get; } = new();
        public FakeWorkloadClusterBus Bus { get; } = new();
        public InMemoryWorkloadProvider Provider { get; } = new(KubernetesProvider.Descriptor);
        public WorkloadProviderId? RegisteredProvider { get; init; } = KubernetesProvider.Id;

        public WorkloadProviderId BoundProvider { get; init; } = KubernetesProvider.Id;
        public bool ThrowOnTeardown { get; set; }
        public Exception? TeardownFault { get; init; }
        public bool HangOnTeardownPastTheDeadline { get; init; }
        public DateTimeOffset Now { get; } = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

        public FakeRetainedVolumeService Retained { get; } = new();
        public FakeWorkloadCredentialPort Tokens { get; } = new();
        public FakeSettingsService Settings { get; } = new();
        public FakeAuditWriter Audit { get; } = new();
        public FakeActiveIntentProvider IntentProvider { get; } = new();
        public WorkloadParameterSecretCodec ParameterSecrets { get; } = new(new EphemeralDataProtectionProvider());

        public FakeDeviceGroupSource Inventory { get; } = new();

        public C1Nf3RetainedVolumeRepository? RealRetainedRepo { get; init; }

        public FakeRequirements Requirements { get; }

        public Harness() => Requirements = new FakeRequirements(Context);

        public Task<WorkloadMaterializeResult> MaterializeAsync(
            WorkloadLifecycleService svc,
            Guid groupInstanceId,
            bool suppressReconcileNudge = false,
            IReadOnlyDictionary<string, string?>? installDeploymentParameters = null)
            => svc.MaterializeAsync(Install(groupInstanceId) with
            {
                Parameters = installDeploymentParameters ?? new Dictionary<string, string?>(),
                SuppressReconcileNudge = suppressReconcileNudge,
            });

        public WorkloadGroupInstall Install(Guid groupInstanceId) => new()
        {
            GroupInstanceId = groupInstanceId,
            GroupId = Context.GroupOf(groupInstanceId),
            SourceKey = FakeRequirements.SourceKey,
            Version = "1.0.0",
            RequirementsFingerprint = Requirements.Fingerprint,
        };

        public Update.SpyIssueRegistry Issues { get; } = new();
        public Update.SpyRepairsFactory Sinks => sinks ??= new Update.SpyRepairsFactory(Issues);
        private Update.SpyRepairsFactory? sinks;

        public WorkloadLifecycleService Build()
        {
            IWorkloadProvider provider = new ThrowingProvider(Provider)
            {
                ThrowOnTeardown = ThrowOnTeardown,
                TeardownFault = TeardownFault,
                HangOnTeardown = HangOnTeardownPastTheDeadline,
            };
            if (HangOnTeardownPastTheDeadline)
            {
                provider = new BoundedWorkloadProvider(
                    provider,
                    new WorkloadProviderTimeoutOptions { CallTimeoutSeconds = 0, MutationTimeoutSeconds = 0 },
                    NullLogger.Instance);
            }

            var registry = new StubRegistry(RegisteredProvider, provider);
            IRetainedVolumeService retained = RealRetainedRepo is null
                ? Retained
                : new RetainedVolumeService(RealRetainedRepo, new NullRetainedVolumeDeleter());
            return new WorkloadLifecycleService(
                Context,
                Repository,
                IntentProvider,
                Modules,
                registry,
                new WorkloadResourceNamerRegistry(
                    [DockerResourceNamer.Instance, new Reeve.Workloads.Providers.Manual.ManualResourceNamer()]),
                new CompiledProviderCatalog(ProviderRegistryFakes.FirstPartyRegistry()),
                retained,
                Bus.Nudge,
                new FixedClock(Now),
                NullLogger<WorkloadLifecycleService>.Instance,
                new WorkloadDeviceGroupIdWriter(Inventory),
                Audit,
                Tokens,
                Requirements,
                Settings,
                ParameterSecrets,
                Sinks,
                Microsoft.Extensions.Options.Options.Create(new WorkloadRepairsOptions()));
        }

        public WorkloadInstance SeedDisownedDeployed(Guid pluginInstanceId)
        {
            var instance = WorkloadInstance.Create(Guid.NewGuid(), pluginInstanceId, GroupId, WorkloadId, Now);
            var spec = SpecFor(instance.Id, withCriticalVolume: false);
            Provider.ApplyAsync(spec, progress: null).GetAwaiter().GetResult();
            instance.SetProvider(BoundProvider, Now);
            instance.SetAppliedSpecHash("hash", Now);
            instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);
            instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
            instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
            instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, Now);
            instance.TransitionTo(WorkloadInstanceState.ExternallyManaged, WorkloadStateReason.ExternalHealthObserved, null, Now);
            Repository.Add(instance);
            return instance;
        }

        public WorkloadInstance SeedSwitchingDeployed(Guid pluginInstanceId)
        {
            var instance = WorkloadInstance.Create(Guid.NewGuid(), pluginInstanceId, GroupId, WorkloadId, Now);
            var spec = SpecFor(instance.Id, withCriticalVolume: false);
            Provider.ApplyAsync(spec, progress: null).GetAwaiter().GetResult();
            instance.SetProvider(BoundProvider, Now);
            instance.SetAppliedSpecHash("hash", Now);
            instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);
            instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
            instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
            instance.TransitionTo(WorkloadInstanceState.Switching, WorkloadStateReason.HealthObserved, null, Now);
            Repository.Add(instance);
            return instance;
        }

        public WorkloadInstance StoredCopy(WorkloadInstance row, WorkloadInstanceState state)
        {
            var copy = WorkloadInstance.Create(row.Id, row.GroupInstanceId, row.GroupId, row.WorkloadId, Now);
            if (row.Provider is { } provider)
            {
                copy.SetProvider(provider, Now);
            }

            copy.SetAppliedSpecHash(row.AppliedSpecHash, Now);
            copy.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
            copy.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
            if (state != WorkloadInstanceState.Healthy)
            {
                var reason = state == WorkloadInstanceState.ExternallyManaged
                    ? WorkloadStateReason.ExternalHealthObserved
                    : WorkloadStateReason.HealthObserved;
                copy.TransitionTo(state, reason, null, Now);
            }

            return copy;
        }

        public WorkloadInstance TerminatingCopy(WorkloadInstance row, DateTimeOffset since, string message)
        {
            var copy = WorkloadInstance.Create(row.Id, row.GroupInstanceId, row.GroupId, row.WorkloadId, Now);
            copy.SetProvider(row.Provider!.Value, Now);
            copy.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, since);
            copy.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, message, since);
            return copy;
        }

        public WorkloadInstance SeedRow(Guid pluginInstanceId, string workloadId)
        {
            var instance = WorkloadInstance.Create(Guid.NewGuid(), pluginInstanceId, GroupId, workloadId, Now);
            Repository.Add(instance);
            return instance;
        }

        public WorkloadInstance SeedDeployed(Guid pluginInstanceId, string workloadId)
        {
            var instance = SeedRow(pluginInstanceId, workloadId);
            Provider.ApplyAsync(SpecFor(instance.Id, withCriticalVolume: false), progress: null).GetAwaiter().GetResult();
            instance.SetProvider(BoundProvider, Now);
            instance.SetAppliedSpecHash("hash", Now);
            instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
            instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
            return instance;
        }

        public WorkloadInstance SeedDeployedTerminating(
            WorkloadRetention retention, bool withCriticalVolume, DateTimeOffset? terminatingSince = null)
        {
            var pluginInstanceId = Guid.NewGuid();
            var instance = WorkloadInstance.Create(Guid.NewGuid(), pluginInstanceId, GroupId, WorkloadId, Now);
            var spec = SpecFor(instance.Id, withCriticalVolume);
            Provider.ApplyAsync(spec, progress: null).GetAwaiter().GetResult();
            instance.SetProvider(BoundProvider, Now);
            instance.SetAppliedSpecHash("hash", Now);
            instance.TransitionTo(WorkloadInstanceState.PendingConfiguration, WorkloadStateReason.ConfigRequired, null, Now);
            instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
            instance.TransitionTo(WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling, null,
                terminatingSince ?? Now);
            instance.SetRetention(retention, Now);

            if (withCriticalVolume && retention == WorkloadRetention.RetainAll)
            {
                var resourceName = BoundProvider == DockerProvider.Id
                    ? DockerResourceNamer.Instance.VolumeName(
                          new WorkloadNamingSubject
                          {
                              GroupId = GroupId,
                              WorkloadId = string.Empty,
                              Inst8 = instance.Inst8,
                          },
                          "state")
                    : DefaultWorkloadResourceNamer.Instance.VolumeName(
                          new WorkloadNamingSubject
                          {
                              GroupId = GroupId,
                              WorkloadId = WorkloadId,
                              Inst8 = instance.Inst8,
                          },
                          "state");
                instance.SnapshotRetainedVolumes(new[]
                {
                    new RetainedVolumeSnapshotItem
                    {
                        VolumeId = "state",
                        ResourceName = resourceName,
                        Size = "1Gi",
                        Criticality = VolumeCriticality.Critical,
                        Description = "Z-Wave network + device bonds",
                    },
                }, Now);
            }

            Repository.Add(instance);
            return instance;
        }

        private static WorkloadDesiredSpec SpecFor(Guid instanceId, bool withCriticalVolume)
        {
            var inst8 = WorkloadInstance.ComputeInst8(instanceId);
            var spec = new WorkloadDesiredSpec
            {
                GroupInstanceId = Guid.NewGuid(),
                GroupId = GroupId,
                WorkloadId = WorkloadId,
                InstanceId = instanceId,
                Inst8 = inst8,
                Provider = KubernetesProvider.Id,
                ResourceName = DefaultWorkloadResourceNamer.Instance.ObjectName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        }),
                SecretName = DefaultWorkloadResourceNamer.Instance.SecretName(new WorkloadNamingSubject
                        {
                            GroupId = GroupId,
                            WorkloadId = WorkloadId,
                            Inst8 = inst8,
                        }),
                Labels = DefaultWorkloadResourceNamer.Instance.Labels(new WorkloadLabelSubject
                        {
                            InstanceId = instanceId,
                            InstallationId = "install-1",
                            SpecHash = "hash",
                        }),
                Image = "registry.example/zwave-js@sha256:" + new string('a', 64),
                Kind = WorkloadKind.SingleNode,
                Health = new WorkloadHealth { Mode = new WorkloadHealthMode("adapter-session"), HeartbeatSeconds = 30 },
                SpecHash = "hash",
            };
            return withCriticalVolume
                ? spec with
                {
                    Volumes = new[]
                    {
                        new ResolvedVolume
                        {
                            Id = "state",
                            ResourceName = "reeve-zwave-state",
                            MountPath = "/data",
                            Size = "1Gi",
                            Criticality = VolumeCriticality.Critical,
                        },
                    },
                }
                : spec;
        }
    }

    private sealed class FakeActiveIntentProvider : IActiveWorkloadIntentProvider
    {
        public Guid? ActiveIntentId { get; set; }
        public Task<WorkloadDesiredSpec?> TryGetActiveTargetSpecAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken)
            => Task.FromResult<WorkloadDesiredSpec?>(null);
        public Task<Guid?> GetActiveIntentIdAsync(Guid pluginInstanceId, CancellationToken cancellationToken)
            => Task.FromResult(ActiveIntentId);
    }

    private sealed class FakeDeploymentContextSource : IWorkloadDeploymentContextSource
    {
        private readonly Dictionary<Guid, string> groupByInstance = new();
        private readonly Dictionary<string, DeploymentManifest> byGroup = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(Guid pluginInstanceId, string groupId, DeploymentManifest deployment)
        {
            groupByInstance[pluginInstanceId] = groupId;
            byGroup[groupId] = deployment;
        }

        public string GroupOf(Guid pluginInstanceId)
            => groupByInstance.TryGetValue(pluginInstanceId, out var groupId) ? groupId : GroupId;

        public void Forget(Guid pluginInstanceId) => byGroup.Remove(GroupOf(pluginInstanceId));

        public DeploymentManifest? DeploymentOf(string groupId) => byGroup.GetValueOrDefault(groupId);

        public Task<WorkloadDeploymentContext?> GetForGroupAsync(string groupId, CancellationToken cancellationToken = default)
            => Task.FromResult(byGroup.TryGetValue(groupId, out var deployment)
                ? new WorkloadDeploymentContext { GroupId = groupId, Deployment = deployment }
                : null);
    }

    private sealed class FakeRequirements(FakeDeploymentContextSource context) : IWorkloadRequirementsService
    {
        public const string SourceKey = "local";

        public string Fingerprint { get; set; } = "fp-judged";

        public WorkloadRequirementsStatus Status { get; set; } = WorkloadRequirementsStatus.Found;

        public List<WorkloadInstallRequirementsQuery> Queries { get; } = [];

        public Task<WorkloadRequirementsResult> ForInstallAsync(
            WorkloadInstallRequirementsQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            if (Status == WorkloadRequirementsStatus.Invalid)
            {
                return Task.FromResult(WorkloadRequirementsResult.Invalid("the values cannot be resolved (test)"));
            }

            if (Status == WorkloadRequirementsStatus.NotFound || context.DeploymentOf(query.GroupId) is not { } deployment)
            {
                return Task.FromResult(WorkloadRequirementsResult.NotFound("no deployment (test)"));
            }

            var after = deployment.Workloads.ToDictionary(
                w => w.Id,
                w => WorkloadPrivilegeRequirements.FromWorkload(w, new Dictionary<string, string>()),
                StringComparer.Ordinal);
            return Task.FromResult(WorkloadRequirementsResult.Found(new WorkloadRequirementsRead
            {
                After = after,
                Changes = new Dictionary<string, WorkloadPrivilegeRequirementsDiff>(),
                AddedWorkloadIds = [.. after.Keys],
                RemovedWorkloadIds = [],
                Fingerprint = Fingerprint,
            }));
        }

        public Task<WorkloadRequirementsResult> ForParameterEditAsync(
            Guid workloadInstanceId, IReadOnlyDictionary<string, string?> newValues, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkloadRequirementsResult> ForUpdateAsync(
            Guid groupInstanceId,
            string targetVersion,
            IReadOnlyDictionary<string, string?>? operatorParameters = null,
            IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class InMemoryRepository : IWorkloadInstanceRepository
    {
        private readonly Dictionary<Guid, WorkloadInstance> store = new();

        public Guid? ThrowOnUpdateFor { get; set; }

        public List<Guid> Updated { get; } = new();

        public Dictionary<Guid, WorkloadInstanceState> PersistedStates { get; } = new();

        private readonly Dictionary<Guid, WorkloadInstance?> raced = new();
        private readonly Dictionary<Guid, (Func<WorkloadInstance?> Copy, int WritesLeft)> racers = new();

        public List<Guid> Reloaded { get; } = new();

        public void Add(WorkloadInstance instance) => store[instance.Id] = instance;

        public void RaceWith(Guid id, Func<WorkloadInstance?> storedCopy, int writes = 1)
        {
            racers[id] = (storedCopy, writes - 1);
            Store(id, storedCopy());
        }

        private void Store(Guid id, WorkloadInstance? copy)
        {
            raced[id] = copy;
            if (copy is null)
            {
                PersistedStates.Remove(id);
            }
            else
            {
                PersistedStates[id] = copy.State;
            }
        }

        public Task<WorkloadInstance?> ReloadAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Reloaded.Add(id);
            if (!raced.TryGetValue(id, out var stored))
            {
                return GetByIdAsync(id, cancellationToken);
            }

            if (stored is null)
            {
                store.Remove(id);
                return Task.FromResult<WorkloadInstance?>(null);
            }

            store[id] = stored;
            var (copy, writesLeft) = racers[id];
            if (writesLeft > 0)
            {
                racers[id] = (copy, writesLeft - 1);
                Store(id, copy());
            }

            return Task.FromResult<WorkloadInstance?>(stored);
        }

        private void ThrowIfStale(WorkloadInstance instance)
        {
            if (raced.TryGetValue(instance.Id, out var stored) && !ReferenceEquals(stored, instance))
            {
                throw new WorkloadConcurrencyConflictException("written by a concurrent writer (test)") { EntityId = instance.Id };
            }
        }

        public Task<WorkloadInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(store.TryGetValue(id, out var i) ? i : null);

        public Task<WorkloadInstance?> GetByWorkloadAsync(Guid pluginInstanceId, string workloadId, CancellationToken cancellationToken = default)
        {
            var normalized = (workloadId ?? string.Empty).Trim();
            var match = store.Values.FirstOrDefault(i => i.GroupInstanceId == pluginInstanceId && i.WorkloadId == normalized);
            return Task.FromResult<WorkloadInstance?>(match);
        }

        public Task<IReadOnlyList<WorkloadInstance>> ListByPluginInstanceAsync(Guid pluginInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(
                store.Values.Where(i => i.GroupInstanceId == pluginInstanceId).OrderBy(i => i.WorkloadId, StringComparer.Ordinal).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListByStateAsync(WorkloadInstanceState state, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.Where(i => i.State == state).ToList());

        public Task<IReadOnlyList<WorkloadInstance>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkloadInstance>>(store.Values.ToList());

        public Task<IReadOnlyDictionary<WorkloadInstanceState, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<WorkloadInstanceState, int>>(
                store.Values.GroupBy(i => i.State).ToDictionary(g => g.Key, g => g.Count()));

        public Task AddAsync(WorkloadInstance instance, CancellationToken cancellationToken = default) { Add(instance); return Task.CompletedTask; }
        public Task UpdateAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            if (ThrowOnUpdateFor == instance.Id)
            {
                ThrowOnUpdateFor = null;
                throw new InvalidOperationException("persistence fault (test)");
            }

            ThrowIfStale(instance);
            Updated.Add(instance.Id);
            PersistedStates[instance.Id] = instance.State;
            Add(instance);
            return Task.CompletedTask;
        }

        public Task<bool> TrySetRestoreBindingUnresolvedAsync(Guid workloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            if (!store.TryGetValue(workloadInstanceId, out var instance)) { return Task.FromResult(false); }
            instance.SetRestoreBindingUnresolved(true, nowUtc);
            return Task.FromResult(true);
        }

        public Task DeleteAsync(WorkloadInstance instance, CancellationToken cancellationToken = default)
        {
            ThrowIfStale(instance);
            store.Remove(instance.Id);
            PersistedStates.Remove(instance.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditWriter : IWorkloadAuditSink
    {
        public List<Reeve.Workloads.Abstractions.WorkloadAuditEntry> Entries { get; } = new();

        public Task WriteAsync(
            Reeve.Workloads.Abstractions.WorkloadAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }


    private sealed class FakeSettingsService : IWorkloadOrchestratorSettingsService
    {
        public WorkloadProviderId EffectiveProvider { get; set; } = KubernetesProvider.Id;
        public bool Throws { get; set; }

        public Task<WorkloadOrchestratorSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
        {
            if (Throws)
            {
                throw new InvalidOperationException("settings store unavailable (test)");
            }

            return Task.FromResult(new WorkloadOrchestratorSettingsResponse
            {
                Settings = new WorkloadOrchestratorSettings(),
                EffectiveProvider = EffectiveProvider,
                AutoDetected = true,
                Providers = Array.Empty<WorkloadProviderInfo>(),
            });
        }


        public Task<WorkloadProviderProbeResult> TestAsync(WorkloadProviderId? requestedProvider, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeRetainedVolumeService : IRetainedVolumeService
    {
        public List<(WorkloadInstance Instance, IReadOnlyList<RetainedVolumeInput> Volumes)> Recorded { get; } = new();

        public bool ThrowOnRecord { get; set; }

        public Task<int> RecordRetainedAsync(WorkloadInstance instance, IReadOnlyList<RetainedVolumeInput> volumes, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRecord)
            {
                throw new InvalidOperationException("durable retained-record write failed (test)");
            }

            Recorded.Add((instance, volumes));
            return Task.FromResult(volumes.Count);
        }

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(Array.Empty<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>());

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(Array.Empty<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>());

        private readonly List<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume> activeByPluginType = new();

        public void SeedActive(Guid id, string pluginTypeId, string workloadId, string volumeId, string resourceName,
            WorkloadProviderId? provider = null)
            => activeByPluginType.Add(Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume.Record(
                id, pluginTypeId, workloadId, volumeId, resourceName, "1Gi",
                VolumeCriticality.Critical, description: null, provider ?? KubernetesProvider.Id,
                provider == DockerProvider.Id
                    ? Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolumeSchemeId.Docker
                    : Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolumeSchemeId.Kubernetes,
                sourceGroupInstanceId: Guid.NewGuid(), sourceWorkloadInstanceId: Guid.NewGuid(),
                retainedAtUtc: new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero)));

        public void SeedActiveBySource(Guid id, string pluginTypeId, string workloadId, string volumeId, string resourceName,
            Guid sourceWorkloadInstanceId, WorkloadProviderId? provider = null)
            => activeByPluginType.Add(Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume.Record(
                id, pluginTypeId, workloadId, volumeId, resourceName, "1Gi",
                VolumeCriticality.Critical, description: null, provider ?? KubernetesProvider.Id,
                provider == DockerProvider.Id
                    ? Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolumeSchemeId.Docker
                    : Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolumeSchemeId.Kubernetes,
                sourceGroupInstanceId: Guid.NewGuid(), sourceWorkloadInstanceId: sourceWorkloadInstanceId,
                retainedAtUtc: new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero)));

        public void SeedClaimedByOther(Guid rowId, Guid otherInstanceId)
            => activeByPluginType.FirstOrDefault(r => r.Id == rowId)?.TryClaimForRestore(otherInstanceId);

        public Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume? Row(Guid rowId)
            => activeByPluginType.FirstOrDefault(r => r.Id == rowId);

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveByPluginTypeAsync(string pluginTypeId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(
                activeByPluginType.Where(r => r.GroupId == pluginTypeId && r.IsActive).ToList());

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListOfferableByPluginTypeAsync(string pluginTypeId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(
                activeByPluginType.Where(r => r.GroupId == pluginTypeId && r.IsActive && r.ClaimedByWorkloadInstanceId is null).ToList());

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveByPluginTypeForInstanceAsync(string pluginTypeId, Guid forWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(
                activeByPluginType.Where(r => r.GroupId == pluginTypeId && r.IsActive
                    && (r.ClaimedByWorkloadInstanceId is null || r.ClaimedByWorkloadInstanceId == forWorkloadInstanceId)).ToList());

        public List<Guid> ClaimedRowIds { get; } = new();

        public List<Guid> ReleasedForInstance { get; } = new();

        public Task<bool> TryClaimForRestoreAsync(Guid retainedVolumeId, Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
        {
            var row = activeByPluginType.FirstOrDefault(r => r.Id == retainedVolumeId);
            var won = row?.TryClaimForRestore(claimingWorkloadInstanceId) ?? false;
            if (won)
            {
                ClaimedRowIds.Add(retainedVolumeId);
            }

            return Task.FromResult(won);
        }

        public Task<int> ReleaseClaimsForInstanceAsync(Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
        {
            ReleasedForInstance.Add(claimingWorkloadInstanceId);
            return Task.FromResult(activeByPluginType.Count(r => r.ReleaseClaimFor(claimingWorkloadInstanceId)));
        }

        public Task<bool> AnyActiveClaimForResourceNameAsync(string resourceName, Guid? excludingClaimingInstanceId, CancellationToken cancellationToken = default)
        {
            var normalized = (resourceName ?? string.Empty).Trim();
            return Task.FromResult(activeByPluginType.Any(r => r.ResourceName == normalized
                                                               && r.IsActive
                                                               && r.ClaimedByWorkloadInstanceId is not null
                                                               && r.ClaimedByWorkloadInstanceId != excludingClaimingInstanceId));
        }

        public Task<bool> AnyActiveClaimByInstanceAsync(Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(activeByPluginType.Any(r => r.IsActive && r.ClaimedByWorkloadInstanceId == claimingWorkloadInstanceId));

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveBySourceInstanceAsync(Guid sourceWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(
                activeByPluginType.Where(r => r.SourceWorkloadInstanceId == sourceWorkloadInstanceId && r.IsActive).ToList());

        public Task<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume?> FindActiveByResourceNameAsync(string resourceName, CancellationToken cancellationToken = default)
        {
            var normalized = (resourceName ?? string.Empty).Trim();
            return Task.FromResult(activeByPluginType.FirstOrDefault(r => r.ResourceName == normalized && r.IsActive));
        }

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListOlderThanAsync(TimeSpan age, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(Array.Empty<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>());

        public List<Guid> ReattachedForInstance { get; } = new();

        public List<Guid> Reattached { get; } = new();

        public int ReattachRowCount { get; set; } = 1;

        public Task MarkReattachedAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            Reattached.Add(id);
            var match = activeByPluginType.FirstOrDefault(r => r.Id == id);
            match?.MarkReattached(nowUtc);
            return Task.CompletedTask;
        }

        public Task<int> MarkReattachedForInstanceAsync(Guid sourceWorkloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            ReattachedForInstance.Add(sourceWorkloadInstanceId);
            return Task.FromResult(ReattachRowCount);
        }

        public Task PurgeAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> TryMarkReattachedAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<RetainedVolumePurgeResult> TryPurgeAsync(Guid id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(RetainedVolumePurgeResult.NotFound());

        public List<Guid> PurgedForInstance { get; } = new();

        public int PurgeRowCount { get; set; } = 1;

        public Task<int> MarkPurgedForInstanceAsync(Guid sourceWorkloadInstanceId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            PurgedForInstance.Add(sourceWorkloadInstanceId);
            return Task.FromResult(PurgeRowCount);
        }
    }

    internal sealed class C1Nf3RetainedVolumeRepository : IRetainedVolumeRepository
    {
        private readonly List<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume> store = new();

        public void Seed(Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume row) => store.Add(row);

        public IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume> Active
            => store.Where(r => r.IsActive).ToList();

        public Task<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(store.FirstOrDefault(r => r.Id == id));

        public Task<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume?> FindBySourceAsync(Guid sourceWorkloadInstanceId, string volumeId, CancellationToken cancellationToken = default)
        {
            var normalized = (volumeId ?? string.Empty).Trim();
            return Task.FromResult(store.FirstOrDefault(r => r.SourceWorkloadInstanceId == sourceWorkloadInstanceId && r.VolumeId == normalized));
        }

        public Task<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume?> FindActiveByResourceNameAsync(string resourceName, CancellationToken cancellationToken = default)
        {
            var normalized = (resourceName ?? string.Empty).Trim();
            return Task.FromResult(store.FirstOrDefault(r => r.ResourceName == normalized && r.IsActive));
        }

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveBySourceInstanceAsync(Guid sourceWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(store.Where(r => r.SourceWorkloadInstanceId == sourceWorkloadInstanceId && r.IsActive).ToList());

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(store.ToList());

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(store.Where(r => r.IsActive).ToList());

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveByPluginTypeAsync(string pluginTypeId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(store.Where(r => r.GroupId == pluginTypeId && r.IsActive).ToList());

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveWithoutLiveSourceOlderThanAsync(DateTimeOffset retainedOnOrBeforeUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(store.Where(r => r.IsActive && r.ClaimedByWorkloadInstanceId is null && r.RetainedAtUtc <= retainedOnOrBeforeUtc).ToList());

        public Task<bool> TryClaimForRestoreAsync(Guid retainedVolumeId, Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(store.FirstOrDefault(r => r.Id == retainedVolumeId)?.TryClaimForRestore(claimingWorkloadInstanceId) ?? false);

        public Task<int> ReleaseClaimsForInstanceAsync(Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(store.Count(r => r.ReleaseClaimFor(claimingWorkloadInstanceId)));

        public Task<bool> ReleaseClaimAsync(Guid rowId, Guid claimant, CancellationToken cancellationToken = default)
        {
            var row = store.FirstOrDefault(r => r.Id == rowId);
            if (row is null || row.ClaimedByWorkloadInstanceId != claimant || !row.IsActive)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(row.ReleaseClaimFor(claimant));
        }

        public Task<bool> SourceInstanceExistsAsync(Guid sourceWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveWithoutLiveSourceAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(store.Where(r => r.IsActive).OrderBy(r => r.RetainedAtUtc).ToList());

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListOfferableByPluginTypeAsync(string pluginTypeId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(store.Where(r => r.GroupId == pluginTypeId && r.IsActive && r.ClaimedByWorkloadInstanceId is null).ToList());

        public Task<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>> ListActiveByPluginTypeForInstanceAsync(string pluginTypeId, Guid forWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume>>(store.Where(r => r.GroupId == pluginTypeId && r.IsActive && (r.ClaimedByWorkloadInstanceId is null || r.ClaimedByWorkloadInstanceId == forWorkloadInstanceId)).ToList());

        public Task<bool> AnyActiveClaimForResourceNameAsync(string resourceName, Guid? excludingClaimingInstanceId, CancellationToken cancellationToken = default)
        {
            var normalized = (resourceName ?? string.Empty).Trim();
            return Task.FromResult(store.Any(r => r.ResourceName == normalized && r.IsActive && r.ClaimedByWorkloadInstanceId is not null && r.ClaimedByWorkloadInstanceId != excludingClaimingInstanceId));
        }

        public Task<bool> AnyActiveClaimByInstanceAsync(Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(store.Any(r => r.IsActive && r.ClaimedByWorkloadInstanceId == claimingWorkloadInstanceId));

        public Task AddAsync(Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
        {
            if (store.Any(r => r.ResourceName == retainedVolume.ResourceName && r.IsActive))
            {
                throw new InvalidOperationException(
                    $"active-resource-name uniqueness violated for '{retainedVolume.ResourceName}' (partial-unique index; fake fidelity)");
            }

            store.Add(retainedVolume);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Reeve.Workloads.Abstractions.Domain.Entities.RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
        {
            if (!store.Contains(retainedVolume))
            {
                store.Add(retainedVolume);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StubRegistry(WorkloadProviderId? registered, IWorkloadProvider provider) : IWorkloadProviderRegistry
    {
        public IWorkloadProvider? TryResolve(WorkloadProviderId kind)
            => registered is { } r && r == kind ? provider : null;

        public IWorkloadProvider Resolve(WorkloadProviderId kind)
            => TryResolve(kind) ?? throw new WorkloadProviderNotRegisteredException(kind);

        public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders
            => registered is { } r ? new[] { r } : Array.Empty<WorkloadProviderId>();

        public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors
            => [.. RegisteredProviders.Select(Describe)];

        public WorkloadProviderDescriptor Describe(WorkloadProviderId kind)
            => TryResolve(kind)?.Descriptor ?? ShippedProviders.For(kind);
    }

    private sealed class ThrowingProvider(InMemoryWorkloadProvider inner) : IWorkloadProvider
    {
        public bool ThrowOnTeardown { get; init; }

        public Exception? TeardownFault { get; init; }

        public bool HangOnTeardown { get; init; }

        public WorkloadProviderDescriptor Descriptor => inner.Descriptor;

        public Task<WorkloadApplyResult> ApplyAsync(WorkloadDesiredSpec spec, IProgress<WorkloadApplyProgress>? progress, CancellationToken cancellationToken = default, bool forceConflicts = false)
            => inner.ApplyAsync(spec, progress, cancellationToken, forceConflicts);

        public Task TeardownAsync(WorkloadRef reference, bool retainVolumes, CancellationToken cancellationToken = default)
        {
            if (ThrowOnTeardown)
            {
                throw new InvalidOperationException("provider unreachable during teardown (test)");
            }

            if (TeardownFault is { } fault)
            {
                throw fault;
            }

            return HangOnTeardown
                ? Task.Delay(Timeout.Infinite, cancellationToken)
                : inner.TeardownAsync(reference, retainVolumes, cancellationToken);
        }

        public Task SuspendAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => inner.SuspendAsync(reference, cancellationToken);

        public Task ResumeAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => inner.ResumeAsync(reference, cancellationToken);

        public Task<WorkloadStatus> GetStatusAsync(WorkloadRef reference, CancellationToken cancellationToken = default)
            => inner.GetStatusAsync(reference, cancellationToken);

        public Task<WorkloadPreflightResult> PreflightAsync(WorkloadDesiredSpec spec, CancellationToken cancellationToken = default)
            => inner.PreflightAsync(spec, cancellationToken);

        public Task<WorkloadLogs> GetRecentLogsAsync(WorkloadRef reference, int tailLines, bool previousContainer, CancellationToken cancellationToken = default)
            => inner.GetRecentLogsAsync(reference, tailLines, previousContainer, cancellationToken);

        public Task<WorkloadAttachmentExistence> GetAttachmentExistenceAsync(
            IReadOnlyList<string> attachments, CancellationToken cancellationToken = default)
            => inner.GetAttachmentExistenceAsync(attachments, cancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset now) : IWorkloadClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

}
