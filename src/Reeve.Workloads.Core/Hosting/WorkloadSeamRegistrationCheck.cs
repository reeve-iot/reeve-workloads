using Microsoft.Extensions.DependencyInjection;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Core.HealthGate;

namespace Reeve.Workloads.Core;

internal static class WorkloadSeamRegistrationCheck
{
    public static void Run(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        using var scope = scopeFactory.CreateScope();
        Verify(
            scope.ServiceProvider.GetServices<IWorkloadHealthContributor>(),
            scope.ServiceProvider.GetServices<IRolloutCertifier>());
    }

    public static void Verify(
        IEnumerable<IWorkloadHealthContributor> contributors, IEnumerable<IRolloutCertifier> certifiers)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        ArgumentNullException.ThrowIfNull(certifiers);

        var owners = new Dictionary<WorkloadHealthMode, IWorkloadHealthContributor>();
        foreach (var contributor in contributors)
        {
            foreach (var mode in contributor.ObservedModes)
            {
                if (owners.TryGetValue(mode, out var owner))
                {
                    throw new InvalidOperationException(
                        $"Two health contributors observe the health mode '{mode}': {owner.GetType().FullName} and "
                        + $"{contributor.GetType().FullName}. Each mode has one contributor at most.");
                }

                owners[mode] = contributor;
            }
        }

        var registered = certifiers.Select(certifier => certifier.GetType().FullName).ToList();
        if (registered.Count > 1)
        {
            throw new InvalidOperationException(
                $"{registered.Count} implementations of {nameof(IRolloutCertifier)} are registered "
                + $"({string.Join(", ", registered)}); the health gate takes one at most.");
        }
    }
}
