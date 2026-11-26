using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Reeve.Workloads.DependencyInjection;

/// <summary>The one place a host answers the engine's ports. Handed to the callback on
/// <c>AddReeveWorkloads</c>, so the obligations the library imposes are written together, under one name,
/// instead of being spread through whatever file the host happens to compose its container in.</summary>
/// <remarks>Everything here lands in the same <see cref="IServiceCollection"/> a plain <c>AddScoped</c> would
/// reach, and nothing stops a host registering a port elsewhere — this is a place to put them, not a lock on
/// the door. What it buys is that the set becomes NAMEABLE: the ports supplied through it are recorded, so
/// <c>FindSuppliedPortsTheEngineDoesNotAskFor</c> can point at wiring the engine stopped needing, which is
/// otherwise invisible — dead registrations resolve perfectly and cost nothing but the lie they tell.</remarks>
public sealed class ReeveWorkloadsHostPorts
{
    private readonly ReeveWorkloadsBuilder builder;

    internal ReeveWorkloadsHostPorts(ReeveWorkloadsBuilder builder) => this.builder = builder;

    /// <summary>The collection, for the host services a port implementation of its own depends on. Registering a
    /// PORT here works too and is recorded by nothing, which is the whole difference from <c>Use</c>.</summary>
    public IServiceCollection Services => builder.Services;

    /// <summary>Answers <typeparamref name="TPort"/> with <typeparamref name="TImplementation"/>.</summary>
    public ReeveWorkloadsHostPorts Use<TPort, TImplementation>(ServiceLifetime lifetime)
        where TPort : class
        where TImplementation : class, TPort
    {
        builder.Services.Add(new ServiceDescriptor(typeof(TPort), typeof(TImplementation), lifetime));
        return Supplied(typeof(TPort));
    }

    /// <summary>Answers <typeparamref name="TPort"/> from a factory, for a port whose implementation is chosen at
    /// resolve time — one of two behind a setting, or a decorator over something else.</summary>
    public ReeveWorkloadsHostPorts Use<TPort>(Func<IServiceProvider, TPort> factory, ServiceLifetime lifetime)
        where TPort : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        builder.Services.Add(new ServiceDescriptor(typeof(TPort), factory, lifetime));
        return Supplied(typeof(TPort));
    }

    /// <summary>Answers <typeparamref name="TPort"/> only if nothing already has — for a port whose answer here
    /// is the host's own fallback, meant to lose to a layer that registered ahead of it.</summary>
    /// <remarks>Not the same decision as <see cref="Use{TPort,TImplementation}"/> with the calls reordered. A
    /// composition root that is itself called by an outer layer — a web host wiring its realtime notifiers before
    /// it calls the infrastructure's registration method — relies on exactly this to let the outer layer win,
    /// and it wins by registering EARLIER, which a plain Add would silently overturn.</remarks>
    public ReeveWorkloadsHostPorts TryUse<TPort, TImplementation>(ServiceLifetime lifetime)
        where TPort : class
        where TImplementation : class, TPort
    {
        builder.Services.TryAdd(new ServiceDescriptor(typeof(TPort), typeof(TImplementation), lifetime));
        return Supplied(typeof(TPort));
    }

    /// <summary>Points <typeparamref name="TPort"/> at a concrete type the host registers separately, so the port
    /// and the concrete resolve to ONE instance rather than to two that each think they are the only one.</summary>
    public ReeveWorkloadsHostPorts UseExisting<TPort, TConcrete>(ServiceLifetime lifetime)
        where TPort : class
        where TConcrete : class, TPort
        => Use<TPort>(sp => sp.GetRequiredService<TConcrete>(), lifetime);

    private ReeveWorkloadsHostPorts Supplied(Type port)
    {
        builder.RecordSuppliedPort(port);
        return this;
    }
}
