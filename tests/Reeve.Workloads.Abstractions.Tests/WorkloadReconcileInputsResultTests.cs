using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Tests.Shared;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadReconcileInputsResultTests
{
    [Fact]
    public void Not_declared_carries_neither_inputs_nor_a_refusal()
    {
        WorkloadReconcileInputsResult.NotDeclared.Inputs.Should().BeNull();
        WorkloadReconcileInputsResult.NotDeclared.Refusal.Should().BeNull();
    }

    [Fact]
    public void Declared_carries_the_inputs_and_no_refusal()
    {
        var inputs = Inputs();

        var result = WorkloadReconcileInputsResult.Declared(inputs);

        result.Inputs.Should().BeSameAs(inputs);
        result.Refusal.Should().BeNull();
    }

    [Fact]
    public void Host_refused_carries_the_reason_and_no_inputs()
    {
        var result = WorkloadReconcileInputsResult.HostRefused("host says no");

        result.Refusal.Should().Be("host says no");
        result.Inputs.Should().BeNull();
        result.Should().NotBe(WorkloadReconcileInputsResult.NotDeclared);
    }

    [Fact]
    public void Declared_without_inputs_throws()
        => ((Action)(() => WorkloadReconcileInputsResult.Declared(null!))).Should().Throw<ArgumentNullException>();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_refusal_without_a_reason_throws(string? reason)
        => ((Action)(() => WorkloadReconcileInputsResult.HostRefused(reason!))).Should().Throw<ArgumentException>();

    private static WorkloadReconcileInputs Inputs() => new()
    {
        WorkloadManifest = new WorkloadManifest
        {
            Id = "controller",
            DisplayName = "controller",
            Image = new WorkloadImage { Ref = "registry.example/controller", Digest = "sha256:" + new string('a', 64) },
            Kind = WorkloadKind.Service,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
        },
        ResolvedParameters = new Dictionary<string, string?>(StringComparer.Ordinal),
        Settings = new WorkloadOrchestratorSettings(),
        Bootstrap = new WorkloadBootstrapContext { InstanceId = Guid.NewGuid(), SecretName = "reeve-wl-secret" },
        EffectiveProvider = StandInProviders.Kubernetes,
        Namer = DefaultWorkloadResourceNamer.Instance,
    };
}
