using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.Core.Providers;

/// <summary>Indexes the DI-registered <see cref="IWorkloadProvider"/> set by id. An empty set is allowed — <see cref="TryResolve"/> returns null. A duplicate id (wiring bug) has the last one win, deterministically; not fatal.</summary>
public sealed class WorkloadProviderRegistry : IWorkloadProviderRegistry
{
    private readonly IReadOnlyDictionary<WorkloadProviderId, IWorkloadProvider> _byId;
    private readonly IReadOnlyList<WorkloadProviderDescriptor> _descriptors;

    /// <summary>The DI constructor. Every provider is indexed BEHIND ITS DEADLINE — here rather than in each
    /// provider, because a provider a third party shipped cannot be asked to remember to have one.</summary>
    public WorkloadProviderRegistry(
        IEnumerable<IWorkloadProvider> providers,
        IOptions<WorkloadProviderTimeoutOptions> timeouts,
        ILoggerFactory loggerFactory)
        : this(Bounded(providers, timeouts?.Value ?? new WorkloadProviderTimeoutOptions(), loggerFactory))
    {
    }

    public WorkloadProviderRegistry(IEnumerable<IWorkloadProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var byId = new Dictionary<WorkloadProviderId, IWorkloadProvider>();
        var descriptors = new List<WorkloadProviderDescriptor>();
        foreach (var provider in providers)
        {
            if (byId.ContainsKey(provider.Descriptor.Id))
            {
                descriptors.RemoveAll(d => d.Id == provider.Descriptor.Id);
            }

            byId[provider.Descriptor.Id] = provider;
            descriptors.Add(provider.Descriptor);
        }

        _byId = byId;
        _descriptors = descriptors;
    }

    public IReadOnlyCollection<WorkloadProviderId> RegisteredProviders => (IReadOnlyCollection<WorkloadProviderId>)_byId.Keys;

    public IReadOnlyCollection<WorkloadProviderDescriptor> Descriptors => _descriptors;

    public IWorkloadProvider? TryResolve(WorkloadProviderId provider)
        => _byId.TryGetValue(provider, out var impl) ? impl : null;

    public IWorkloadProvider Resolve(WorkloadProviderId provider)
        => TryResolve(provider) ?? throw new WorkloadProviderNotRegisteredException(provider);

    public WorkloadProviderDescriptor Describe(WorkloadProviderId provider)
        => TryResolve(provider)?.Descriptor ?? new WorkloadProviderDescriptor { Id = provider };

    private static IEnumerable<IWorkloadProvider> Bounded(
        IEnumerable<IWorkloadProvider> providers,
        WorkloadProviderTimeoutOptions timeouts,
        ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<BoundedWorkloadProvider>();
        return providers.Select(provider => (IWorkloadProvider)new BoundedWorkloadProvider(provider, timeouts, logger));
    }
}
