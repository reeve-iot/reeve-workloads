using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Providers;

namespace Reeve.Workloads.DependencyInjection;

/// <summary>The entry point a host calls once. Every <c>Reeve.Workloads.*</c> package then contributes its own
/// services through an extension on the returned builder, so no host names an engine class.</summary>
public static class ReeveWorkloadsServiceCollectionExtensions
{
    /// <summary>Opens the engine's registration surface over <paramref name="services"/>, answers the host ports
    /// this package can answer itself, and gives the rest a place to be written.</summary>
    /// <param name="services">The collection the engine and the host's ports share.</param>
    /// <param name="configuration">Bound by the package extensions for their own options sections.</param>
    /// <returns>The builder each package's extension method extends.</returns>
    /// <remarks>The defaults are decisions a host SHOULD
    /// be able to decline — a deployment with no audit log, no cluster, no private registry — and making someone
    /// hand-write "I decline" ten times is not a decision, it is a toll. Every one is overridable, and
    /// <c>FindHostPortsOnLibraryDefaults</c> names the ones still standing on a default so declining stays
    /// visible rather than silent.</remarks>
    public static ReeveWorkloadsBuilder AddReeveWorkloads(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return WithLibraryDefaults(new ReeveWorkloadsBuilder(services, configuration));
    }

    /// <summary>The same, plus the one place the host answers the ports the library cannot answer for it.</summary>
    /// <param name="services">The collection the engine and the host's ports share.</param>
    /// <param name="configuration">Bound by the package extensions for their own options sections.</param>
    /// <param name="hostPorts">Where the host answers the engine's ports. Runs AFTER the defaults, so anything
    /// it supplies wins. Ports do not HAVE to be registered here — the collection takes them anywhere — but a
    /// host that scatters them through whatever file composes its container ends up unable to see, from any one
    /// place, which registrations the library demands and which are its own plumbing. That is the problem this
    /// overload exists for.</param>
    /// <returns>The builder each package's extension method extends.</returns>
    /// <remarks>A separate overload rather than an optional parameter on the one above: adding a parameter, even
    /// an optional one, changes the method's signature, so every assembly already compiled against the old two-
    /// argument form would fail to bind it at runtime. Source compatibility is not the same thing as binary
    /// compatibility, and a published package owes both.</remarks>
    public static ReeveWorkloadsBuilder AddReeveWorkloads(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<ReeveWorkloadsHostPorts> hostPorts)
    {
        ArgumentNullException.ThrowIfNull(hostPorts);

        var builder = services.AddReeveWorkloads(configuration);
        hostPorts(new ReeveWorkloadsHostPorts(builder));
        return builder;
    }

    private static ReeveWorkloadsBuilder WithLibraryDefaults(ReeveWorkloadsBuilder builder)
        => builder
            .AddDefault<IWorkloadDiagnosticsAnnouncer, NoWorkloadDiagnosticsAnnouncer>(ServiceLifetime.Singleton)
            .AddDefault<IWorkloadDiagnosticsSinkFactory, NoWorkloadDiagnosticsSinkFactory>(ServiceLifetime.Singleton)
            .AddDefault<IWorkloadActorAccessor, NoWorkloadActorAccessor>(ServiceLifetime.Scoped)
            .AddDefault<IWorkloadAuditSink, NoWorkloadAuditSink>(ServiceLifetime.Scoped)
            .AddDefault<IWorkloadCredentialPort, NoCredentialPort>(ServiceLifetime.Scoped)
            .AddDefault<IWorkloadDeviceGroupSource, NoWorkloadDeviceGroupSource>(ServiceLifetime.Scoped)
            .AddDefault<IWorkloadImageRegistryCredentialResolver, NoWorkloadImageRegistryCredentialResolver>(
                ServiceLifetime.Scoped)
            .AddDefault<IWorkloadClusterBus, InProcessWorkloadClusterBus>(ServiceLifetime.Singleton)
            .AddDefault<IWorkloadLockProvider, InProcessWorkloadLockProvider>(ServiceLifetime.Singleton);
}
