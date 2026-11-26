using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Abstractions.Tests;

public sealed class AotCompatibleAnnotationParityTests
{
    private static readonly JsonSerializerOptions ReflectionDefault = new();
    private static readonly JsonSerializerOptions ReflectionWeb = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ReflectionNonGenericEnumConverter = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static TheoryData<Type> StringEnums()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(WorkloadOrchestratorSettings).Assembly.GetTypes()
                     .Where(t => t.IsEnum && t.IsPublic)
                     .Where(t => t.GetCustomAttributes(typeof(JsonConverterAttribute), false)
                         .OfType<JsonConverterAttribute>()
                         .Any(a => a.ConverterType is { IsGenericType: true } c
                             && c.GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>)))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            data.Add(type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StringEnums))]
    public void A_generic_enum_converter_writes_and_reads_what_the_reflection_converter_did(Type enumType)
    {
        foreach (var value in Enum.GetValues(enumType))
        {
            var generic = JsonSerializer.Serialize(value, enumType);
            var reflection = JsonSerializer.Serialize(value, enumType, ReflectionNonGenericEnumConverter);

            generic.Should().Be(reflection, $"{enumType.Name}.{value} must keep its wire name");
            JsonSerializer.Deserialize(reflection, enumType).Should().Be(value);
            JsonSerializer.Deserialize(generic, enumType, ReflectionNonGenericEnumConverter).Should().Be(value);
        }
    }

    public static TheoryData<string[]> PasswordNameLists() => new()
    {
        Array.Empty<string>(),
        new[] { "password" },
        new[] { "a\"b", "<script>", "c+d&e", "zażółć", "😀", "tab\there", "back\\slash" },
    };

    [Theory]
    [MemberData(nameof(PasswordNameLists))]
    public void Password_name_lists_serialize_to_the_reflection_bytes(string[] names)
    {
        IReadOnlyList<string> asList = names.ToList();

        JsonSerializer.Serialize(asList, WorkloadEntityJsonContext.Default.IReadOnlyListString)
            .Should().Be(JsonSerializer.Serialize(asList, ReflectionDefault));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""["password","api_key"]""")]
    [InlineData("""["żółw","\"quoted\""]""")]
    [InlineData("null")]
    public void Password_name_lists_read_what_the_reflection_reader_read(string json)
    {
        JsonSerializer.Deserialize(json, WorkloadEntityJsonContext.Default.StringArray)
            .Should().BeEquivalentTo(JsonSerializer.Deserialize<string[]>(json, ReflectionDefault),
                options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData("""{"binding":"secret/db","isVersionPinned":true}""")]
    [InlineData("""{"Binding":"secret/db","IsVersionPinned":false}""")]
    [InlineData("""{"BINDING":"x","isversionpinned":true,"extra":42}""")]
    [InlineData("""{"isVersionPinned":true}""")]
    [InlineData("""{}""")]
    public void A_hash_pin_marker_reads_what_the_reflection_reader_read(string json)
        => JsonSerializer.Deserialize(json, WorkloadEntityWebJsonContext.Default.ReferenceHashPin)
            .Should().Be(JsonSerializer.Deserialize<ReferenceHashPin>(json, ReflectionWeb));

    [Theory]
    [InlineData("""{"binding":"x","isVersionPinned":"true"}""")]
    [InlineData("""{"binding":7,"isVersionPinned":true}""")]
    [InlineData("""[]""")]
    public void A_malformed_hash_pin_marker_is_refused_as_the_reflection_reader_refused_it(string json)
    {
        var sourceGenerated = () => JsonSerializer.Deserialize(json, WorkloadEntityWebJsonContext.Default.ReferenceHashPin);
        var reflection = () => JsonSerializer.Deserialize<ReferenceHashPin>(json, ReflectionWeb);

        reflection.Should().Throw<JsonException>();
        sourceGenerated.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_provider_entry_written_through_type_info_matches_the_reflection_entry()
    {
        var provider = WorkloadProviderId.Parse("nomad");
        var settings = new ParitySettings
        {
            Address = "https://nomad.example:4646",
            Region = "eu-<1>",
            Datacenters = ["dc1", "dc2"],
            Mode = RegistryAuthType.Basic,
        };
        var existing = new WorkloadOrchestratorSettings().WithProvider(
            WorkloadProviderId.Parse("docker"), new ParitySettings(), ParityJsonContext.Default.ParitySettings);

        var typed = existing.WithProvider(provider, settings, ParityJsonContext.Default.ParitySettings);
        var reflected = existing.WithProvider(provider, settings);

        typed.Providers[provider].GetRawText().Should().Be(reflected.Providers[provider].GetRawText());
        typed.Providers.Keys.Should().BeEquivalentTo(reflected.Providers.Keys);
        JsonSerializer.Serialize(typed).Should().Be(JsonSerializer.Serialize(reflected));
    }

    [Fact]
    public void A_provider_entry_without_type_info_is_refused()
    {
        var act = () => new WorkloadOrchestratorSettings().WithProvider(
            WorkloadProviderId.Parse("nomad"), new ParitySettings(), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("500m")]
    [InlineData("12345678901234567890123456789012")]
    [InlineData("123456789012345678901234567890123")]
    public void A_resource_quantity_is_bounded_where_the_max_length_attribute_bounded_it(string? quantity)
    {
        var expected = new MaxLengthAttribute(32).IsValid(quantity);

        foreach (var request in new[]
                 {
                     new SetWorkloadResourcesRequest { RequestsCpu = quantity },
                     new SetWorkloadResourcesRequest { RequestsMemory = quantity },
                     new SetWorkloadResourcesRequest { LimitsCpu = quantity },
                     new SetWorkloadResourcesRequest { LimitsMemory = quantity },
                 })
        {
            Validator.TryValidateObject(request, new ValidationContext(request), null, validateAllProperties: true)
                .Should().Be(expected);
        }
    }

    [Theory]
    [InlineData("10Gi")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public void A_volume_size_is_bounded_where_the_max_length_attribute_bounded_it(string size)
    {
        var request = new SetWorkloadVolumeSizeRequest { Size = size };

        Validator.TryValidateObject(request, new ValidationContext(request), null, validateAllProperties: true)
            .Should().Be(new MaxLengthAttribute(64).IsValid(size));
    }

    internal sealed record ParitySettings
    {
        [JsonPropertyName("address")]
        public string? Address { get; init; }

        public string? Region { get; init; }

        public IReadOnlyList<string> Datacenters { get; init; } = [];

        public RegistryAuthType Mode { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Token { get; init; }
    }
}

[JsonSerializable(typeof(AotCompatibleAnnotationParityTests.ParitySettings))]
internal sealed partial class ParityJsonContext : JsonSerializerContext;
