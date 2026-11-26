using FluentAssertions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Core.Tests;

public sealed class RetainedVolumeServiceTests
{
    private const string GroupId = "zwave";
    private const string WorkloadId = "zwave-js";
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordRetained_writes_one_row_per_volume_with_the_correct_metadata()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        var instance = DeployedInstance();

        var written = await svc.RecordRetainedAsync(instance, new[]
        {
            Volume("state", "reeve-wl-zwave-zwave-js-state-abc12345", "1Gi", VolumeCriticality.Critical, "bonds"),
            Volume("cache", "reeve-wl-zwave-zwave-js-cache-abc12345", "256Mi", VolumeCriticality.Recoverable, null),
        }, Now);

        written.Should().Be(2);
        var all = await svc.ListAsync();
        all.Should().HaveCount(2);

        var state = all.Single(r => r.VolumeId == "state");
        state.GroupId.Should().Be(GroupId);
        state.WorkloadId.Should().Be(WorkloadId);
        state.ResourceName.Should().Be("reeve-wl-zwave-zwave-js-state-abc12345");
        state.Size.Should().Be("1Gi");
        state.Criticality.Should().Be(VolumeCriticality.Critical);
        state.Description.Should().Be("bonds");
        state.Provider.Should().Be(KubernetesProvider.Id);
        state.SourceGroupInstanceId.Should().Be(instance.GroupInstanceId);
        state.SourceWorkloadInstanceId.Should().Be(instance.Id);
        state.RetainedAtUtc.Should().Be(Now);
        state.IsActive.Should().BeTrue("a freshly-recorded volume is active (neither reattached nor purged)");
    }

    [Fact]
    public async Task An_instance_never_bound_to_a_provider_has_its_volumes_refused_not_guessed()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        var unbound = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, Now);

        var record = () => svc.RecordRetainedAsync(
            unbound, [Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null)], Now);

        await record.Should().ThrowAsync<InvalidOperationException>().WithMessage("*never bound*");
        (await svc.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unbound_instance_with_nothing_to_retain_records_nothing_without_complaint()
    {
        var svc = new RetainedVolumeService(new InMemoryRetainedVolumeRepository(), new NullRetainedVolumeDeleter());
        var unbound = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, Now);

        (await svc.RecordRetainedAsync(unbound, [], Now)).Should().Be(0);
    }

    [Fact]
    public async Task RecordRetained_is_idempotent_per_source_and_volume()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        var instance = DeployedInstance();
        var volumes = new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) };

        var first = await svc.RecordRetainedAsync(instance, volumes, Now);
        var second = await svc.RecordRetainedAsync(instance, volumes, Now.AddMinutes(5));

        first.Should().Be(1);
        second.Should().Be(0, "the row already exists for (sourceWorkloadInstanceId, volumeId) — no duplicate");
        (await svc.ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task RecordRetained_ABSORBS_a_would_be_duplicate_active_resource_name_instead_of_livelocking()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        const string sharedName = "reeve-wl-zwave-zwave-js-state-orig1234";

        var original = InstanceWith(Guid.NewGuid());
        await svc.RecordRetainedAsync(original, new[] { Volume("state", sharedName, "1Gi", VolumeCriticality.Critical, "bonds") }, Now);

        var restored = InstanceWith(Guid.NewGuid());
        var written = await svc.RecordRetainedAsync(
            restored, new[] { Volume("state", sharedName, "1Gi", VolumeCriticality.Critical, "bonds") }, Now.AddMinutes(5));

        written.Should().Be(0, "the active row already binds the physical name — the record is absorbed, not duplicated");
        var active = await svc.ListActiveAsync();
        active.Should().ContainSingle("exactly one active row binds the physical volume — the durable pointer is intact")
            .Which.ResourceName.Should().Be(sharedName);
    }

    [Fact]
    public async Task RecordRetained_re_records_a_FRESH_active_row_after_a_reattach_of_the_same_source_and_volume()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        var instance = InstanceWith(Guid.NewGuid());

        var firstWrite = await svc.RecordRetainedAsync(
            instance, new[] { Volume("state", "reeve-wl-zwave-zwave-js-state-regrant01", "1Gi", VolumeCriticality.Critical, "bonds") }, Now);
        firstWrite.Should().Be(1);

        var reattachedRow = (await svc.ListActiveAsync()).Single();
        await svc.MarkReattachedAsync(reattachedRow.Id, Now.AddMinutes(1));
        (await svc.ListActiveAsync()).Should().BeEmpty("the reattached row left the active set");

        var secondWrite = await svc.RecordRetainedAsync(
            instance, new[] { Volume("state", "reeve-wl-zwave-zwave-js-state-regrant01", "1Gi", VolumeCriticality.Critical, "bonds") }, Now.AddMinutes(2));

        secondWrite.Should().Be(1, "a re-record after a genuine reattach INSERTS a fresh active row (no active row blocks it)");
        var active = await svc.ListActiveAsync();
        active.Should().ContainSingle("the surviving PVC is visible again via a fresh active row — never invisible data loss")
            .Which.SourceWorkloadInstanceId.Should().Be(instance.Id);
        (await svc.ListAsync()).Should().HaveCount(2, "the reattached row is retained as the audit trail; the fresh row is added");
        (await svc.ListActiveByPluginTypeAsync(GroupId)).Should().ContainSingle();
    }

    [Fact]
    public async Task RecordRetained_with_no_volumes_records_nothing()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());

        var written = await svc.RecordRetainedAsync(DeployedInstance(), Array.Empty<RetainedVolumeInput>(), Now);

        written.Should().Be(0);
        (await svc.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ListOlderThan_filters_by_age_relative_to_the_clock()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        var asOf = Now;
        await svc.RecordRetainedAsync(InstanceWith(Guid.NewGuid()), new[] { Volume("old", "pvc-old", "1Gi", VolumeCriticality.Critical, null) }, asOf.AddDays(-100));
        await svc.RecordRetainedAsync(InstanceWith(Guid.NewGuid()), new[] { Volume("new", "pvc-new", "1Gi", VolumeCriticality.Critical, null) }, asOf.AddDays(-10));

        var stale = await svc.ListOlderThanAsync(TimeSpan.FromDays(90), asOf);

        stale.Should().ContainSingle("only the 100-day-old record is older than 90 days");
        stale.Single().VolumeId.Should().Be("old");
    }

    [Fact]
    public async Task MarkReattached_flips_the_timestamp_and_excludes_the_row_from_active()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var id = (await svc.ListAsync()).Single().Id;

        await svc.MarkReattachedAsync(id, Now.AddDays(1));

        var record = (await svc.ListAsync()).Single();
        record.ReattachedAtUtc.Should().Be(Now.AddDays(1));
        record.IsActive.Should().BeFalse();
        (await svc.ListActiveAsync()).Should().BeEmpty("a reattached record leaves the active view");
    }

    [Fact]
    public async Task MarkReattachedForInstance_reattaches_all_active_rows_of_that_instance_and_excludes_them_from_the_90_day_scan()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        var regranted = InstanceWith(Guid.NewGuid());
        var other = InstanceWith(Guid.NewGuid());
        await svc.RecordRetainedAsync(regranted, new[]
        {
            Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null),
            Volume("cache", "pvc-cache", "256Mi", VolumeCriticality.Recoverable, null),
        }, Now.AddDays(-100));
        await svc.RecordRetainedAsync(other, new[] { Volume("state", "pvc-other", "1Gi", VolumeCriticality.Critical, null) }, Now.AddDays(-100));

        var reattached = await svc.MarkReattachedForInstanceAsync(regranted.Id, Now);

        reattached.Should().Be(2, "both of the re-granted instance's active rows are marked reattached");
        var active = await svc.ListActiveAsync();
        active.Should().OnlyContain(r => r.SourceWorkloadInstanceId == other.Id,
            "the re-granted instance's rows left the active set; the OTHER instance's row is untouched");
        var scan = await svc.ListOlderThanAsync(TimeSpan.FromDays(90), Now);
        scan.Should().OnlyContain(r => r.SourceWorkloadInstanceId == other.Id,
            "the reattached now-live volumes are excluded from the 90-day informational scan");

        (await svc.MarkReattachedForInstanceAsync(regranted.Id, Now.AddDays(1))).Should().Be(0);
    }

    [Fact]
    public async Task MarkPurgedForInstance_purges_all_active_rows_of_that_instance_and_excludes_them_from_the_90_day_scan()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        var purgedInstance = InstanceWith(Guid.NewGuid());
        var other = InstanceWith(Guid.NewGuid());
        await svc.RecordRetainedAsync(purgedInstance, new[]
        {
            Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null),
            Volume("cache", "pvc-cache", "256Mi", VolumeCriticality.Recoverable, null),
        }, Now.AddDays(-100));
        await svc.RecordRetainedAsync(other, new[] { Volume("state", "pvc-other", "1Gi", VolumeCriticality.Critical, null) }, Now.AddDays(-100));

        var purged = await svc.MarkPurgedForInstanceAsync(purgedInstance.Id, Now);

        purged.Should().Be(2, "both of the PurgeAll-uninstalled instance's active rows are marked purged");
        var active = await svc.ListActiveAsync();
        active.Should().OnlyContain(r => r.SourceWorkloadInstanceId == other.Id,
            "the PurgeAll-uninstalled instance's rows left the active set; the OTHER instance's row is untouched");
        var scan = await svc.ListOlderThanAsync(TimeSpan.FromDays(90), Now);
        scan.Should().OnlyContain(r => r.SourceWorkloadInstanceId == other.Id,
            "the purged nonexistent volumes are excluded from the 90-day informational scan");
        (await svc.ListAsync()).Should().HaveCount(3, "the records are NEVER deleted — they remain as the audit trail");

        (await svc.MarkPurgedForInstanceAsync(purgedInstance.Id, Now.AddDays(1))).Should().Be(0);
    }

    [Fact]
    public async Task Purge_flips_the_timestamp_excludes_from_active_and_never_deletes_the_row()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var id = (await svc.ListAsync()).Single().Id;

        await svc.PurgeAsync(id, Now.AddDays(2));

        var record = (await svc.ListAsync()).Single();
        record.PurgedAtUtc.Should().Be(Now.AddDays(2), "purge stamps the timestamp");
        record.IsActive.Should().BeFalse();
        (await svc.ListActiveAsync()).Should().BeEmpty("a purged record leaves the active view");
        (await svc.ListAsync()).Should().ContainSingle("the record is NEVER deleted — it is the audit trail");
    }

    [Fact]
    public async Task Purge_REFUSES_a_row_whose_physical_volume_is_claimed_by_another_restore()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var row = (await svc.ListActiveAsync()).Single();
        (await svc.TryClaimForRestoreAsync(row.Id, Guid.NewGuid())).Should().BeTrue("another instance claims it for a restore");

        var act = async () => await svc.PurgeAsync(row.Id, Now.AddDays(1));

        await act.Should().ThrowAsync<RetainedVolumeClaimedException>(
            "a physical volume an in-flight restore is adopting must not be destructively purged");
        (await svc.ListActiveAsync()).Should().ContainSingle("the claimed row is NOT purged — it stays active for the restore");
    }

    [Fact]
    public async Task MarkPurgedForInstance_SKIPS_a_row_claimed_by_another_restore()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        var sourceInstance = InstanceWith(Guid.NewGuid());
        await svc.RecordRetainedAsync(sourceInstance, new[]
        {
            Volume("state", "pvc-shared", "1Gi", VolumeCriticality.Critical, null),
            Volume("cache", "pvc-free", "256Mi", VolumeCriticality.Recoverable, null),
        }, Now);
        var sharedRow = (await svc.ListActiveAsync()).Single(r => r.ResourceName == "pvc-shared");
        (await svc.TryClaimForRestoreAsync(sharedRow.Id, Guid.NewGuid())).Should().BeTrue();

        var purged = await svc.MarkPurgedForInstanceAsync(sourceInstance.Id, Now.AddDays(1));

        purged.Should().Be(1, "only the UNclaimed 'pvc-free' row of the source instance is purged; 'pvc-shared' is skipped (claimed elsewhere)");
        var stillActive = await svc.ListActiveAsync();
        stillActive.Should().Contain(r => r.SourceWorkloadInstanceId == sourceInstance.Id && r.ResourceName == "pvc-shared",
            "the source instance's row sharing the claimed physical name stays active — its physical volume must not be destroyed");
    }


    [Fact]
    public async Task TryPurge_PHYSICALLY_frees_the_volume_then_marks_the_row_purged_and_returns_Purged()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var deleter = new RecordingRetainedVolumeDeleter();
        var svc = new RetainedVolumeService(repo, deleter);
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var id = (await svc.ListActiveAsync()).Single().Id;

        var outcome = await svc.TryPurgeAsync(id, Now.AddDays(2));

        outcome.Kind.Should().Be(RetainedVolumePurgeOutcome.Purged);
        outcome.PurgedAtUtc.Should().Be(Now.AddDays(2));
        deleter.DeletedResourceNames.Should().ContainSingle().Which.Should().Be("pvc-state",
            "the physical volume is actually freed, not just the row marked");
        (await svc.ListActiveAsync()).Should().BeEmpty("the purged row leaves the active view");
        (await svc.ListAsync()).Should().ContainSingle("the audit row is never deleted");
    }

    [Fact]
    public async Task TryPurge_returns_NotFound_for_an_unknown_or_already_purged_row_and_does_NOT_delete()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var deleter = new RecordingRetainedVolumeDeleter();
        var svc = new RetainedVolumeService(repo, deleter);
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var id = (await svc.ListActiveAsync()).Single().Id;
        await svc.TryPurgeAsync(id, Now.AddDays(1));
        deleter.DeletedResourceNames.Clear();

        var second = await svc.TryPurgeAsync(id, Now.AddDays(2));
        var unknown = await svc.TryPurgeAsync(Guid.NewGuid(), Now.AddDays(2));

        second.Kind.Should().Be(RetainedVolumePurgeOutcome.NotFound);
        unknown.Kind.Should().Be(RetainedVolumePurgeOutcome.NotFound);
        deleter.DeletedResourceNames.Should().BeEmpty("nothing is physically deleted for a not-found/already-purged row");
    }

    [Fact]
    public async Task TryPurge_returns_Claimed_and_does_NOT_delete_when_the_volume_is_claimed_by_another_restore()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var deleter = new RecordingRetainedVolumeDeleter();
        var svc = new RetainedVolumeService(repo, deleter);
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var row = (await svc.ListActiveAsync()).Single();
        (await svc.TryClaimForRestoreAsync(row.Id, Guid.NewGuid())).Should().BeTrue();

        var outcome = await svc.TryPurgeAsync(row.Id, Now.AddDays(1));

        outcome.Kind.Should().Be(RetainedVolumePurgeOutcome.Claimed);
        deleter.DeletedResourceNames.Should().BeEmpty("a claimed volume is never physically deleted");
        (await svc.ListActiveAsync()).Should().ContainSingle("the claimed row stays active for the restore");
    }

    [Fact]
    public async Task TryPurge_does_NOT_mark_the_row_purged_when_the_physical_delete_fails()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var deleter = new RecordingRetainedVolumeDeleter { Result = false };
        var svc = new RetainedVolumeService(repo, deleter);
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var id = (await svc.ListActiveAsync()).Single().Id;

        var outcome = await svc.TryPurgeAsync(id, Now.AddDays(1));

        outcome.Kind.Should().Be(RetainedVolumePurgeOutcome.DeleteFailed);
        (await svc.ListActiveAsync()).Should().ContainSingle("the row stays active when the physical volume could not be freed");
    }

    [Fact]
    public async Task TryPurge_releases_its_reservation_when_the_physical_delete_fails_so_the_row_stays_offerable()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var deleter = new RecordingRetainedVolumeDeleter { Result = false };
        var svc = new RetainedVolumeService(repo, deleter);
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var id = (await svc.ListActiveAsync()).Single().Id;

        (await svc.TryPurgeAsync(id, Now.AddDays(1))).Kind.Should().Be(RetainedVolumePurgeOutcome.DeleteFailed);

        (await svc.TryClaimForRestoreAsync(id, Guid.NewGuid())).Should()
            .BeTrue("the purge reservation must be released on delete failure so the row stays offerable/claimable");
    }

    [Fact]
    public async Task TryPurge_refuses_a_row_already_claimed_by_a_restore_without_a_read_then_act_window()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var deleter = new RecordingRetainedVolumeDeleter();
        var svc = new RetainedVolumeService(repo, deleter);
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var row = (await svc.ListActiveAsync()).Single();
        (await svc.TryClaimForRestoreAsync(row.Id, Guid.NewGuid())).Should().BeTrue("a restore reserves the row first");

        var outcome = await svc.TryPurgeAsync(row.Id, Now.AddDays(1));

        outcome.Kind.Should().Be(RetainedVolumePurgeOutcome.Claimed,
            "the purge cannot win the reserve race against an in-flight restore — refuse atomically");
        deleter.DeletedResourceNames.Should().BeEmpty("a reserved-by-restore row is never physically deleted");
        (await svc.ListActiveAsync()).Should().ContainSingle("the row stays active for the restore");
    }

    [Fact]
    public async Task TryPurge_with_the_default_null_deleter_FAILS_CLOSED_and_keeps_the_row_active()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var id = (await svc.ListActiveAsync()).Single().Id;

        var outcome = await svc.TryPurgeAsync(id, Now.AddDays(1));

        outcome.Kind.Should().Be(RetainedVolumePurgeOutcome.DeleteFailed,
            "a null deleter frees nothing — it must fail closed, not falsely report the row purged");
        (await svc.ListActiveAsync()).Should().ContainSingle("the row stays active when no physical backend could free it");
    }

    [Fact]
    public async Task NullRetainedVolumeDeleter_reports_not_freed_fail_closed()
    {
        var freed = await new NullRetainedVolumeDeleter().TryDeletePhysicalVolumeAsync(
            RetainedVolume.Record(
                Guid.NewGuid(), GroupId, WorkloadId, "state", "pvc-state", "1Gi",
                VolumeCriticality.Critical, null, KubernetesProvider.Id,
                RetainedVolumeSchemeId.Kubernetes, Guid.NewGuid(), Guid.NewGuid(), Now));

        freed.Should().BeFalse();
    }

    [Fact]
    public async Task TryPurge_refuses_and_does_NOT_delete_when_the_source_instance_still_EXISTS()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var deleter = new RecordingRetainedVolumeDeleter();
        var svc = new RetainedVolumeService(repo, deleter);
        var source = DeployedInstance();
        await svc.RecordRetainedAsync(source, new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var row = (await repo.ListAllAsync()).Single();
        repo.ExistingSourceInstanceIds.Add(source.Id);

        var outcome = await svc.TryPurgeAsync(row.Id, Now.AddDays(1));

        outcome.Kind.Should().Be(RetainedVolumePurgeOutcome.Claimed,
            "a row whose source instance still exists must not be purged");
        deleter.DeletedResourceNames.Should().BeEmpty("a live/resurrectable source's volume is never physically deleted");
        (await repo.ListAllAsync()).Single().IsActive.Should().BeTrue("the row stays active for the live source");
    }

    [Fact]
    public async Task ListActive_panel_EXCLUDES_rows_whose_source_instance_still_EXISTS()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        var liveSource = DeployedInstance();
        var orphanSource = DeployedInstance();
        await svc.RecordRetainedAsync(liveSource, new[] { Volume("live", "pvc-live", "1Gi", VolumeCriticality.Critical, null) }, Now);
        await svc.RecordRetainedAsync(orphanSource, new[] { Volume("orphan", "pvc-orphan", "1Gi", VolumeCriticality.Critical, null) }, Now);
        repo.ExistingSourceInstanceIds.Add(liveSource.Id);

        var listed = await svc.ListActiveAsync();

        listed.Should().ContainSingle("only the genuinely-orphaned row is offered to the panel")
            .Which.VolumeId.Should().Be("orphan");
    }

    [Fact]
    public async Task TryPurge_failure_path_releases_ONLY_its_own_row_not_a_concurrent_purges_reservation()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var deleterA = new RecordingRetainedVolumeDeleter { Result = false };
        var svc = new RetainedVolumeService(repo, deleterA);
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("a", "pvc-a", "1Gi", VolumeCriticality.Critical, null) }, Now);
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("b", "pvc-b", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var rowA = (await svc.ListActiveAsync()).Single(r => r.VolumeId == "a");
        var rowB = (await svc.ListActiveAsync()).Single(r => r.VolumeId == "b");

        (await repo.ReserveUnderSentinelForTest(rowB.Id)).Should().BeTrue();

        (await svc.TryPurgeAsync(rowA.Id, Now.AddDays(1))).Kind.Should().Be(RetainedVolumePurgeOutcome.DeleteFailed);

        var reloadedB = (await repo.ListAllAsync()).Single(r => r.VolumeId == "b");
        reloadedB.ClaimedByWorkloadInstanceId.Should().Be(InMemoryRetainedVolumeRepository.PurgeSentinelForTest,
            "purge A's rollback must not release purge B's in-flight reservation of a different row");
    }

    [Fact]
    public async Task TryMarkReattached_returns_true_for_an_active_row_and_drops_it_from_active()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var id = (await svc.ListActiveAsync()).Single().Id;

        var handled = await svc.TryMarkReattachedAsync(id, Now.AddDays(1));

        handled.Should().BeTrue();
        (await svc.ListActiveAsync()).Should().BeEmpty("a reattached/dismissed row leaves the active view");
        (await svc.ListAsync()).Single().ReattachedAtUtc.Should().Be(Now.AddDays(1));
    }

    [Fact]
    public async Task TryMarkReattached_returns_false_for_an_unknown_or_already_inactive_row()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var id = (await svc.ListActiveAsync()).Single().Id;
        (await svc.TryMarkReattachedAsync(id, Now.AddDays(1))).Should().BeTrue();

        var again = await svc.TryMarkReattachedAsync(id, Now.AddDays(2));
        var unknown = await svc.TryMarkReattachedAsync(Guid.NewGuid(), Now);

        again.Should().BeFalse();
        unknown.Should().BeFalse();
    }

    [Fact]
    public async Task Purge_and_MarkReattached_are_idempotent_and_no_op_for_unknown_ids()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(DeployedInstance(), new[] { Volume("state", "pvc-state", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var id = (await svc.ListAsync()).Single().Id;

        await svc.PurgeAsync(id, Now);
        await svc.PurgeAsync(id, Now.AddDays(1));
        await svc.MarkReattachedAsync(id, Now.AddDays(1));
        await svc.PurgeAsync(Guid.NewGuid(), Now);

        var record = (await svc.ListAsync()).Single();
        record.PurgedAtUtc.Should().Be(Now, "idempotent purge keeps the first timestamp");
        record.ReattachedAtUtc.Should().BeNull("a purged record is not later reattached");
    }

    [Fact]
    public async Task ListActive_excludes_purged_and_reattached_rows_and_returns_only_active()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(InstanceWith(Guid.NewGuid()), new[] { Volume("active", "pvc-active", "1Gi", VolumeCriticality.Critical, null) }, Now);
        await svc.RecordRetainedAsync(InstanceWith(Guid.NewGuid()), new[] { Volume("purged", "pvc-purged", "1Gi", VolumeCriticality.Critical, null) }, Now);
        await svc.RecordRetainedAsync(InstanceWith(Guid.NewGuid()), new[] { Volume("reattached", "pvc-reattached", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var byVolume = (await svc.ListAsync()).ToDictionary(r => r.VolumeId, r => r.Id);
        await svc.PurgeAsync(byVolume["purged"], Now.AddDays(1));
        await svc.MarkReattachedAsync(byVolume["reattached"], Now.AddDays(1));

        var active = await svc.ListActiveAsync();

        active.Should().ContainSingle("only the neither-purged-nor-reattached row is active")
            .Which.VolumeId.Should().Be("active");
    }

    [Fact]
    public async Task ListActiveByPluginType_returns_only_active_records_of_the_matching_plugin_type()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(ZwaveInstance(), new[] { Volume("state", "pvc-zwave-state", "1Gi", VolumeCriticality.Critical, "bonded z-wave network") }, Now);
        await svc.RecordRetainedAsync(ZwaveInstance(), new[] { Volume("cache", "pvc-zwave-cache", "256Mi", VolumeCriticality.Recoverable, null) }, Now);
        await svc.RecordRetainedAsync(ZigbeeInstance(), new[] { Volume("state", "pvc-zigbee-state", "1Gi", VolumeCriticality.Critical, "zigbee coordinator") }, Now);

        var matches = await svc.ListActiveByPluginTypeAsync(GroupId);

        matches.Should().HaveCount(2, "only the two zwave volumes match — the zigbee volume is a different plugin type");
        matches.Should().OnlyContain(r => r.GroupId == GroupId);
        matches.Select(r => r.VolumeId).Should().BeEquivalentTo(new[] { "state", "cache" });
        matches.Single(r => r.VolumeId == "state").Description.Should().Be("bonded z-wave network");
    }

    [Fact]
    public async Task ListActiveByPluginType_excludes_reattached_and_purged_rows_of_the_matching_type()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(ZwaveInstance(), new[] { Volume("active", "pvc-active", "1Gi", VolumeCriticality.Critical, null) }, Now);
        await svc.RecordRetainedAsync(ZwaveInstance(), new[] { Volume("reattached", "pvc-reattached", "1Gi", VolumeCriticality.Critical, null) }, Now);
        await svc.RecordRetainedAsync(ZwaveInstance(), new[] { Volume("purged", "pvc-purged", "1Gi", VolumeCriticality.Critical, null) }, Now);
        var byVolume = (await svc.ListAsync()).ToDictionary(r => r.VolumeId, r => r.Id);
        await svc.MarkReattachedAsync(byVolume["reattached"], Now.AddDays(1));
        await svc.PurgeAsync(byVolume["purged"], Now.AddDays(1));

        var matches = await svc.ListActiveByPluginTypeAsync(GroupId);

        matches.Should().ContainSingle("only the still-active zwave row is offered for restore")
            .Which.VolumeId.Should().Be("active");
    }

    [Fact]
    public async Task ListActiveByPluginType_returns_empty_for_a_type_with_no_retained_data()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(ZwaveInstance(), new[] { Volume("state", "pvc-zwave-state", "1Gi", VolumeCriticality.Critical, null) }, Now);

        var matches = await svc.ListActiveByPluginTypeAsync("some-other-plugin");

        matches.Should().BeEmpty("no retained volumes exist for a plugin type that never left data behind");
    }

    [Fact]
    public async Task ListOlderThan_excludes_reattached_and_purged_records()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(InstanceWith(Guid.NewGuid()), new[] { Volume("a", "pvc-a", "1Gi", VolumeCriticality.Critical, null) }, Now.AddDays(-100));
        await svc.RecordRetainedAsync(InstanceWith(Guid.NewGuid()), new[] { Volume("b", "pvc-b", "1Gi", VolumeCriticality.Critical, null) }, Now.AddDays(-100));
        var ids = (await svc.ListAsync()).Select(r => r.Id).ToList();
        await svc.PurgeAsync(ids[0], Now);

        var stale = await svc.ListOlderThanAsync(TimeSpan.FromDays(90), Now);

        stale.Should().ContainSingle("the purged record is excluded even though it is >90 days old");
        stale.Single().VolumeId.Should().Be("b");
    }

    [Fact]
    public async Task ListOlderThan_excludes_a_CLAIMED_row_from_the_D9_scan()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        await svc.RecordRetainedAsync(InstanceWith(Guid.NewGuid()), new[] { Volume("claimed", "pvc-claimed", "1Gi", VolumeCriticality.Critical, null) }, Now.AddDays(-100));
        await svc.RecordRetainedAsync(InstanceWith(Guid.NewGuid()), new[] { Volume("free", "pvc-free", "1Gi", VolumeCriticality.Critical, null) }, Now.AddDays(-100));

        var claimedRow = (await svc.ListActiveAsync()).Single(r => r.VolumeId == "claimed");
        (await svc.TryClaimForRestoreAsync(claimedRow.Id, Guid.NewGuid())).Should().BeTrue();

        var stale = await svc.ListOlderThanAsync(TimeSpan.FromDays(90), Now);

        stale.Should().ContainSingle("a CLAIMED row is excluded from the 90-day scan — only the unclaimed stale row is surfaced");
        stale.Single().VolumeId.Should().Be("free");
    }

    [Fact]
    public async Task ListOlderThan_excludes_a_row_whose_SOURCE_instance_still_exists_from_the_D9_scan()
    {
        var repo = new InMemoryRetainedVolumeRepository();
        var svc = new RetainedVolumeService(repo, new NullRetainedVolumeDeleter());
        var parkedSource = Guid.NewGuid();
        await svc.RecordRetainedAsync(InstanceWith(parkedSource), new[] { Volume("parked", "pvc-parked", "1Gi", VolumeCriticality.Critical, null) }, Now.AddDays(-100));
        await svc.RecordRetainedAsync(InstanceWith(Guid.NewGuid()), new[] { Volume("orphaned", "pvc-orphaned", "1Gi", VolumeCriticality.Critical, null) }, Now.AddDays(-100));

        repo.ExistingSourceInstanceIds.Add(parkedSource);

        var stale = await svc.ListOlderThanAsync(TimeSpan.FromDays(90), Now);

        stale.Should().ContainSingle("FIX-F: a source-still-exists (revocation-parked) row is excluded — only the genuinely-orphaned stale row is surfaced");
        stale.Single().VolumeId.Should().Be("orphaned");
    }


    private static WorkloadInstance DeployedInstance() => InstanceWith(Guid.NewGuid());

    private static WorkloadInstance InstanceWith(Guid instanceId)
        => InstanceWith(instanceId, GroupId, WorkloadId);

    private static WorkloadInstance ZwaveInstance() => InstanceWith(Guid.NewGuid(), GroupId, WorkloadId);

    private static WorkloadInstance ZigbeeInstance() => InstanceWith(Guid.NewGuid(), "zigbee", "zigbee2mqtt");

    private static WorkloadInstance InstanceWith(Guid instanceId, string pluginTypeId, string workloadId)
    {
        var instance = WorkloadInstance.Create(instanceId, Guid.NewGuid(), pluginTypeId, workloadId, Now);
        instance.SetProvider(KubernetesProvider.Id, Now);
        return instance;
    }

    private static RetainedVolumeInput Volume(string id, string resourceName, string size, VolumeCriticality criticality, string? description)
        => new()
        {
            VolumeId = id,
            ResourceName = resourceName,
            Size = size,
            Criticality = criticality,
            Description = description,
        };

    private sealed class RecordingRetainedVolumeDeleter : IRetainedVolumeDeleter
    {
        public List<string> DeletedResourceNames { get; } = new();

        public bool Result { get; set; } = true;

        public Task<bool> TryDeletePhysicalVolumeAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
        {
            DeletedResourceNames.Add(retainedVolume.ResourceName);
            return Task.FromResult(Result);
        }
    }

    private sealed class InMemoryRetainedVolumeRepository : IRetainedVolumeRepository
    {
        private readonly List<RetainedVolume> store = new();

        public HashSet<Guid> ExistingSourceInstanceIds { get; } = new();

        public static readonly Guid PurgeSentinelForTest = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

        public Task<bool> ReserveUnderSentinelForTest(Guid rowId)
            => TryClaimForRestoreAsync(rowId, PurgeSentinelForTest);

        public Task<RetainedVolume?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(store.FirstOrDefault(r => r.Id == id));

        public Task<RetainedVolume?> FindBySourceAsync(Guid sourceWorkloadInstanceId, string volumeId, CancellationToken cancellationToken = default)
        {
            var normalized = (volumeId ?? string.Empty).Trim();
            return Task.FromResult(store.FirstOrDefault(r => r.SourceWorkloadInstanceId == sourceWorkloadInstanceId
                                                             && r.VolumeId == normalized
                                                             && r.ReattachedAtUtc is null
                                                             && r.PurgedAtUtc is null));
        }

        public Task<IReadOnlyList<RetainedVolume>> ListActiveBySourceInstanceAsync(
            Guid sourceWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RetainedVolume>>(
                store.Where(r => r.SourceWorkloadInstanceId == sourceWorkloadInstanceId
                                 && r.ReattachedAtUtc is null
                                 && r.PurgedAtUtc is null)
                    .OrderBy(r => r.RetainedAtUtc)
                    .ToList());

        public Task<RetainedVolume?> FindActiveByResourceNameAsync(string resourceName, CancellationToken cancellationToken = default)
        {
            var normalized = (resourceName ?? string.Empty).Trim();
            return Task.FromResult(store.FirstOrDefault(r => r.ResourceName == normalized
                                                             && r.ReattachedAtUtc is null
                                                             && r.PurgedAtUtc is null));
        }

        public Task<IReadOnlyList<RetainedVolume>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RetainedVolume>>(store.OrderBy(r => r.RetainedAtUtc).ToList());

        public Task<IReadOnlyList<RetainedVolume>> ListActiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RetainedVolume>>(
                store.Where(r => r.ReattachedAtUtc is null && r.PurgedAtUtc is null)
                    .OrderBy(r => r.RetainedAtUtc)
                    .ToList());

        public Task<IReadOnlyList<RetainedVolume>> ListActiveWithoutLiveSourceOlderThanAsync(
            DateTimeOffset retainedOnOrBeforeUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RetainedVolume>>(
                store.Where(r => r.ReattachedAtUtc is null
                                 && r.PurgedAtUtc is null
                                 && r.ClaimedByWorkloadInstanceId is null
                                 && r.RetainedAtUtc <= retainedOnOrBeforeUtc
                                 && !ExistingSourceInstanceIds.Contains(r.SourceWorkloadInstanceId))
                    .OrderBy(r => r.RetainedAtUtc)
                    .ToList());

        public Task<IReadOnlyList<RetainedVolume>> ListActiveByPluginTypeAsync(
            string pluginTypeId, CancellationToken cancellationToken = default)
        {
            var normalized = (pluginTypeId ?? string.Empty).Trim();
            return Task.FromResult<IReadOnlyList<RetainedVolume>>(
                store.Where(r => r.GroupId == normalized
                                 && r.ReattachedAtUtc is null
                                 && r.PurgedAtUtc is null)
                    .OrderBy(r => r.RetainedAtUtc)
                    .ToList());
        }

        public Task<bool> TryClaimForRestoreAsync(Guid retainedVolumeId, Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(store.FirstOrDefault(r => r.Id == retainedVolumeId)?.TryClaimForRestore(claimingWorkloadInstanceId) ?? false);

        public Task<int> ReleaseClaimsForInstanceAsync(Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(store.Count(r => r.ReleaseClaimFor(claimingWorkloadInstanceId)));

        public Task<bool> ReleaseClaimAsync(Guid rowId, Guid claimant, CancellationToken cancellationToken = default)
        {
            var row = store.FirstOrDefault(r => r.Id == rowId);
            if (row is null || row.ClaimedByWorkloadInstanceId != claimant
                || row.ReattachedAtUtc is not null || row.PurgedAtUtc is not null)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(row.ReleaseClaimFor(claimant));
        }

        public Task<bool> SourceInstanceExistsAsync(Guid sourceWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(ExistingSourceInstanceIds.Contains(sourceWorkloadInstanceId));

        public Task<IReadOnlyList<RetainedVolume>> ListActiveWithoutLiveSourceAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RetainedVolume>>(
                store.Where(r => r.ReattachedAtUtc is null
                                 && r.PurgedAtUtc is null
                                 && !ExistingSourceInstanceIds.Contains(r.SourceWorkloadInstanceId))
                    .OrderBy(r => r.RetainedAtUtc)
                    .ToList());

        public Task<bool> AnyActiveClaimForResourceNameAsync(string resourceName, Guid? excludingClaimingInstanceId, CancellationToken cancellationToken = default)
        {
            var normalized = (resourceName ?? string.Empty).Trim();
            return Task.FromResult(store.Any(r => r.ResourceName == normalized
                                                  && r.ReattachedAtUtc is null
                                                  && r.PurgedAtUtc is null
                                                  && r.ClaimedByWorkloadInstanceId is not null
                                                  && r.ClaimedByWorkloadInstanceId != excludingClaimingInstanceId));
        }

        public Task<bool> AnyActiveClaimByInstanceAsync(Guid claimingWorkloadInstanceId, CancellationToken cancellationToken = default)
            => Task.FromResult(store.Any(r => r.ReattachedAtUtc is null && r.PurgedAtUtc is null && r.ClaimedByWorkloadInstanceId == claimingWorkloadInstanceId));

        public Task<IReadOnlyList<RetainedVolume>> ListOfferableByPluginTypeAsync(string pluginTypeId, CancellationToken cancellationToken = default)
        {
            var normalized = (pluginTypeId ?? string.Empty).Trim();
            return Task.FromResult<IReadOnlyList<RetainedVolume>>(
                store.Where(r => r.GroupId == normalized
                                 && r.ReattachedAtUtc is null
                                 && r.PurgedAtUtc is null
                                 && r.ClaimedByWorkloadInstanceId is null)
                    .OrderBy(r => r.RetainedAtUtc)
                    .ToList());
        }

        public Task<IReadOnlyList<RetainedVolume>> ListActiveByPluginTypeForInstanceAsync(string pluginTypeId, Guid forWorkloadInstanceId, CancellationToken cancellationToken = default)
        {
            var normalized = (pluginTypeId ?? string.Empty).Trim();
            return Task.FromResult<IReadOnlyList<RetainedVolume>>(
                store.Where(r => r.GroupId == normalized
                                 && r.ReattachedAtUtc is null
                                 && r.PurgedAtUtc is null
                                 && (r.ClaimedByWorkloadInstanceId is null || r.ClaimedByWorkloadInstanceId == forWorkloadInstanceId))
                    .OrderBy(r => r.RetainedAtUtc)
                    .ToList());
        }

        public Task AddAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
        {
            if (store.Any(r => r.ResourceName == retainedVolume.ResourceName
                               && r.ReattachedAtUtc is null
                               && r.PurgedAtUtc is null))
            {
                throw new InvalidOperationException(
                    $"active-resource-name uniqueness violated for '{retainedVolume.ResourceName}' (partial-unique index; fake fidelity)");
            }

            store.Add(retainedVolume);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(RetainedVolume retainedVolume, CancellationToken cancellationToken = default)
        {
            if (!store.Contains(retainedVolume))
            {
                store.Add(retainedVolume);
            }

            return Task.CompletedTask;
        }
    }
}
