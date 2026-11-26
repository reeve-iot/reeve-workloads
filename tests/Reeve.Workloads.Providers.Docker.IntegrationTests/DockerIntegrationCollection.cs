namespace Reeve.Workloads.Providers.Docker.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class DockerIntegrationCollection : ICollectionFixture<DockerDaemonFixture>
{
    public const string Name = "docker-integration";
}
