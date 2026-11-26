using FluentAssertions;
using Reeve.Workloads.ApiSurface;

namespace Reeve.Workloads.ApiSurface.Tests;

public sealed class GateTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("api-surface-fixture").FullName;
    private readonly StringWriter stderr = new();
    private readonly TextWriter realStderr = Console.Error;
    private readonly TextWriter realStdout = Console.Out;

    public GateTests()
    {
        Console.SetError(stderr);
        Console.SetOut(new StringWriter());
        Write("contracts/api-surface.txt", "# committed baseline", "Alpha.Thing");
        Project("Alpha", packable: true);
        Project("Beta", packable: true);
    }

    public void Dispose()
    {
        Console.SetError(realStderr);
        Console.SetOut(realStdout);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void A_partial_build_is_refused_and_names_what_is_missing()
    {
        Build("Alpha");

        Program.Run(["check", "--root", root]).Should().Be(1);
        stderr.ToString().Should().Contain("1 of 2 packable projects").And.Contain("Beta.dll");
    }

    [Fact]
    public void Emit_does_not_rewrite_the_baseline_from_a_partial_build()
    {
        Build("Alpha");

        Program.Run(["emit", "--root", root]).Should().Be(1);
        Baseline().Should().Equal("# committed baseline", "Alpha.Thing");
    }

    [Fact]
    public void Emit_does_not_rewrite_the_baseline_from_a_stale_build()
    {
        Build("Alpha");
        Build("Beta");
        Touch("src/Alpha/Alpha.cs", DateTime.UtcNow.AddMinutes(5));

        Program.Run(["emit", "--root", root]).Should().Be(1);
        stderr.ToString().Should().Contain("stale build");
        Baseline().Should().Equal("# committed baseline", "Alpha.Thing");
    }

    [Fact]
    public void A_source_dated_in_the_future_is_not_answered_with_rebuild()
    {
        Build("Alpha");
        Build("Beta");
        Touch("src/Alpha/Alpha.cs", DateTime.UtcNow.AddDays(1));

        Program.Run(["check", "--root", root]).Should().Be(1);
        stderr.ToString().Should().Contain("FUTURE").And.NotContain("Rebuild first");
    }

    [Fact]
    public void A_shared_msbuild_file_touched_after_the_build_makes_every_project_stale()
    {
        Build("Alpha");
        Build("Beta");
        Touch("Directory.Build.props", DateTime.UtcNow.AddMinutes(5));

        Program.Run(["check", "--root", root]).Should().Be(1);
        stderr.ToString().Should().Contain("Alpha.dll").And.Contain("Beta.dll")
            .And.Contain("Directory.Build.props");
    }

    [Fact]
    public void An_unbuilt_tree_is_told_to_build_rather_than_graded()
    {
        Program.Run(["check", "--root", root]).Should().Be(1);
        stderr.ToString().Should().Contain("no built assemblies");
    }

    [Fact]
    public void An_unknown_mode_gets_the_usage_text_even_when_the_tree_is_stale()
    {
        Build("Alpha");
        Build("Beta");
        Touch("src/Alpha/Alpha.cs", DateTime.UtcNow.AddMinutes(5));

        Program.Run(["bogus", "--root", root]).Should().Be(2);
        stderr.ToString().Should().Contain("usage:").And.NotContain("stale build");
    }

    [Theory]
    [InlineData(new string[0], "Release")]
    [InlineData(new[] { "check" }, "Release")]
    [InlineData(new[] { "check", "--configuration", "Debug" }, "Debug")]
    public void The_default_configuration_is_the_one_packages_are_published_from(string[] args, string expected)
        => Program.Configuration(args).Should().Be(expected);

    private void Project(string name, bool packable)
    {
        Write($"src/{name}/{name}.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            packable ? "  <PropertyGroup><IsPackable>true</IsPackable></PropertyGroup>" : "",
            "</Project>");
        Write($"src/{name}/{name}.cs", $"public sealed class {name} {{ }}");
        Write("Directory.Build.props", "<Project />");
    }

    private void Build(string name)
    {
        Write($"src/{name}/bin/Release/{Program.TargetFramework}/{name}.dll", string.Empty);
        Touch($"src/{name}/bin/Release/{Program.TargetFramework}/{name}.dll", DateTime.UtcNow.AddMinutes(1));
    }

    private void Touch(string relative, DateTime written)
        => File.SetLastWriteTimeUtc(Path.Combine(root, relative), written);

    private string[] Baseline() => File.ReadAllLines(Path.Combine(root, "contracts/api-surface.txt"));

    private void Write(string relative, params string[] lines)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
