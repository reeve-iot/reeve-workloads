using System.Text.Json;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Manifest;

namespace Reeve.Workloads.Core.Tests;

public sealed class DeploymentManifestGrammarTests
{
    [Fact]
    public void AWellFormedManifest_Parses()
    {
        Parse(Manifest()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("GOOD_NAME")]
    [InlineData("_leading_underscore")]
    [InlineData("Mixed123")]
    public void AnEnvNameThatIsAnIdentifier_IsAccepted(string name)
        => Parse(Manifest(envName: name)).Should().BeEmpty();

    [Theory]
    [InlineData("BAD NAME")]
    [InlineData("BAD-NAME")]
    [InlineData("1LEADING_DIGIT")]
    [InlineData("")]
    public void AnEnvNameThatIsNotAnIdentifier_IsRejected(string name)
        => Parse(Manifest(envName: name))
            .Should().Contain(e => e.Code == DeploymentValidationCodes.InvalidEnvName
                                   || e.Code == DeploymentValidationCodes.MissingField);

    [Theory]
    [InlineData("reg/img")]
    [InlineData("registry.example.com:5000/team/img")]
    [InlineData("img")]
    public void AnOrdinaryImageReference_IsAccepted(string reference)
        => Parse(Manifest(imageRef: reference)).Should().BeEmpty();

    [Theory]
    [InlineData("reg/img\"")]
    [InlineData("reg/img with spaces")]
    [InlineData("-leading-dash")]
    public void AnImageReferenceOutsideTheCharacterSet_IsRejected(string reference)
        => Parse(Manifest(imageRef: reference))
            .Should().Contain(e => e.Code == DeploymentValidationCodes.InvalidImageRef);

    [Theory]
    [InlineData("INJECT\nprivileged: true")]
    [InlineData("INJECT\rprivileged: true")]
    [InlineData("INJECT\u0000")]
    [InlineData("FEED\n")]
    public void AnEnvNameCarryingAControlCharacter_IsRejectedByTheReaderNotByTheFieldRule(string name)
        => Parse(Manifest(envName: name))
            .Should().Contain(e => e.Code == DeploymentValidationCodes.ControlCharacterInString);

    [Theory]
    [InlineData("reg/img\nprivileged: true")]
    [InlineData("reg/img\tprivileged")]
    public void AnImageReferenceCarryingAControlCharacter_IsRejectedByTheReaderNotByTheFieldRule(string reference)
        => Parse(Manifest(imageRef: reference))
            .Should().Contain(e => e.Code == DeploymentValidationCodes.ControlCharacterInString);

    [Fact]
    public void AMountPathCarryingANewline_IsRejected()
    {
        Parse(ManifestWithVolume("/data\nprivileged: true"))
            .Should().Contain(e => e.Code == DeploymentValidationCodes.ControlCharacterInString);
    }

    [Fact]
    public void AnOrdinaryMountPath_IsAccepted()
        => Parse(ManifestWithVolume("/data")).Should().BeEmpty();

    private static IReadOnlyList<DeploymentValidationError> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        DeploymentManifestParser.Parse(document.RootElement, [], out var errors);
        return errors;
    }

    private static string ManifestWithVolume(string mountPath)
    {
        var digest = "sha256:" + new string('a', 64);
        return $$"""
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "w",
              "displayName": "W",
              "image": { "ref": "reg/img", "digest": "{{digest}}" },
              "kind": "single-node",
              "health": { "mode": "container" },
              "volumes": [ { "id": "data", "mountPath": {{JsonSerializer.Serialize(mountPath)}}, "size": "1Gi", "criticality": "recoverable" } ]
            }
          ]
        }
        """;
    }

    private static string Manifest(string envName = "GOOD_NAME", string imageRef = "reg/img")
    {
        var digest = "sha256:" + new string('a', 64);
        return $$"""
        {
          "schemaVersion": 1,
          "workloads": [
            {
              "id": "w",
              "displayName": "W",
              "image": { "ref": {{JsonSerializer.Serialize(imageRef)}}, "digest": "{{digest}}" },
              "kind": "single-node",
              "health": { "mode": "container" },
              "env": [ { "name": {{JsonSerializer.Serialize(envName)}}, "from": "literal:x" } ]
            }
          ]
        }
        """;
    }
}
