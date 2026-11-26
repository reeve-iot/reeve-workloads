using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Reeve.Workloads.EntityFrameworkCore.Persistence;

/// <summary>Names the EF Core provider a database-conventions package serves, and the type that applies them.
/// The workloads model finds the attribute on its own: referencing the package is the whole of a host's wiring.</summary>
/// <remarks>The same shape as EF's own <c>DesignTimeProviderServicesAttribute</c>: the provider is matched by the
/// name it reports as <c>Database.ProviderName</c>, so this package never names one.</remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class WorkloadsDatabaseConventionsAttribute : Attribute
{
    public WorkloadsDatabaseConventionsAttribute(string providerName, Type conventionsType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(conventionsType);
        ProviderName = providerName;
        ConventionsType = conventionsType;
    }

    /// <summary>The provider's <c>Database.ProviderName</c>, compared ordinally.</summary>
    public string ProviderName { get; }

    /// <summary>A public <see cref="IWorkloadsDatabaseConventions"/> with a parameterless constructor.</summary>
    public Type ConventionsType { get; }
}

/// <summary>What one database adds to the provider-agnostic workloads model: column types, index filters, and
/// anything else its dialect spells its own way. <see cref="WorkloadsModelContract"/> says what needs spelling.</summary>
public interface IWorkloadsDatabaseConventions
{
    /// <summary>Runs last in <c>OnModelCreating</c>, over the finished agnostic configuration.</summary>
    void Apply(ModelBuilder modelBuilder);
}

internal static class WorkloadsDatabaseConventions
{
    internal const string PackagePrefix = "Reeve.Workloads.EntityFrameworkCore.";

    private static readonly Lazy<IReadOnlyList<WorkloadsDatabaseConventionsAttribute>> Discovered =
        new(() => Collect(CandidateAssemblies()), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<string, string?> MigrationsAssemblies = new(StringComparer.Ordinal);

    internal static IReadOnlyList<IWorkloadsDatabaseConventions> For(string? providerName) =>
        For(providerName, Discovered.Value);

    internal static IReadOnlyList<IWorkloadsDatabaseConventions> For(
        string? providerName,
        IEnumerable<WorkloadsDatabaseConventionsAttribute> registered)
    {
        return registered
            .Where(attribute => string.Equals(attribute.ProviderName, providerName, StringComparison.Ordinal))
            .OrderBy(attribute => attribute.ConventionsType.AssemblyQualifiedName, StringComparer.Ordinal)
            .Select(attribute => Create(attribute.ConventionsType))
            .ToList();
    }

    internal static string? AssemblyFor(string? providerName) =>
        providerName is null
            ? null
            : MigrationsAssemblies.GetOrAdd(providerName, static name => AssemblyFor(name, Discovered.Value));

    internal static string? AssemblyFor(
        string? providerName,
        IEnumerable<WorkloadsDatabaseConventionsAttribute> registered)
    {
        var assemblies = registered
            .Where(attribute => string.Equals(attribute.ProviderName, providerName, StringComparison.Ordinal))
            .Select(attribute => attribute.ConventionsType.Assembly.GetName().Name)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (assemblies.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{providerName}' is served by more than one workloads conventions package "
                + $"({string.Join(", ", assemblies)}), so there is no single migrations assembly to point Entity "
                + "Framework at. Reference exactly one of them.");
        }

        return assemblies.SingleOrDefault();
    }

    internal static IReadOnlyList<WorkloadsDatabaseConventionsAttribute> Collect(IEnumerable<Assembly> assemblies) =>
        assemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetCustomAttributes<WorkloadsDatabaseConventionsAttribute>())
            .ToList();

    private static IWorkloadsDatabaseConventions Create(Type conventionsType)
    {
        if (!typeof(IWorkloadsDatabaseConventions).IsAssignableFrom(conventionsType)
            || conventionsType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException(
                $"'{conventionsType.FullName}' is registered as workloads database conventions but is not a "
                + $"{nameof(IWorkloadsDatabaseConventions)} with a public parameterless constructor.");
        }

        return (IWorkloadsDatabaseConventions)Activator.CreateInstance(conventionsType)!;
    }

    private static IEnumerable<Assembly> CandidateAssemblies()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name is { } name && name.StartsWith(PackagePrefix, StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.StartsWith(PackagePrefix, StringComparison.Ordinal))
                {
                    names.Add(name);
                }
            }
        }

        foreach (var name in names)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.Load(new AssemblyName(name));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                continue;
            }

            yield return assembly;
        }
    }
}
