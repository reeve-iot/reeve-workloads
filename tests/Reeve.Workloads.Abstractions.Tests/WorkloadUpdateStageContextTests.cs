using System.Reflection;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Update;
using Xunit;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class WorkloadUpdateStageContextTests
{
    private static readonly string[] ForbiddenWords =
    [
        "Module", "Plugin", "Assembly", "Replica", "Loaded", "Swap",
    ];

    public static TheoryData<Type> ContextTypes =>
        new(typeof(WorkloadUpdateStageContext), typeof(WorkloadUpdateStageWorkload));

    [Theory]
    [MemberData(nameof(ContextTypes))]
    public void The_stage_context_names_nothing_that_is_loaded(Type type)
    {
        var offenders = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => ForbiddenWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        offenders.Should().BeEmpty(
            "a stage is told about a group of workloads moving between two declared versions, and a member "
            + "named for a module, a plugin, an assembly, a replica or a loaded version would put back the "
            + "assumption the stage context is built to avoid");
    }

    [Theory]
    [MemberData(nameof(ContextTypes))]
    public void The_stage_context_names_nothing_that_is_loaded_in_its_property_types(Type type)
    {
        var offenders = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new { p.Name, TypeName = Describe(p.PropertyType) })
            .Where(p => ForbiddenWords.Any(word => p.TypeName.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        offenders.Should().BeEmpty(
            "a clean property name over a type called LoadedModuleVersion would carry the assumption back in "
            + "through the type system instead of through the name");
    }

    [Fact]
    public void The_stage_context_publishes_these_members_and_no_others()
    {
        var published = typeof(WorkloadUpdateStageContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => name != "EqualityContract")
            .OrderBy(name => name, StringComparer.Ordinal);

        published.Should().Equal(
            "Cohort", "FromVersion", "GroupId", "GroupInstanceId", "IntentId", "SourceKey", "ToVersion", "Workloads");
    }

    [Fact]
    public void The_carrier_the_core_stages_read_is_not_published()
    {
        var carrier = typeof(WorkloadUpdateStageContext)
            .GetProperty("Internals", BindingFlags.NonPublic | BindingFlags.Instance);

        carrier.Should().NotBeNull("the core stages read the intent and the plan off it");
        carrier!.GetMethod!.IsPublic.Should().BeFalse(
            "a host stage resolves what it loads from GroupId and SourceKey; handing it the intent entity and "
            + "the plan would give back exactly the knowledge the stage context keeps away");
    }

    private static string Describe(Type type) =>
        type.IsGenericType
            ? type.Name + string.Concat(type.GetGenericArguments().Select(Describe))
            : type.Name;
}
