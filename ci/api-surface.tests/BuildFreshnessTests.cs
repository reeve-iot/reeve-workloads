using FluentAssertions;
using Reeve.Workloads.ApiSurface;

namespace Reeve.Workloads.ApiSurface.Tests;

public sealed class BuildFreshnessTests
{
    private static readonly DateTime Built = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = Built.AddYears(1);

    [Fact]
    public void A_source_touched_after_the_build_makes_the_assembly_stale()
    {
        var verdict = BuildFreshness.Grade("X.dll", Built, [new SourceFile("Foo.cs", Built.AddSeconds(1))], Now);

        verdict.Should().NotBeNull();
        verdict!.Assembly.Should().Be("X.dll");
        verdict.Source.Should().Be("Foo.cs");
        verdict.SourceWritten.Should().Be(Built.AddSeconds(1));
        verdict.AssemblyWritten.Should().Be(Built);
        verdict.SourceIsInTheFuture.Should().BeFalse();
    }

    [Fact]
    public void A_source_older_than_the_build_is_what_a_current_build_looks_like()
    {
        BuildFreshness.Grade("X.dll", Built, [new SourceFile("Foo.cs", Built.AddSeconds(-1))], Now)
            .Should().BeNull();
    }

    [Fact]
    public void A_source_written_in_the_same_instant_as_the_build_passes()
    {
        BuildFreshness.Grade("X.dll", Built, [new SourceFile("Foo.cs", Built)], Now).Should().BeNull();
    }

    [Fact]
    public void A_source_dated_after_now_is_flagged_as_such()
    {
        var verdict = BuildFreshness.Grade(
            "X.dll", Built, [new SourceFile("Foo.cs", Now.AddDays(1))], Now);

        verdict!.SourceIsInTheFuture.Should().BeTrue();
    }

    [Fact]
    public void A_source_written_in_this_very_instant_is_not_in_the_future()
    {
        var verdict = BuildFreshness.Grade("X.dll", Built, [new SourceFile("Foo.cs", Now)], Now);

        verdict!.SourceIsInTheFuture.Should().BeFalse();
    }

    [Theory]
    [InlineData("obj/Debug/net11.0/X.AssemblyInfo.cs")]
    [InlineData("bin/Release/net11.0/Generated.cs")]
    [InlineData(@"obj\Debug\net11.0\X.AssemblyInfo.cs")]
    public void Build_products_are_not_inputs(string path)
    {
        BuildFreshness.Grade("X.dll", Built, [new SourceFile(path, Built.AddHours(1))], Now).Should().BeNull();
    }

    [Theory]
    [InlineData("objects/Foo.cs")]
    [InlineData("binding/Foo.cs")]
    public void A_directory_that_merely_starts_with_obj_or_bin_is_a_real_input(string path)
    {
        BuildFreshness.Grade("X.dll", Built, [new SourceFile(path, Built.AddSeconds(1))], Now)
            .Should().NotBeNull(because: "only a whole path segment names the compiler's own output");
    }

    [Fact]
    public void The_newest_input_is_the_one_reported()
    {
        var verdict = BuildFreshness.Grade("X.dll", Built, [
            new SourceFile("Old.cs", Built.AddSeconds(1)),
            new SourceFile("Newest.cs", Built.AddSeconds(30)),
            new SourceFile("Middle.cs", Built.AddSeconds(10)),
        ], Now);

        verdict!.Source.Should().Be("Newest.cs");
        verdict.SourceWritten.Should().Be(Built.AddSeconds(30));
    }

    [Fact]
    public void Two_inputs_written_in_the_same_instant_resolve_to_the_same_one_every_time()
    {
        var files = new[]
        {
            new SourceFile("Alpha.cs", Built.AddSeconds(1)),
            new SourceFile("Zulu.cs", Built.AddSeconds(1)),
        };

        BuildFreshness.Grade("X.dll", Built, files, Now)!.Source.Should().Be("Zulu.cs");
        BuildFreshness.Grade("X.dll", Built, files.Reverse(), Now)!.Source.Should().Be("Zulu.cs");
    }

    [Fact]
    public void A_project_with_nothing_but_build_products_is_not_graded_stale()
    {
        BuildFreshness.Grade("X.dll", Built, [new SourceFile("obj/X.cs", Built.AddHours(1))], Now)
            .Should().BeNull();
    }

    [Fact]
    public void No_inputs_at_all_is_not_stale()
    {
        BuildFreshness.Grade("X.dll", Built, [], Now).Should().BeNull();
    }

    [Theory]
    [InlineData("obj/X.cs", true)]
    [InlineData("src/obj/X.cs", true)]
    [InlineData("bin/X.cs", true)]
    [InlineData("OBJ/X.cs", true)]
    [InlineData("objects/X.cs", false)]
    [InlineData("X.cs", false)]
    public void Build_products_are_matched_by_whole_segment(string path, bool isProduct)
        => BuildFreshness.IsBuildProduct(path).Should().Be(isProduct);

    [Theory]
    [InlineData("Foo.cs", true)]
    [InlineData("Foo.csproj", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("Directory.Build.targets", true)]
    [InlineData("README.md", false)]
    [InlineData("appsettings.json", false)]
    [InlineData("Strings.resx", false)]
    [InlineData("obj/Foo.cs", false)]
    public void Only_what_can_move_a_public_signature_counts_as_an_input(string path, bool isInput)
        => BuildFreshness.IsCompileInput(path).Should().Be(isInput);
}
