using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core;
using Reeve.Workloads.EntityFrameworkCore;
using Reeve.Workloads.EntityFrameworkCore.Persistence;
using Reeve.Workloads.Tests.Shared;

namespace Reeve.Workloads.EntityFrameworkCore.IntegrationTests;

[Collection(WorkloadsStoreCollection.Name)]
public sealed class RetainedVolumeRepositoryClaimTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);
    private const string GroupId = "zwave";
    private const string WorkloadId = "zwave-js";

    private readonly PostgresFixture postgres;

    public RetainedVolumeRepositoryClaimTests(PostgresFixture postgres) => this.postgres = postgres;

    private static RetainedVolume Record(string resourceName, string volumeId = "state")
        => RetainedVolume.Record(
            Guid.NewGuid(), GroupId, WorkloadId, volumeId, resourceName, "1Gi",
            VolumeCriticality.Critical, description: null, StandInProviders.KubernetesId,
            RetainedVolumeSchemeId.Kubernetes,
            sourceGroupInstanceId: Guid.NewGuid(), sourceWorkloadInstanceId: Guid.NewGuid(), Now);

    [SkippableFact]
    public async Task Two_concurrent_claims_on_one_row_exactly_one_wins()
    {
        var store = await postgres.CreateStoreAsync();
        await using var dbContext = store.NewContext();
        var seeded = Record("reeve-wl-zwave-zwave-js-state-abc12345");
        dbContext.RetainedVolumes.Add(seeded);
        await dbContext.SaveChangesAsync();

        var instanceA = Guid.NewGuid();
        var instanceB = Guid.NewGuid();

        async Task<bool> ClaimAsync(Guid instanceId)
        {
            await using var ctx = store.NewContext();
            var repo = new RetainedVolumeRepository(ctx);
            return await repo.TryClaimForRestoreAsync(seeded.Id, instanceId);
        }

        var results = await Task.WhenAll(ClaimAsync(instanceA), ClaimAsync(instanceB));

        results.Count(won => won).Should().Be(1,
            "the atomic rowcount-guarded UPDATE lets EXACTLY ONE concurrent claimant win the same row");

        await using var readCtx = store.NewContext();
        var row = await readCtx.RetainedVolumes.AsNoTracking().FirstAsync(r => r.Id == seeded.Id);
        row.ClaimedByWorkloadInstanceId.Should().NotBeNull();
        new[] { instanceA, instanceB }.Should().Contain(row.ClaimedByWorkloadInstanceId!.Value);
    }

    [SkippableFact]
    public async Task Release_makes_a_claimed_row_claimable_again()
    {
        var store = await postgres.CreateStoreAsync();
        await using var dbContext = store.NewContext();
        var repo = new RetainedVolumeRepository(dbContext);
        var seeded = Record("reeve-wl-zwave-zwave-js-state-def45678");
        dbContext.RetainedVolumes.Add(seeded);
        await dbContext.SaveChangesAsync();

        var instanceA = Guid.NewGuid();
        var instanceB = Guid.NewGuid();

        (await repo.TryClaimForRestoreAsync(seeded.Id, instanceA)).Should().BeTrue("A claims the free row");
        (await repo.TryClaimForRestoreAsync(seeded.Id, instanceB)).Should().BeFalse("B cannot claim A's row");

        var released = await repo.ReleaseClaimsForInstanceAsync(instanceA);
        released.Should().Be(1, "A's single claim is released");

        (await repo.TryClaimForRestoreAsync(seeded.Id, instanceB)).Should().BeTrue(
            "after release the row is claimable again — B now wins it");
    }

    [SkippableFact]
    public async Task Offer_query_excludes_a_claimed_row()
    {
        var store = await postgres.CreateStoreAsync();
        await using var dbContext = store.NewContext();
        var repo = new RetainedVolumeRepository(dbContext);
        var claimed = Record("reeve-wl-zwave-zwave-js-state-claimed01");
        var free = Record("reeve-wl-zwave-zwave-js-cache-free0002", volumeId: "cache");
        dbContext.RetainedVolumes.AddRange(claimed, free);
        await dbContext.SaveChangesAsync();

        (await repo.TryClaimForRestoreAsync(claimed.Id, Guid.NewGuid())).Should().BeTrue();

        var offerable = await repo.ListOfferableByPluginTypeAsync(GroupId);

        offerable.Should().ContainSingle("a claimed row is excluded from the wizard offer set")
            .Which.Id.Should().Be(free.Id);
    }

    [SkippableFact]
    public async Task AnyActiveClaimForResourceName_sees_another_instances_claim_and_excludes_the_holder()
    {
        var store = await postgres.CreateStoreAsync();
        await using var dbContext = store.NewContext();
        var repo = new RetainedVolumeRepository(dbContext);
        const string name = "reeve-wl-zwave-zwave-js-state-shared01";
        var row = Record(name);
        dbContext.RetainedVolumes.Add(row);
        await dbContext.SaveChangesAsync();

        var holder = Guid.NewGuid();
        (await repo.TryClaimForRestoreAsync(row.Id, holder)).Should().BeTrue();

        (await repo.AnyActiveClaimForResourceNameAsync(name, excludingClaimingInstanceId: null))
            .Should().BeTrue("a purge with no exclusion sees the active claim on the physical name");
        (await repo.AnyActiveClaimForResourceNameAsync(name, excludingClaimingInstanceId: holder))
            .Should().BeFalse("the holder's OWN claim does not block its own teardown");
        (await repo.AnyActiveClaimForResourceNameAsync(name, excludingClaimingInstanceId: Guid.NewGuid()))
            .Should().BeTrue("a claim by another instance blocks a purge of the physical name");
        (await repo.AnyActiveClaimForResourceNameAsync("some-other-name", excludingClaimingInstanceId: null))
            .Should().BeFalse("an unrelated name has no claim");
    }

    [SkippableFact]
    public async Task Partial_unique_index_refuses_a_second_active_row_sharing_a_resource_name()
    {
        var store = await postgres.CreateStoreAsync();
        await using var dbContext = store.NewContext();
        const string name = "reeve-wl-zwave-zwave-js-state-dup00001";
        dbContext.RetainedVolumes.Add(Record(name));
        await dbContext.SaveChangesAsync();

        await using var ctx2 = store.NewContext();
        ctx2.RetainedVolumes.Add(Record(name));

        var act = async () => await ctx2.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>(
            "the partial-unique index (ux_retained_volumes_active_resource_name) forbids a 2nd ACTIVE row sharing a physical ResourceName");
    }

    [SkippableFact]
    public async Task RecordRetained_ABSORBS_a_would_be_duplicate_active_name_instead_of_violating_the_index()
    {
        var store = await postgres.CreateStoreAsync();
        await using var dbContext = store.NewContext();
        const string sharedName = "reeve-wl-zwave-zwave-js-state-abs00001";

        dbContext.RetainedVolumes.Add(Record(sharedName));
        await dbContext.SaveChangesAsync();

        await using var ctx2 = store.NewContext();
        var svc = new RetainedVolumeService(new RetainedVolumeRepository(ctx2), new NullRetainedVolumeDeleter());
        var restored = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, Now);
        restored.SetProvider(StandInProviders.KubernetesId, Now);

        var written = await svc.RecordRetainedAsync(
            restored,
            new[] { new RetainedVolumeInput { VolumeId = "state", ResourceName = sharedName, Size = "1Gi", Criticality = VolumeCriticality.Critical } },
            Now.AddMinutes(5));

        written.Should().Be(0, "the active row already binds the physical name — the record is absorbed, not duplicated (no index violation, no livelock)");
        var active = await svc.ListActiveAsync();
        active.Should().ContainSingle("exactly one active row binds the physical volume — the durable pointer is intact")
            .Which.ResourceName.Should().Be(sharedName);
    }

    [SkippableFact]
    public async Task Offer_query_EXCLUDES_any_row_whose_source_instance_still_exists_only_orphaned_rows_are_offerable()
    {
        var store = await postgres.CreateStoreAsync();
        await using var dbContext = store.NewContext();
        var repo = new RetainedVolumeRepository(dbContext);

        var parkedSource = SeedWorkloadInstance(dbContext, WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling);
        var rowA = RecordFromSource("reeve-wl-zwave-zwave-js-state-parked01", parkedSource.Id);
        var rowB = RecordFromSource("reeve-wl-zwave-zwave-js-cache-gone0001", Guid.NewGuid(), volumeId: "cache");
        var liveSource = SeedWorkloadInstance(dbContext, WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved);
        var rowC = RecordFromSource("reeve-wl-zwave-zwave-js-logs-live0001", liveSource.Id, volumeId: "logs");
        var deployingSource = SeedWorkloadInstance(dbContext, WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning);
        var rowD = RecordFromSource("reeve-wl-zwave-zwave-js-data-deploy01", deployingSource.Id, volumeId: "data");
        dbContext.RetainedVolumes.AddRange(rowA, rowB, rowC, rowD);
        await dbContext.SaveChangesAsync();

        var offerable = await repo.ListOfferableByPluginTypeAsync(GroupId);

        offerable.Select(r => r.Id).Should().BeEquivalentTo(new[] { rowB.Id },
            "only the genuinely-orphaned (source uninstalled → row gone) row is offerable; every row whose source still exists (tearing down, Healthy, or Deploying) is excluded");

        var forInstance = await repo.ListActiveByPluginTypeForInstanceAsync(GroupId, Guid.NewGuid());
        forInstance.Select(r => r.Id).Should().BeEquivalentTo(new[] { rowB.Id },
            "restore validation for a fresh install also excludes every source-still-exists row");
    }

    [SkippableFact]
    public async Task Per_install_validation_still_sees_the_installs_OWN_claimed_row_even_when_its_source_exists()
    {
        var store = await postgres.CreateStoreAsync();
        await using var dbContext = store.NewContext();
        var repo = new RetainedVolumeRepository(dbContext);

        var liveSource = SeedWorkloadInstance(dbContext, WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved);
        var row = RecordFromSource("reeve-wl-zwave-zwave-js-state-ownclm01", liveSource.Id);
        dbContext.RetainedVolumes.Add(row);
        await dbContext.SaveChangesAsync();

        var installingInstance = Guid.NewGuid();
        (await repo.TryClaimForRestoreAsync(row.Id, installingInstance)).Should().BeTrue("this install claims the row");

        var forInstance = await repo.ListActiveByPluginTypeForInstanceAsync(GroupId, installingInstance);

        forInstance.Select(r => r.Id).Should().Contain(row.Id,
            "a row this install already claimed is visible to its own re-validation even though its source instance still exists");
    }

    [SkippableFact]
    public async Task D9_scan_query_EXCLUDES_a_source_alive_row_but_INCLUDES_a_source_gone_row_and_a_claimed_row()
    {
        var store = await postgres.CreateStoreAsync();
        await using var dbContext = store.NewContext();
        var repo = new RetainedVolumeRepository(dbContext);
        var oldEnough = Now.AddDays(-100);

        var parkedSource = SeedWorkloadInstance(dbContext, WorkloadInstanceState.Terminating, WorkloadStateReason.Uninstalling);
        var rowA = RecordFromSource("reeve-wl-zwave-zwave-js-state-d9park01", parkedSource.Id, retainedAt: oldEnough);
        var rowB = RecordFromSource("reeve-wl-zwave-zwave-js-cache-d9gone01", Guid.NewGuid(), volumeId: "cache", retainedAt: oldEnough);
        var rowC = RecordFromSource("reeve-wl-zwave-zwave-js-logs-d9clm001", Guid.NewGuid(), volumeId: "logs", retainedAt: oldEnough);
        var rowD = RecordFromSource("reeve-wl-zwave-zwave-js-data-d9new001", Guid.NewGuid(), volumeId: "data", retainedAt: Now.AddDays(-10));
        dbContext.RetainedVolumes.AddRange(rowA, rowB, rowC, rowD);
        await dbContext.SaveChangesAsync();

        (await repo.TryClaimForRestoreAsync(rowC.Id, Guid.NewGuid())).Should().BeTrue("an in-flight restore claims row C");

        var scanned = await repo.ListActiveWithoutLiveSourceOlderThanAsync(Now.AddDays(-90));

        scanned.Select(r => r.Id).Should().BeEquivalentTo(new[] { rowB.Id },
            "FIX-F: only the genuinely-orphaned, unclaimed, older-than-cutoff row is surfaced; the source-alive (tearing down), claimed, and too-new rows are all excluded");
    }

    private static WorkloadInstance SeedWorkloadInstance(
        WorkloadsDbContext dbContext, WorkloadInstanceState state, WorkloadStateReason reason)
    {
        var instance = WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), GroupId, WorkloadId, Now);
        instance.SetProvider(StandInProviders.KubernetesId, Now);
        instance.TransitionTo(WorkloadInstanceState.Deploying, WorkloadStateReason.Provisioning, null, Now);
        if (state == WorkloadInstanceState.Healthy)
        {
            instance.TransitionTo(WorkloadInstanceState.Healthy, WorkloadStateReason.HealthObserved, null, Now);
        }
        else
        {
            instance.TransitionTo(WorkloadInstanceState.Terminating, reason, "uninstalling", Now);
        }

        dbContext.WorkloadInstances.Add(instance);
        return instance;
    }

    private static RetainedVolume RecordFromSource(
        string resourceName, Guid sourceWorkloadInstanceId, string volumeId = "state", DateTimeOffset? retainedAt = null)
        => RetainedVolume.Record(
            Guid.NewGuid(), GroupId, WorkloadId, volumeId, resourceName, "1Gi",
            VolumeCriticality.Critical, description: null, StandInProviders.KubernetesId,
            RetainedVolumeSchemeId.Kubernetes,
            sourceGroupInstanceId: Guid.NewGuid(), sourceWorkloadInstanceId: sourceWorkloadInstanceId, retainedAt ?? Now);

    [SkippableFact]
    public async Task RecordRetained_re_records_a_FRESH_active_row_after_a_reattach_of_the_same_source_and_volume()
    {
        var store = await postgres.CreateStoreAsync();
        await using var dbContext = store.NewContext();
        const string name = "reeve-wl-zwave-zwave-js-state-a1regr01";
        var sourceInstanceId = Guid.NewGuid();

        var svc = new RetainedVolumeService(new RetainedVolumeRepository(dbContext), new NullRetainedVolumeDeleter());
        var instance = WorkloadInstance.Create(sourceInstanceId, Guid.NewGuid(), GroupId, WorkloadId, Now);
        instance.SetProvider(StandInProviders.KubernetesId, Now);
        var vols = new[] { new RetainedVolumeInput { VolumeId = "state", ResourceName = name, Size = "1Gi", Criticality = VolumeCriticality.Critical } };
        (await svc.RecordRetainedAsync(instance, vols, Now)).Should().Be(1);

        var row = await dbContext.RetainedVolumes.FirstAsync(r => r.SourceWorkloadInstanceId == sourceInstanceId);
        row.MarkReattached(Now.AddMinutes(1));
        await dbContext.SaveChangesAsync();

        await using var ctx2 = store.NewContext();
        var svc2 = new RetainedVolumeService(new RetainedVolumeRepository(ctx2), new NullRetainedVolumeDeleter());

        var written = await svc2.RecordRetainedAsync(instance, vols, Now.AddMinutes(2));

        written.Should().Be(1, "a re-record after a genuine reattach INSERTS a fresh active row (the reattached row does not block it)");
        var active = await svc2.ListActiveByPluginTypeAsync(GroupId);
        active.Should().ContainSingle("the surviving PVC is visible again via a fresh active row — never invisible data loss")
            .Which.ResourceName.Should().Be(name);
        (await ctx2.RetainedVolumes.CountAsync()).Should().Be(2, "the reattached row is retained as the audit trail; the fresh row is added");
    }

    [SkippableFact]
    public async Task Partial_unique_index_allows_re_recording_a_name_after_the_prior_row_is_reattached()
    {
        var store = await postgres.CreateStoreAsync();
        await using var dbContext = store.NewContext();
        const string name = "reeve-wl-zwave-zwave-js-state-reuse001";
        var first = Record(name);
        dbContext.RetainedVolumes.Add(first);
        await dbContext.SaveChangesAsync();

        first.MarkReattached(Now.AddDays(1));
        await dbContext.SaveChangesAsync();

        dbContext.RetainedVolumes.Add(Record(name));
        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().NotThrowAsync(
            "the partial index only guards ACTIVE rows — a reattached (inactive) audit row does not block re-recording the name");
    }
}
