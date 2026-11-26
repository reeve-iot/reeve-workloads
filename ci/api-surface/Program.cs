using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reeve.Workloads.ApiSurface;

internal static class Program
{
    private const string SurfaceFile = "contracts/api-surface.txt";

    private static int Main(string[] args) => Run(args);

    internal static int Run(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "check";
        if (mode is not ("emit" or "check"))
        {
            Console.Error.WriteLine("usage: api-surface [emit|check] [--configuration <name>]   (default: Release)");
            return 2;
        }

        var configuration = Configuration(args);
        var root = Argument(args, "--root") ?? RepositoryRoot();

        var projects = PackableProjects(root, configuration).ToList();
        var packable = projects.Where(project => File.Exists(project.Assembly)).ToList();
        if (packable.Count == 0)
        {
            Console.Error.WriteLine(
                $"FAIL: no built assemblies under src/*/bin/{configuration}/. Build the solution first.");
            return 1;
        }

        if (packable.Count < projects.Count)
        {
            Console.Error.WriteLine($"FAIL: {projects.Count - packable.Count} of {projects.Count} packable "
                + "projects have no built assembly, so this would grade a partial tree and call every member "
                + "of the missing packages a removal.");
            foreach (var project in projects.Except(packable))
            {
                Console.Error.WriteLine($"  missing {Path.GetRelativePath(root, project.Assembly)}");
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine($"Build them all: dotnet build Reeve.Workloads.slnx -c {configuration}");
            return 1;
        }

        List<StaleAssembly> stale;
        try
        {
            stale = StaleAssemblies(root, packable).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"FAIL: could not read the sources to check them against the build: "
                + $"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }

        if (stale.Count > 0)
        {
            Console.Error.WriteLine("FAIL: stale build. These assemblies no longer describe their sources, so");
            Console.Error.WriteLine("whatever this graded would be a verdict on a tree that no longer exists.");
            foreach (var assembly in stale)
            {
                Console.Error.WriteLine(
                    $"  {Path.GetRelativePath(root, assembly.Assembly)} built {assembly.AssemblyWritten:u}");
                Console.Error.WriteLine(
                    $"    is older than {assembly.Source}, written {assembly.SourceWritten:u}");
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine(stale.Any(assembly => assembly.SourceIsInTheFuture)
                ? "One of those is dated in the FUTURE, so rebuilding will not settle it — the assembly would "
                    + "still be older. Fix the file's timestamp (touch it) or the clock that wrote it."
                : $"Rebuild first: dotnet build Reeve.Workloads.slnx -c {configuration}");
            return 1;
        }

        var assemblies = packable.Select(project => project.Assembly).ToList();
        var surface = Extract(assemblies);
        var path = Path.Combine(root, SurfaceFile);

        if (mode == "emit")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, Header(assemblies.Count, surface.Count).Concat(surface));
            Console.WriteLine($"api-surface: wrote {surface.Count} members across {assemblies.Count} packages to {SurfaceFile}");
            return 0;
        }

        if (mode != "check")
        {
            Console.Error.WriteLine("usage: api-surface [emit|check] [--configuration <name>]   (default: Release)");
            return 2;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"FAIL: {SurfaceFile} does not exist. Run `emit` and commit it.");
            return 1;
        }

        var committed = File.ReadAllLines(path)
            .Where(line => !line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
            .ToList();
        var removed = committed.Except(surface, StringComparer.Ordinal).ToList();
        var added = surface.Except(committed, StringComparer.Ordinal).ToList();

        if (removed.Count == 0 && added.Count == 0)
        {
            Console.WriteLine($"api-surface OK: {surface.Count} public members across {assemblies.Count} packages match {SurfaceFile}");
            return 0;
        }

        foreach (var line in removed)
        {
            Console.WriteLine($"  BREAKING (gone or changed): {line}");
        }

        foreach (var line in added)
        {
            Console.WriteLine($"  added:                      {line}");
        }

        Console.WriteLine();
        Console.WriteLine($"FAIL: the committed surface and the assemblies disagree — {removed.Count} removed or "
            + $"changed, {added.Count} added.");
        Console.WriteLine("If the change is intended, run `emit` and commit the diff; that diff IS the review of");
        Console.WriteLine("what consumers have to absorb. A BREAKING line needs a major version.");
        return 1;
    }

    private static IEnumerable<string> Header(int packages, int members) =>
    [
        "# The public .NET surface of every package this repository publishes. GENERATED — do not hand-edit.",
        "#",
        "#   dotnet run --project ci/api-surface -- emit",
        "#",
        "# A line that DISAPPEARS from this file is a source break for every consumer. A line that appears is",
        "# additive. Nothing runs the check for you: it grades the tree when you run it.",
        "#",
        "# NOT listed: types EF's scaffolder writes — anything deriving from Migration or ModelSnapshot, and any",
        "# IDesignTimeDbContextFactory. They are public because a TOOL requires it, are named by that tool, and",
        "# are found by reflection, so squashing or renaming one is not a source break. Matched by base type and",
        "# not by namespace, so a hand-written type never falls through. See IsScaffolded in ci/api-surface.",
        $"# {packages} packages, {members} public members.",
        "",
    ];

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Reeve.Workloads.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    private sealed record PackableProject(string Project, string Assembly);

    private static readonly string[] SharedInputs =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    private static IEnumerable<StaleAssembly> StaleAssemblies(string root, IEnumerable<PackableProject> projects)
    {
        var now = DateTime.UtcNow;
        var shared = SharedInputs
            .Select(name => Path.Combine(root, name))
            .Where(File.Exists)
            .Select(file => new SourceFile(Path.GetRelativePath(root, file), File.GetLastWriteTimeUtc(file)))
            .ToList();

        foreach (var project in projects)
        {
            var directory = Path.GetDirectoryName(project.Project)!;
            var files = Directory
                .EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(file => BuildFreshness.IsCompileInput(Path.GetRelativePath(directory, file)))
                .Select(file => new SourceFile(Path.GetRelativePath(directory, file), File.GetLastWriteTimeUtc(file)))
                .Concat(shared);

            var stale = BuildFreshness.Grade(
                project.Assembly, File.GetLastWriteTimeUtc(project.Assembly), files, now);
            if (stale is not null)
            {
                yield return stale;
            }
        }
    }

    internal static string Configuration(string[] args) => Argument(args, "--configuration") ?? "Release";

    internal static string TargetFramework { get; } = typeof(Program).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "LibraryTargetFramework").Value!;

    private static IEnumerable<PackableProject> PackableProjects(string root, string configuration)
    {
        foreach (var project in Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (!File.ReadAllText(project).Contains("<IsPackable>true</IsPackable>", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(project);
            var dll = Path.Combine(Path.GetDirectoryName(project)!, "bin", configuration, TargetFramework, $"{name}.dll");
            yield return new PackableProject(project, dll);
        }
    }

    private static List<string> Extract(IReadOnlyList<string> assemblies)
    {
        var byName = new Dictionary<string, PortableExecutableReference>(StringComparer.OrdinalIgnoreCase);
        var candidates = assemblies
            .Concat(assemblies
                .Select(Path.GetDirectoryName)
                .Append(Path.GetDirectoryName(typeof(object).Assembly.Location))
                .Where(directory => directory is not null)
                .Distinct(StringComparer.Ordinal)
                .SelectMany(directory => Directory.GetFiles(directory!, "*.dll").Order(StringComparer.Ordinal)));

        foreach (var path in candidates)
        {
            var name = Path.GetFileName(path);
            if (!byName.ContainsKey(name))
            {
                byName[name] = MetadataReference.CreateFromFile(path);
            }
        }

        var references = byName.Values.Cast<MetadataReference>().ToList();

        var compilation = CSharpCompilation.Create("api-surface-probe", references: references);
        var lines = new List<string>();

        foreach (var path in assemblies)
        {
            var reference = byName[Path.GetFileName(path)];
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            {
                throw new InvalidOperationException($"could not read {path} as an assembly");
            }

            foreach (var type in PublicTypes(assembly.GlobalNamespace).Where(type => !IsScaffolded(type)))
            {
                lines.Add($"{assembly.Name}|{TypeLine(type)}");
                foreach (var member in type.GetMembers().Where(Visible).OrderBy(m => m.MetadataName, StringComparer.Ordinal))
                {
                    lines.Add($"{assembly.Name}|{TypeLine(type)}|{Display(member)}");
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static bool IsScaffolded(INamedTypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.ContainingNamespace?.ToDisplayString() != "Microsoft.EntityFrameworkCore.Migrations")
            {
                continue;
            }

            if (baseType.MetadataName is "Migration" or "ModelSnapshot")
            {
                return true;
            }
        }

        return type.AllInterfaces.Any(contract =>
            contract.OriginalDefinition.MetadataName == "IDesignTimeDbContextFactory`1"
            && contract.ContainingNamespace?.ToDisplayString() == "Microsoft.EntityFrameworkCore.Design");
    }

    private static IEnumerable<INamedTypeSymbol> PublicTypes(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol nested)
            {
                foreach (var type in PublicTypes(nested))
                {
                    yield return type;
                }
            }
            else if (member is INamedTypeSymbol type && Visible(type))
            {
                yield return type;

                foreach (var inner in PublicTypes(type))
                {
                    yield return inner;
                }
            }
        }
    }

    private static string TypeLine(INamedTypeSymbol type)
    {
        var modifiers = string.Concat(
            type.DeclaredAccessibility == Accessibility.Public ? "public " : "protected ",
            type.IsStatic ? "static " : string.Empty,
            type.IsAbstract && type.TypeKind == TypeKind.Class ? "abstract " : string.Empty,
            type.IsSealed && type.TypeKind == TypeKind.Class ? "sealed " : string.Empty,
            type.IsRecord ? "record " : string.Empty,
            type.IsReadOnly && type.TypeKind == TypeKind.Struct ? "readonly " : string.Empty);
        return modifiers + Display(type);
    }

    private static bool Visible(ISymbol symbol)
        => symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
           && !symbol.IsImplicitlyDeclared
           && symbol is not INamespaceSymbol
           && symbol is not IMethodSymbol
           {
               MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet
                   or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise,
           }
           && !symbol.MetadataName.StartsWith('<');

    private static readonly SymbolDisplayFormat Format = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeAccessibility
            | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeOptionalBrackets,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword | SymbolDisplayKindOptions.IncludeMemberKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static string Display(ISymbol symbol) => symbol.ToDisplayString(Format);
}
