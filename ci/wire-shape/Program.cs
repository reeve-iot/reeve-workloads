using System.Text.Json;
using Reeve.Workloads.WireShape;

const string DefaultShapePath = "contracts/wire-shape.json";
const string ContractRoot = "src/Reeve.Workloads.Abstractions/Contracts";

var argv = args.ToList();
var verb = argv.FirstOrDefault() ?? "";
if (verb is not ("emit" or "check" or "parity"))
{
    Console.Error.WriteLine("usage: WireShape <emit|check|parity> [options]");
    return 2;
}

string Opt(string name, string fallback)
{
    var i = argv.IndexOf(name);
    return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : fallback;
}

var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
if (repoRoot is null)
{
    Console.Error.WriteLine("could not locate the repository root (no .git above the working directory)");
    return 2;
}

if (verb == "parity") return Consumer.Run(argv, Opt);

var shapePath = Path.Combine(repoRoot, Opt("--shape", DefaultShapePath));
var contractDir = Path.Combine(repoRoot, ContractRoot);
if (!Directory.Exists(contractDir))
{
    Console.Error.WriteLine($"contract root not found: {contractDir}");
    return 2;
}

var files = Directory.GetFiles(contractDir, "*.cs", SearchOption.AllDirectories)
    .OrderBy(f => f, StringComparer.Ordinal)
    .ToList();

List<WireType> types;
try
{
    types = ShapeExtractor.ForFiles(
        files.Select(f => (f, Path.GetRelativePath(repoRoot, f).Replace('\\', '/'))).ToList());
}
catch (ShapeExtractor.UnreadableShapeException ex)
{
    Console.Error.WriteLine($"wire-shape: {ex.Problems.Count} contract member(s) do not declare a wire key.");
    foreach (var problem in ex.Problems) Console.Error.WriteLine("  " + problem);
    return 1;
}

types.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
var doc = new ShapeDocument
{
    Version = 1,
    Source = ContractRoot,
    Types = types,
};
var json = JsonSerializer.Serialize(doc, ShapeJson.Options) + "\n";

if (verb == "emit")
{
    Directory.CreateDirectory(Path.GetDirectoryName(shapePath)!);
    File.WriteAllText(shapePath, json);
    Console.WriteLine($"wrote {Path.GetRelativePath(repoRoot, shapePath)}: " +
                      $"{types.Count} types from {files.Count} contract sources");
    return 0;
}

if (!File.Exists(shapePath))
{
    Console.Error.WriteLine($"wire-shape: {Path.GetRelativePath(repoRoot, shapePath)} does not exist. " +
                            "Run `dotnet run --project ci/wire-shape -- emit` and commit it.");
    return 1;
}

var committed = File.ReadAllText(shapePath);
if (committed == json)
{
    Console.WriteLine($"wire-shape OK: {types.Count} types across {files.Count} contract sources match " +
                      Path.GetRelativePath(repoRoot, shapePath));
    return 0;
}

Console.Error.WriteLine("wire-shape DRIFT: the contract sources no longer match the committed shape.");
Console.Error.WriteLine("This is a change to what consumers parse. Review the diff below, then run");
Console.Error.WriteLine("  dotnet run --project ci/wire-shape -- emit");
Console.Error.WriteLine("and commit the result IN THE SAME pull request as the source change.");
Console.Error.WriteLine();
foreach (var line in ShapeDiff.Describe(committed, json)) Console.Error.WriteLine("  " + line);
return 1;

static string? FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
            File.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}
