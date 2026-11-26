using System;
using Docker.DotNet;

namespace Reeve.Workloads.Providers.Docker;

internal interface IDockerClientFactory
{
    IDockerClient CreateClient(string? socketUri = null);
}
