using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Reeve.Workloads.DependencyInjection;

/// <summary>The registration surface of the workload engine: every <c>Reeve.Workloads.*</c> package contributes
/// its own extension on this type, so a host composes the engine instead of naming its classes.</summary>
public sealed class ReeveWorkloadsBuilder
{
    private readonly HashSet<Assembly> libraryAssemblies = [];
    private readonly HashSet<Type> defaultedPorts = [];
    private readonly HashSet<Type> suppliedPorts = [];
    private readonly HashSet<ServiceDescriptor> libraryDescriptors = [];
    private readonly HashSet<Type> libraryImplementations = [];

    internal ReeveWorkloadsBuilder(IServiceCollection services, IConfiguration configuration)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>The collection being built. Exposed so a host can register its own ports beside the engine's
    /// services, and so a package extension outside this assembly can add to it.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The configuration the engine binds its options sections from, taken once at the entry point so
    /// no package extension needs a parameter of its own.</summary>
    public IConfiguration Configuration { get; }

    /// <summary>The <c>Reeve.Workloads.*</c> assemblies whose services this builder has registered. The port
    /// audit reads this: an interface is the HOST's to supply only when the engine declares it.</summary>
    public IReadOnlyCollection<Assembly> LibraryAssemblies => libraryAssemblies;

    /// <summary>Registers one engine service and records its assembly as part of the engine.</summary>
    public ReeveWorkloadsBuilder Add<TService, TImplementation>(ServiceLifetime lifetime)
        where TService : class
        where TImplementation : class, TService
    {
        Services.Add(new ServiceDescriptor(typeof(TService), typeof(TImplementation), lifetime));
        return Track(typeof(TImplementation));
    }

    /// <summary>Registers an engine service only when the host has not already supplied its own. Used for the
    /// few services with a deliberate no-op default; a host override must win.</summary>
    public ReeveWorkloadsBuilder TryAdd<TService, TImplementation>(ServiceLifetime lifetime)
        where TService : class
        where TImplementation : class, TService
    {
        Services.TryAdd(new ServiceDescriptor(typeof(TService), typeof(TImplementation), lifetime));
        return Track(typeof(TImplementation));
    }

    /// <summary>Supplies a HOST PORT the library can answer itself, so the host is not made to write a line of
    /// registration for a behaviour it does not want. Distinct from <see cref="TryAdd{TService,TImplementation}(ServiceLifetime)"/>
    /// only in that the port is remembered: a default that answers a port also HIDES it from the missing-port
    /// audit, and something has to be able to say which ports are standing on one.</summary>
    /// <remarks>Both override directions work. Registering later with <c>Add</c> wins because the container
    /// resolves the last descriptor; registering EARLIER wins because this is a TryAdd and does nothing. A host
    /// that wires its own port on either side of <c>AddReeveWorkloads</c> therefore gets its own, and the
    /// composition tests assert both directions rather than the convenient one.</remarks>
    public ReeveWorkloadsBuilder AddDefault<TService, TImplementation>(ServiceLifetime lifetime)
        where TService : class
        where TImplementation : class, TService
        => AddDefaultDescriptor<TService>(
            new ServiceDescriptor(typeof(TService), typeof(TImplementation), lifetime), typeof(TImplementation));

    /// <summary>The same, from a factory — for a default that has to REACH something at resolve time: the host's
    /// <see cref="TimeProvider"/> if it registered one.</summary>
    /// <remarks><typeparamref name="TImplementation"/> is explicit rather than inferred because a factory
    /// descriptor carries no implementation type, and the audit needs one to know whose assembly the default
    /// belongs to and which ports it in turn asks for. Registering by type stays the norm; this exists for the
    /// cases where it cannot be.</remarks>
    public ReeveWorkloadsBuilder AddDefault<TService, TImplementation>(
        Func<IServiceProvider, TImplementation> factory, ServiceLifetime lifetime)
        where TService : class
        where TImplementation : class, TService
    {
        ArgumentNullException.ThrowIfNull(factory);

        return AddDefaultDescriptor<TService>(
            new ServiceDescriptor(typeof(TService), sp => factory(sp), lifetime), typeof(TImplementation));
    }

    /// <summary>Like <see cref="TryAdd{TService,TImplementation}(ServiceLifetime)"/>, from a factory: for a service
    /// whose construction needs more than the container can infer from one constructor. Records no port.</summary>
    public ReeveWorkloadsBuilder TryAdd<TService, TImplementation>(
        Func<IServiceProvider, TImplementation> factory, ServiceLifetime lifetime)
        where TService : class
        where TImplementation : class, TService
    {
        ArgumentNullException.ThrowIfNull(factory);

        Services.TryAdd(new ServiceDescriptor(typeof(TService), sp => factory(sp), lifetime));
        return Track(typeof(TImplementation));
    }

    private ReeveWorkloadsBuilder AddDefaultDescriptor<TService>(ServiceDescriptor descriptor, Type implementation)
        where TService : class
    {
        defaultedPorts.Add(typeof(TService));
        libraryDescriptors.Add(descriptor);
        Services.TryAdd(descriptor);
        return Track(implementation);
    }

    /// <summary>The host ports the library offered a default for. Recorded by the registration itself, so it
    /// cannot drift from what was actually registered the way a hand-kept list beside it would.</summary>
    public IReadOnlyCollection<Type> DefaultedPorts => defaultedPorts;

    /// <summary>The ports the host answered through the <c>AddReeveWorkloads</c> callback. Only those: a port
    /// wired straight onto <see cref="Services"/> is invisible here, which is the cost of not using it.</summary>
    public IReadOnlyCollection<Type> SuppliedPorts => suppliedPorts;

    /// <summary>The descriptors this builder registered. Identity and not assembly, because a factory descriptor
    /// names no implementation type and "is this default still in force" can only be asked of the descriptor.</summary>
    public IReadOnlyCollection<ServiceDescriptor> LibraryDescriptors => libraryDescriptors;

    /// <summary>Every engine implementation type this builder registered, including those behind a factory
    /// descriptor that the collection alone cannot name.</summary>
    public IReadOnlyCollection<Type> LibraryImplementations => libraryImplementations;

    internal void RecordSuppliedPort(Type port) => suppliedPorts.Add(port);

    /// <summary>Appends an implementation to a multi-implementation service (providers, artifact renderers,
    /// watch sources), which resolve as <c>IEnumerable&lt;T&gt;</c>. Not TryAdd: each one is a distinct member.</summary>
    public ReeveWorkloadsBuilder AddEnumerable<TService, TImplementation>(ServiceLifetime lifetime)
        where TService : class
        where TImplementation : class, TService
    {
        Services.TryAddEnumerable(new ServiceDescriptor(typeof(TService), typeof(TImplementation), lifetime));
        return Track(typeof(TImplementation));
    }

    /// <summary>Points a port at a concrete type registered separately, so both resolve to ONE instance per
    /// scope. Register the concrete type with <see cref="AddSelf{TImplementation}"/> first.</summary>
    public ReeveWorkloadsBuilder Forward<TService, TImplementation>(ServiceLifetime lifetime)
        where TService : class
        where TImplementation : class, TService
    {
        Services.Add(new ServiceDescriptor(
            typeof(TService), sp => sp.GetRequiredService<TImplementation>(), lifetime));
        return Track(typeof(TImplementation));
    }

    /// <summary>Registers a concrete engine type as itself, for the cases a package resolves by concrete type.</summary>
    public ReeveWorkloadsBuilder AddSelf<TImplementation>(ServiceLifetime lifetime)
        where TImplementation : class
    {
        Services.Add(new ServiceDescriptor(typeof(TImplementation), typeof(TImplementation), lifetime));
        return Track(typeof(TImplementation));
    }

    private ReeveWorkloadsBuilder Track(Type implementation)
    {
        libraryAssemblies.Add(implementation.Assembly);
        libraryImplementations.Add(implementation);
        return this;
    }
}
