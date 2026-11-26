using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class RetainedVolumeSchemeColumnTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private const string Schema = "workloads";

    private const string SchemeColumnSql =
        "SELECT volume_naming_scheme AS \"Value\" FROM \"" + Schema + "\".retained_volumes WHERE id = {0}";

    private readonly PostgresFixture postgres;

    public RetainedVolumeSchemeColumnTests(PostgresFixture postgres) => this.postgres = postgres;

    [SkippableTheory]
    [InlineData("docker", "Docker")]
    [InlineData("kubernetes", "Kubernetes")]
    [InlineData("acme.nomad", "acme.nomad")]
    public async Task The_column_holds_the_6_x_member_name_for_the_schemes_6_x_knew(string scheme, string stored)
    {
        var store = await postgres.CreateStoreAsync();
        var row = RetainedVolume.Record(
            Guid.NewGuid(), "zwave", "zwave-js", "state", "some-volume", "1Gi",
            VolumeCriticality.Critical, description: null, StandInProviders.DockerId,
            RetainedVolumeSchemeId.Parse(scheme),
            sourceGroupInstanceId: Guid.NewGuid(), sourceWorkloadInstanceId: Guid.NewGuid(), Now);

        await using (var writer = store.NewContext())
        {
            writer.RetainedVolumes.Add(row);
            await writer.SaveChangesAsync();
        }

        await using var reader = store.NewContext();

        reader.Model.GetDefaultSchema().Should().Be(
            Schema, "the raw query below names the schema, and would otherwise read the wrong table");

        var raw = await reader.Database.SqlQueryRaw<string>(SchemeColumnSql, row.Id).SingleAsync();

        raw.Should().Be(stored);
        (await reader.RetainedVolumes.SingleAsync(r => r.Id == row.Id)).VolumeNamingScheme
            .Should().Be(RetainedVolumeSchemeId.Parse(scheme));
    }

    [SkippableFact]
    public async Task The_column_default_is_still_the_6_x_spelling_so_the_hosts_model_snapshot_does_not_move()
    {
        var store = await postgres.CreateStoreAsync();
        await using var context = store.NewContext();
        var property = context.Model.FindEntityType(typeof(RetainedVolume))!
            .FindProperty(nameof(RetainedVolume.VolumeNamingScheme))!;

        property.GetTypeMapping().Converter!.ConvertToProvider(property.GetDefaultValue())
            .Should().Be("Kubernetes");
    }
}
