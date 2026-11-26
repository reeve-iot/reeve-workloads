using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities.Documents;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Reeve.Workloads.Tests.Shared;
using CredentialFree = Reeve.Workloads.Tests.Shared.CredentialFree;

namespace Reeve.Workloads.Composition.Tests.EntityFrameworkCore;

public sealed class WorkloadSpecDocumentAcrossVersionsTests
{
    private static readonly JsonSerializerOptions Options = WorkloadDocumentStringColumns.Options;

    [Fact]
    public void A_document_without_credential_fields_reads_back_with_no_credentials()
    {
        var writtenWithoutCredentials = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<CredentialFree.WorkloadSpecMapDocument>(WrittenWithCredentials(), Options), Options);
        writtenWithoutCredentials.Should().NotContain("CredentialId").And.NotContain("ProvisionedCredentials");

        var document = JsonSerializer.Deserialize<WorkloadSpecMapDocument>(writtenWithoutCredentials, Options)!;
        var spec = WorkloadDocumentMapper.FromDocument(document)["gateway"];

        document.Workloads.Single().ProvisionedCredentials.Should().BeNull();
        spec.ProvisionedCredentials.Should().BeEmpty();
        spec.Env.Select(env => env.CredentialId).Should().Equal(null, null);
    }

    [Fact]
    public void A_document_with_credentials_reads_back_through_a_credential_free_shape()
    {
        var writtenWithCredentials = WrittenWithCredentials();

        var credentialFree = JsonSerializer.Deserialize<CredentialFree.WorkloadSpecMapDocument>(writtenWithCredentials, Options)!;

        JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(credentialFree, Options),
                WithoutCredentialKeys(JsonNode.Parse(writtenWithCredentials)!))
            .Should().BeTrue();
        credentialFree.Workloads.Single().Env.Select(env => (env.Name, env.Kind, env.SecretRef))
            .Should().Equal(("REEVE_WORKLOAD_TOKEN", "SecretRef", "reeve-gateway-secret"), ("LOG_LEVEL", "Literal", null));
    }

    private static string WrittenWithCredentials()
    {
        var json = JsonSerializer.Serialize(WorkloadDocumentMapper.SpecMap(CredentialSpecs.Map()), Options);
        json.Should().Contain("\"CredentialId\":\"workload-token\"").And.Contain("\"FEED_TOKEN\"");
        return json;
    }

    private static JsonNode WithoutCredentialKeys(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("CredentialId");
                obj.Remove("ProvisionedCredentials");
                foreach (var child in obj.Select(pair => pair.Value).OfType<JsonNode>().ToList())
                {
                    WithoutCredentialKeys(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array.OfType<JsonNode>())
                {
                    WithoutCredentialKeys(child);
                }

                break;
        }

        return node;
    }
}
