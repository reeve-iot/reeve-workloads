using Reeve.Workloads.Providers.Kubernetes.Rendering;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Rendering;
using Reeve.Workloads.Core.Rendering;
using Reeve.Workloads.Providers.Docker.Rendering;
using Reeve.Workloads.Providers.Docker;

namespace Reeve.Workloads.Core.Tests;

public sealed class WorkloadArtifactInjectionTests
{
    private const string Injected = "X\nprivileged: true\nnetwork_mode: host";

    [Fact]
    public void AnImageReferenceCarryingANewline_CannotAddKeysToTheComposeFile()
    {
        var content = new ComposeArtifactRenderer()
            .Render(Spec(image: Injected), Options).PrimaryContent;

        content.Should().NotContain("\nprivileged: true");
        content.Should().NotContain("\nnetwork_mode: host");
    }

    [Fact]
    public void AnEnvNameCarryingANewline_CannotAddKeysToTheComposeFile()
    {
        var content = new ComposeArtifactRenderer()
            .Render(SpecWithEnv(name: Injected, value: "v"), Options).PrimaryContent;

        content.Should().NotContain("\nprivileged: true");
    }

    [Fact]
    public void APlainScalarWithATrailingNewlineIsQuoted()
    {
        WorkloadArtifactEscaping.YamlPlainOrQuoted("image:tag\n").Should().Be("\"image:tag\\n\"");
    }

    [Fact]
    public void AnEnvValueCarryingANewline_CannotAddKeysToTheComposeFile()
    {
        var content = new ComposeArtifactRenderer()
            .Render(SpecWithEnv(name: "OK", value: Injected), Options).PrimaryContent;

        content.Should().NotContain("\nprivileged: true");
    }

    [Fact]
    public void AQuoteInTheImageReference_CannotCloseTheHelmScalar()
    {
        var content = new HelmValuesArtifactRenderer()
            .Render(Spec(image: "reg/img\"\nprivileged: true"), Options).PrimaryContent;

        content.Should().NotContain("\nprivileged: true");
    }

    [Fact]
    public void AnEnvNameCarryingANewline_CannotAddEntriesToTheHelmValues()
    {
        var content = new HelmValuesArtifactRenderer()
            .Render(SpecWithEnv(name: Injected, value: "v"), Options).PrimaryContent;

        content.Should().NotContain("\nprivileged: true");
    }

    [Fact]
    public void AnEnvValueCarryingANewline_CannotAddEntriesToTheDotEnvFile()
    {
        var artifact = new EnvArtifactRenderer()
            .Render(SpecWithEnv(name: "OK", value: "v\nINJECTED=1"), Options);

        artifact.PrimaryContent.Should().NotContain("\nINJECTED=1");
    }

    [Theory]
    [InlineData("BAD NAME")]
    [InlineData("FEED\n")]
    public void AnEnvNameThatIsNotAnIdentifier_IsOmittedFromTheDotEnvFileAndSaidSo(string name)
    {
        var artifact = new EnvArtifactRenderer()
            .Render(SpecWithEnv(name: name, value: "v"), Options);

        artifact.PrimaryContent.Should().NotContain(name);
        artifact.Notes.Should().Contain(n => n.Contains("OMITTED") && n.Contains(name));
    }

    [Fact]
    public void AnOrdinaryDotEnvValue_IsWrittenVERBATIM_NotQuoted()
    {
        var content = new EnvArtifactRenderer()
            .Render(SpecWithEnv(name: "GATEWAY_URL", value: "wss://reeve.example/ws"),
                Options).PrimaryContent;

        content.Should().Contain("GATEWAY_URL=wss://reeve.example/ws");
        content.Should().NotContain("GATEWAY_URL=\"");
    }

    [Fact]
    public void ADotEnvValueCarryingALineBreak_IsOmittedRatherThanQuoted()
    {
        var artifact = new EnvArtifactRenderer()
            .Render(SpecWithEnv(name: "TOKEN", value: "abc\nINJECTED=1"), Options);

        artifact.PrimaryContent.Should().NotContain("INJECTED=1");
        artifact.Notes.Should().Contain(n => n.Contains("OMITTED") && n.Contains("TOKEN"));
    }

    [Fact]
    public void APasswordShapedSecretRef_RendersAPlaceholderInEveryTextFormat()
    {
        var spec = SpecWithEnv(name: "DB_PASSWORD", value: null, kind: ResolvedEnvKind.SecretRef);
        var options = Options;

        foreach (var content in new[]
                 {
                     new ComposeArtifactRenderer().Render(spec, options).PrimaryContent,
                     new HelmValuesArtifactRenderer().Render(spec, options).PrimaryContent,
                     new EnvArtifactRenderer().Render(spec, options).PrimaryContent,
                 })
        {
            content.Should().Contain(WorkloadEnvRendering.SecretPlaceholder);
        }
    }

    [Fact]
    public void AMountPathCarryingANewline_CannotAddKeysToTheHelmValues()
    {
        var spec = Spec() with
        {
            ConfigMapMounts = [new ResolvedConfigMapMount { MountPath = Injected, ConfigMapName = "cfg" }],
        };

        var content = new HelmValuesArtifactRenderer().Render(spec, Options).PrimaryContent;

        content.Should().NotContain("\nprivileged: true");
        content.Should().NotContain("\nnetwork_mode: host");
    }

    [Fact]
    public void AHostPathCarryingAQuote_CannotBreakOutOfTheComposeBindMount()
    {
        var spec = Spec() with
        {
            Privileges = new ResolvedPrivileges
            {
                HostPaths = [new ResolvedHostPath { HostPath = "/etc", MountPath = "/host\"\nprivileged: true" }],
            },
        };

        var content = new ComposeArtifactRenderer().Render(spec, Options).PrimaryContent;

        content.Should().NotContain("\nprivileged: true");
    }

    [Theory]
    [InlineData("/cache")]
    [InlineData("/data")]
    [InlineData("/dev/ttyUSB0")]
    [InlineData("/etc/reeve/config")]
    [InlineData("/metrics")]
    [InlineData("/state")]
    [InlineData("/var/lib/reeve/ble/state")]
    [InlineData("ALL")]
    [InlineData("NET_ADMIN")]
    [InlineData("LOG_LEVEL")]
    [InlineData("REEVE_GATEWAY_URL")]
    [InlineData("abc123def456")]
    [InlineData("cache")]
    [InlineData("critical")]
    [InlineData("recoverable")]
    [InlineData("host")]
    [InlineData("info")]
    [InlineData("matter-sidecar")]
    [InlineData("reeve")]
    [InlineData("reeve-wl-com-reeve-matter-matter-11111111")]
    [InlineData("reeve-wl-pvc-retained-state")]
    [InlineData("rwo")]
    [InlineData("ghcr.io/reeve/matter@sha256:1111111111111111111111111111111111111111111111111111111111111111")]
    [InlineData("wss://reeve.example.local/workloads/gw")]
    public void AValueTheGoldensRenderPlain_IsLeftVerbatim(string value)
        => WorkloadArtifactEscaping.YamlPlainOrQuoted(value).Should().Be(value);

    [Theory]
    [InlineData("/data\nprivileged: true")]
    [InlineData("/data extra")]
    [InlineData("/data\"")]
    [InlineData("value:")]
    [InlineData("-leading-dash")]
    [InlineData("off")]
    [InlineData("null")]
    public void AValueThatWouldNotReadBackAsItself_IsQuoted(string value)
    {
        var rendered = WorkloadArtifactEscaping.YamlPlainOrQuoted(value);

        rendered.Should().StartWith("\"").And.EndWith("\"");
        rendered.Should().NotContain("\n").And.NotContain("\r");
    }

    [Fact]
    public void TheQuotedFormEscapesTheQuoteThatWouldEndIt()
        => WorkloadArtifactEscaping.YamlPlainOrQuoted("""/data", privileged: true""")
            .Should().Be(""""
            "/data\", privileged: true"
            """".Trim());

    private static WorkloadArtifactRenderOptions Options =>
        new();

    private static WorkloadDesiredSpec SpecWithEnv(
        string name, string? value, ResolvedEnvKind kind = ResolvedEnvKind.Literal)
        => Spec() with { Env = [new ResolvedEnvVar { Name = name, Kind = kind, Value = value }] };

    private static WorkloadDesiredSpec Spec(string? image = null)
        => new()
        {
            GroupInstanceId = Guid.NewGuid(),
            GroupId = "mod",
            WorkloadId = "w",
            InstanceId = Guid.NewGuid(),
            Inst8 = "abcd1234",
            Provider = DockerProvider.Id,
            ResourceName = "res",
            SecretName = "res",
            Labels = new Dictionary<string, string>(),
            Image = image ?? "reg/img@sha256:" + new string('a', 64),
            Kind = WorkloadKind.SingleNode,
            Health = new WorkloadHealth { Mode = WorkloadHealthMode.Container },
            SpecHash = "unset",
            Env = [],
        };
}
