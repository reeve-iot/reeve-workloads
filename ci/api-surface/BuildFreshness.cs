namespace Reeve.Workloads.ApiSurface;

public sealed record SourceFile(string Path, DateTime Written);

public sealed record StaleAssembly(
    string Assembly,
    DateTime AssemblyWritten,
    string Source,
    DateTime SourceWritten,
    bool SourceIsInTheFuture);

public static class BuildFreshness
{
    public static bool IsCompileInput(string path) =>
        !IsBuildProduct(path)
        && (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase));

    public static bool IsBuildProduct(string path) =>
        path.Split('/', '\\').Any(segment =>
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("bin", StringComparison.OrdinalIgnoreCase));

    public static StaleAssembly? Grade(
        string assembly, DateTime assemblyWritten, IEnumerable<SourceFile> files, DateTime now)
    {
        var newest = files
            .Where(file => IsCompileInput(file.Path))
            .OrderBy(file => file.Written)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .LastOrDefault();

        return newest is not null && newest.Written > assemblyWritten
            ? new StaleAssembly(assembly, assemblyWritten, newest.Path, newest.Written, newest.Written > now)
            : null;
    }
}
