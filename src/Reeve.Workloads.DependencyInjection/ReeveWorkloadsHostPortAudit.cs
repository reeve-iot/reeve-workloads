using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Reeve.Workloads.DependencyInjection;

/// <summary>One engine port the host has not supplied: the interface, who needs it, and whether every site that
/// needs it would accept its absence.</summary>
/// <param name="Port">The engine interface no registration satisfies.</param>
/// <param name="RequiredBy">Engine types whose constructor asks for it, sorted, for the failure message.</param>
/// <param name="EveryRequestIsOptional">True when every asking parameter carries a default, so the container
/// resolves without it and the behaviour it drives silently degrades instead of failing.</param>
public sealed record MissingHostPort(Type Port, IReadOnlyList<Type> RequiredBy, bool EveryRequestIsOptional);

/// <summary>Derives the host's obligations from the registrations themselves rather than from a hand-kept list,
/// which is the only version that cannot fall out of date as the engine gains a port.</summary>
/// <remarks>It reads constructor parameters AND the <c>GetService&lt;T&gt;</c>/<c>GetRequiredService&lt;T&gt;</c>
/// calls in method bodies, because the engine's singletons reach scoped ports the second way and reading only the
/// first way under-reported exactly those. What is still invisible: a non-generic <c>GetService(Type)</c>, and a
/// type resolved through a variable rather than a literal generic argument. Everything it reports is real.
/// <para>Every method here is annotated <see cref="RequiresUnreferencedCodeAttribute"/> because the audit
/// reads constructors and IL that no call site references, and a trimmer keeps only what it can see referenced.
/// Trimmed, this does not throw — it reports FEWER missing ports, which is the same answer as a correctly
/// wired host. An unannotated audit would quietly become a no-op in a published trimmed app.</para></remarks>
public static class ReeveWorkloadsHostPortAudit
{
    private const string TrimWarning =
        "The host-port audit discovers ports by reflecting over constructors and method bodies that nothing "
        + "references directly. Trimming removes exactly that, and the audit then reports fewer missing ports "
        + "rather than failing — call it from a non-trimmed build (a test, or startup in a self-contained "
        + "deployment with trimming off).";

    /// <summary>Every engine interface an engine constructor asks for that nothing in the collection provides.</summary>
    [RequiresUnreferencedCode(TrimWarning)]
    public static IReadOnlyList<MissingHostPort> FindMissingHostPorts(this ReeveWorkloadsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var provided = builder.Services
            .Select(descriptor => descriptor.ServiceType)
            .ToHashSet();

        var wanted = new Dictionary<Type, (List<Type> RequiredBy, bool AllOptional)>();

        foreach (var implementation in EngineImplementations(builder))
        {
            var fromConstructor = EnginePortParameters(implementation, builder.LibraryAssemblies);
            var fromScope = ScopeResolvedPorts(implementation, builder.LibraryAssemblies);

            foreach (var (port, isOptional) in fromConstructor.Concat(fromScope))
            {
                if (provided.Contains(port))
                {
                    continue;
                }

                if (wanted.TryGetValue(port, out var entry))
                {
                    entry.RequiredBy.Add(implementation);
                    wanted[port] = (entry.RequiredBy, entry.AllOptional && isOptional);
                }
                else
                {
                    wanted[port] = ([implementation], isOptional);
                }
            }
        }

        return wanted
            .Select(kv => new MissingHostPort(
                kv.Key,
                kv.Value.RequiredBy.Distinct().OrderBy(t => t.FullName, StringComparer.Ordinal).ToList(),
                kv.Value.AllOptional))
            .OrderBy(missing => missing.Port.FullName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The ports still answered by the library's own default rather than by the host: the decisions the
    /// library made on the host's behalf. They come in two kinds. Most are an ABSENCE — no audit sink, no bus beyond
    /// this process, no device groups — a behaviour the host is running without. The rest are the library's own
    /// implementation of something every host needs — for example the schema migrator, the installation identity,
    /// the clock, and the single-replica election, which is right on one replica and only on one — and for those
    /// the default IS the behaviour, so a host that replaced one would be the odd one. Log the list at boot, at
    /// Information and in neutral words, and read it as "what the library decided", not as "what is missing": a
    /// default satisfies the missing-port audit, which is the point, so this list is the only thing that still
    /// reports either kind. The list cannot tell the kinds apart; a host that knows it runs several replicas can,
    /// and warns for the lock provider and the leader election alone.</summary>
    /// <remarks>Derived, not declared: a port counts only while the descriptor the container would actually
    /// resolve — the LAST one registered for it — is one this builder created. A host that registers its own on
    /// either side of <c>AddReeveWorkloads</c> drops off this list by that fact alone.
    /// Identity rather than the implementation type's assembly, because a FACTORY descriptor has none: reading
    /// the type dropped every factory-registered default off this list without saying so.</remarks>
    [RequiresUnreferencedCode(TrimWarning)]
    public static IReadOnlyList<Type> FindHostPortsOnLibraryDefaults(this ReeveWorkloadsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resolved = ResolvedDescriptors(builder);
        var library = builder.LibraryDescriptors.ToHashSet();

        return builder.DefaultedPorts
            .Where(port => resolved.TryGetValue(port, out var descriptor) && library.Contains(descriptor))
            .OrderBy(port => port.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<Type, ServiceDescriptor> ResolvedDescriptors(ReeveWorkloadsBuilder builder)
    {
        var resolved = new Dictionary<Type, ServiceDescriptor>();
        foreach (var descriptor in builder.Services)
        {
            resolved[descriptor.ServiceType] = descriptor;
        }

        return resolved;
    }

    /// <summary>Ports the host supplied through the <c>AddReeveWorkloads</c> callback that no engine type asks
    /// for — wiring the engine has stopped needing, and the one kind of mistake nothing else can see.</summary>
    /// <remarks>A missing port fails the boot. A SURPLUS one resolves perfectly and costs nothing but the claim
    /// it makes, so it survives every test and every review: the host still names a class, still constructs it,
    /// and nothing ever calls it. This is reported rather than thrown on, because a port can also go unasked-for
    /// simply because the provider package that wants it is not in this composition — which is a legitimate
    /// host, not a defect. The caller decides; the audit only makes it sayable.</remarks>
    [RequiresUnreferencedCode(TrimWarning)]
    public static IReadOnlyList<Type> FindSuppliedPortsTheEngineDoesNotAskFor(this ReeveWorkloadsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var wanted = new HashSet<Type>();
        foreach (var implementation in EngineImplementations(builder))
        {
            foreach (var (port, _) in EnginePortParameters(implementation, builder.LibraryAssemblies)
                         .Concat(ScopeResolvedPorts(implementation, builder.LibraryAssemblies)))
            {
                wanted.Add(port);
            }

            wanted.UnionWith(CollectionPortParameters(implementation, builder.LibraryAssemblies));
        }

        return builder.SuppliedPorts
            .Where(port => !wanted.Contains(port))
            .OrderBy(port => port.FullName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Fails the boot naming every port the host owes, instead of letting the container resolve a
    /// half-wired engine whose missing pieces surface later as behaviour that quietly does nothing.</summary>
    /// <param name="builder">The builder to audit.</param>
    /// <param name="includeOptional">Also fail for a port every asking site would accept as absent. Defaults to
    /// true: an optional port left unsupplied is precisely the silent degradation this audit exists to stop.</param>
    [RequiresUnreferencedCode(TrimWarning)]
    public static ReeveWorkloadsBuilder ValidateHostPorts(this ReeveWorkloadsBuilder builder, bool includeOptional = true)
    {
        var missing = builder.FindMissingHostPorts()
            .Where(m => includeOptional || !m.EveryRequestIsOptional)
            .ToList();

        if (missing.Count == 0)
        {
            return builder;
        }

        var lines = missing.Select(m =>
            $"  {m.Port.FullName}{(m.EveryRequestIsOptional ? " (optional at every call site — absence degrades silently)" : string.Empty)}"
            + $"\n      required by: {string.Join(", ", m.RequiredBy.Select(t => t.Name))}");

        throw new InvalidOperationException(
            $"The Reeve workload engine is missing {missing.Count} host port(s). Register an implementation for "
            + $"each before building the provider:\n{string.Join('\n', lines)}");
    }

    private static IEnumerable<Type> EngineImplementations(ReeveWorkloadsBuilder builder)
        => builder.Services
            .Select(descriptor => descriptor.ImplementationType)
            .Where(type => type is not null && builder.LibraryAssemblies.Contains(type.Assembly))
            .Select(type => type!)
            .Concat(builder.LibraryImplementations)
            .Distinct();

    private static IEnumerable<(Type Port, bool IsOptional)> EnginePortParameters(
        Type implementation, IReadOnlyCollection<Assembly> libraryAssemblies)
    {
        var constructor = implementation
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (constructor is null)
        {
            yield break;
        }

        foreach (var parameter in constructor.GetParameters())
        {
            var candidate = UnwrapCollection(parameter.ParameterType);
            if (candidate is not null && IsAuditablePort(candidate, libraryAssemblies))
            {
                if (!ReferenceEquals(candidate, parameter.ParameterType))
                {
                    continue;
                }

                yield return (candidate, parameter.HasDefaultValue);
            }
        }
    }

    private static IEnumerable<Type> CollectionPortParameters(
        Type implementation, IReadOnlyCollection<Assembly> libraryAssemblies)
    {
        var constructor = implementation
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        return constructor is null
            ? []
            : constructor.GetParameters()
                .Select(parameter => (Element: UnwrapCollection(parameter.ParameterType), parameter.ParameterType))
                .Where(pair => !ReferenceEquals(pair.Element, pair.ParameterType)
                               && IsAuditablePort(pair.Element, libraryAssemblies))
                .Select(pair => pair.Element);
    }

    private static IEnumerable<(Type Port, bool IsOptional)> ScopeResolvedPorts(
        Type implementation, IReadOnlyCollection<Assembly> libraryAssemblies)
    {
        foreach (var type in WithNestedTypes(implementation))
        {
            MethodBase[] methods;
            try
            {
                methods = type
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                        | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Cast<MethodBase>()
                    .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    .ToArray();
            }
            catch (Exception exception) when (exception is TypeLoadException or FileNotFoundException)
            {
                continue;
            }

            foreach (var method in methods)
            {
                foreach (var resolved in ResolutionsIn(method, libraryAssemblies))
                {
                    yield return resolved;
                }
            }
        }
    }

    private static IEnumerable<Type> WithNestedTypes(Type type)
    {
        yield return type;

        Type[] nested;
        try
        {
            nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch (Exception exception) when (exception is TypeLoadException or FileNotFoundException)
        {
            yield break;
        }

        foreach (var child in nested)
        {
            foreach (var descendant in WithNestedTypes(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<(Type Port, bool IsOptional)> ResolutionsIn(
        MethodBase method, IReadOnlyCollection<Assembly> libraryAssemblies)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
                                              or BadImageFormatException)
        {
            yield break;
        }

        if (il is null)
        {
            yield break;
        }

        var typeArguments = SafeGenericArguments(method.DeclaringType);
        var methodArguments = method.IsGenericMethodDefinition ? SafeGenericArguments(method) : null;

        foreach (var token in CallTokens(il))
        {
            MethodBase? called;
            try
            {
                called = method.Module.ResolveMethod(token, typeArguments, methodArguments);
            }
            catch (Exception exception) when (exception is ArgumentException or BadImageFormatException
                                                  or MissingMethodException or TypeLoadException
                                                  or FileNotFoundException)
            {
                continue;
            }

            if (called is null || !called.IsGenericMethod || called.DeclaringType != typeof(ServiceProviderServiceExtensions))
            {
                continue;
            }

            var optional = called.Name == nameof(ServiceProviderServiceExtensions.GetService);
            if (!optional && called.Name != nameof(ServiceProviderServiceExtensions.GetRequiredService))
            {
                continue;
            }

            var arguments = called.GetGenericArguments();
            if (arguments.Length == 1
                && arguments[0] is { IsGenericParameter: false } port
                && IsAuditablePort(port, libraryAssemblies))
            {
                yield return (port, optional);
            }
        }
    }

    private static bool IsAuditablePort(Type candidate, IReadOnlyCollection<Assembly> libraryAssemblies)
        => candidate.IsInterface
           && (libraryAssemblies.Contains(candidate.Assembly) || !IsContainerFurniture(candidate));

    private static bool IsContainerFurniture(Type candidate)
    {
        var assembly = candidate.Assembly.GetName().Name;
        return assembly is null
               || assembly.StartsWith("System.", StringComparison.Ordinal)
               || assembly.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal);
    }

    private static Type[]? SafeGenericArguments(MemberInfo? member)
    {
        try
        {
            return member switch
            {
                Type type when type.IsGenericType => type.GetGenericArguments(),
                MethodBase { IsGenericMethodDefinition: true } m => m.GetGenericArguments(),
                _ => null,
            };
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static IEnumerable<int> CallTokens(byte[] il)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            var code = il[offset++];
            OpCode opCode;
            if (code == 0xFE)
            {
                if (offset >= il.Length || !TwoByteOpCodes.TryGetValue(il[offset++], out opCode))
                {
                    yield break;
                }
            }
            else if (!SingleByteOpCodes.TryGetValue(code, out opCode))
            {
                yield break;
            }

            if (opCode.OperandType is OperandType.InlineMethod
                && (opCode.Value == OpCodes.Call.Value || opCode.Value == OpCodes.Callvirt.Value)
                && offset + 4 <= il.Length)
            {
                yield return BitConverter.ToInt32(il, offset);
            }

            var width = OperandWidth(opCode, il, offset);
            if (width < 0 || offset + width > il.Length)
            {
                yield break;
            }

            offset += width;
        }
    }

    private static int OperandWidth(OpCode opCode, byte[] il, int offset)
        => opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch when offset + 4 <= il.Length => 4 + (4 * BitConverter.ToInt32(il, offset)),
            _ => -1,
        };

    private static readonly Dictionary<byte, OpCode> SingleByteOpCodes = BuildOpCodeTable(twoByte: false);

    private static readonly Dictionary<byte, OpCode> TwoByteOpCodes = BuildOpCodeTable(twoByte: true);

    private static Dictionary<byte, OpCode> BuildOpCodeTable(bool twoByte)
    {
        var table = new Dictionary<byte, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            var value = (ushort)opCode.Value;
            if (twoByte == (value > 0xFF))
            {
                table[(byte)(value & 0xFF)] = opCode;
            }
        }

        return table;
    }

    private static Type UnwrapCollection(Type type)
    {
        if (!type.IsGenericType || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return type;
        }

        var arguments = type.GetGenericArguments();
        return arguments.Length == 1 ? arguments[0] : type;
    }
}
