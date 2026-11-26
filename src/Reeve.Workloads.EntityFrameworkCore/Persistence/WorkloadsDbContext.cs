using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Reeve.Workloads.Abstractions.Domain.Entities;

using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.EntityFrameworkCore.Persistence;

/// <summary>The workloads model, in its own context and its own schema. It deliberately does NOT extend
/// SmartHomeDbContext: sharing a model is what makes the boundary unenforceable.</summary>
public class WorkloadsDbContext : DbContext
{
    private readonly WorkloadsStoreOptions _storeOptions;
    private readonly string _schema;

    public WorkloadsDbContext(DbContextOptions<WorkloadsDbContext> options, WorkloadsStoreOptions storeOptions)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(storeOptions);
        _storeOptions = storeOptions;
        _schema = storeOptions.Schema;
    }

    /// <summary>Checks, on EVERY context built over options the library configured, that the migrations assembly
    /// and the history table are still the library's.
    ///
    /// Here rather than anywhere earlier because this is the only hook EF runs per CONTEXT. Both
    /// <c>IDbContextOptionsExtension.Validate</c> and <c>ISingletonOptions.Validate</c> are reached only when EF
    /// BUILDS an internal service provider, and it caches those on a key made of each extension's
    /// <c>GetServiceProviderHashCode</c> and <c>ShouldUseSameServiceProvider</c>. Two options sets differing only
    /// in the migrations assembly answer both IDENTICALLY, because that assembly changes no service — measured on
    /// EF Core 10 against the provider this library ships. So a second composition of the same shape reuses the
    /// first one's provider and is never looked at.
    ///
    /// And here rather than in the migrator because a host that migrates in a deployment step passes
    /// <c>runMigrations: false</c> and never constructs one. That is the path Reeve's own adoption takes, so a
    /// check the migrator made would have missed the host it was written for.</summary>
    /// <remarks>Does nothing unless <c>AddEntityFrameworkStores</c> configured these options, which it says by
    /// leaving a marker extension on them. A context built by hand — every test that news one up, and any host
    /// that still owns its own wiring — is not held to facts nothing promised on its behalf.
    ///
    /// A subclass that overrides this and does not call base opts out of the check, exactly as one overriding
    /// <c>OnModelCreating</c> opts out of the model it protects. That is a bound on what this can promise, not a
    /// hole worth closing: subclassing this context already means owning what it builds.</remarks>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        WorkloadsSchemaRegistration.EnsureNothingOverrodeIt(optionsBuilder.Options, _storeOptions);
    }

    public DbSet<WorkloadInstance> WorkloadInstances => Set<WorkloadInstance>();

    public DbSet<RetainedVolume> RetainedVolumes => Set<RetainedVolume>();

    public DbSet<WorkloadUpdateIntent> WorkloadUpdateIntents => Set<WorkloadUpdateIntent>();

    public DbSet<WorkloadUpdateIntentStep> WorkloadUpdateIntentSteps => Set<WorkloadUpdateIntentStep>();

    public DbSet<WorkloadInstallationRecord> WorkloadInstallation => Set<WorkloadInstallationRecord>();

    public DbSet<WorkloadGroup> WorkloadGroups => Set<WorkloadGroup>();

    internal bool ShapesJsonDocuments => Model
        .FindEntityType(typeof(WorkloadInstance))?
        .FindComplexProperty(nameof(WorkloadInstance.ParametersDocument)) is not null;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(_schema);

        var documents = Database.IsRelational();

        modelBuilder.Entity<WorkloadInstance>(builder =>
        {
            builder.ToTable("workload_instances", _schema);
            builder.HasKey(entity => entity.Id);
            builder.Ignore(entity => entity.Inst8);
            builder.Ignore(entity => entity.CanMutate);
            builder.Property(entity => entity.GroupInstanceId).IsRequired();
            builder.Property(entity => entity.GroupId).HasMaxLength(200).IsRequired();
            builder.Property(entity => entity.WorkloadId).HasMaxLength(200).IsRequired();
            builder.Property(entity => entity.Version).IsConcurrencyToken().IsRequired();
            builder.Property(entity => entity.Provider)
                .HasConversion(new WorkloadProviderIdConverter(), new WorkloadProviderIdComparer())
                .HasMaxLength(WorkloadProviderId.MaxLength);
            builder.Property(entity => entity.State)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            builder.Property(entity => entity.StateReason)
                .HasConversion<string>()
                .HasMaxLength(64)
                .IsRequired();
            builder.Property(entity => entity.StateMessage).HasMaxLength(WorkloadInstance.MaxStateMessageLength);
            builder.Property(entity => entity.ProvisioningPercent);
            builder.Property(entity => entity.DesiredSpecHash).HasMaxLength(128);
            builder.Property(entity => entity.AppliedSpecHash).HasMaxLength(128);
            builder.Property(entity => entity.ReferenceRolloutState);
            builder.Property(entity => entity.PendingScanRunId).HasMaxLength(64);
            builder.Ignore(entity => entity.HasPendingScanRun);
            builder.PrimitiveCollection(entity => entity.LastScanFailedNodes);
            builder.Property(entity => entity.ObserveOnly).IsRequired();
            builder.Property(entity => entity.RestartCount).IsRequired();
            builder.Property(entity => entity.Retention)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();
            builder.Ignore(entity => entity.WasDeployed);
            if (documents)
            {
                builder.ComplexProperty(entity => entity.ParametersDocument, document =>
                {
                    document.ToJson();
                    document.ComplexCollection(parameters => parameters.Entries);
                });
                builder.ComplexProperty(entity => entity.RetainedVolumesSnapshotDocument, document =>
                {
                    document.ToJson();
                    document.ComplexCollection(snapshot => snapshot.Volumes);
                });
                builder.ComplexProperty(entity => entity.AttachmentPinDocument, document =>
                    document.ToJson());
            }
            builder.Property(entity => entity.RestoreBindingUnresolved).IsRequired().HasDefaultValue(false);
            builder.Property(entity => entity.LastHealthyAtUtc);
            builder.Property(entity => entity.LastTransitionAtUtc).IsRequired();
            builder.Property(entity => entity.DeployStartedAtUtc);
            builder.Property(entity => entity.LastRejectedApplyAtUtc);
            builder.Property(entity => entity.CreatedAtUtc).IsRequired();
            builder.Property(entity => entity.UpdatedAtUtc).IsRequired();
            builder.HasIndex(entity => new { entity.GroupInstanceId, entity.WorkloadId })
                .IsUnique()
                .HasDatabaseName("ix_workload_instances_module_instance_id_workload_id");
            builder.HasIndex(entity => entity.State)
                .HasDatabaseName("ix_workload_instances_state");
        });

        modelBuilder.Entity<RetainedVolume>(builder =>
        {
            builder.ToTable("retained_volumes", _schema);
            builder.HasKey(entity => entity.Id);
            builder.Ignore(entity => entity.IsActive);
            builder.Property(entity => entity.GroupId).HasMaxLength(200).IsRequired();
            builder.Property(entity => entity.WorkloadId).HasMaxLength(200).IsRequired();
            builder.Property(entity => entity.VolumeId).HasMaxLength(200).IsRequired();
            builder.Property(entity => entity.ResourceName).HasMaxLength(253).IsRequired();
            builder.Property(entity => entity.Size).HasMaxLength(32).IsRequired();
            builder.Property(entity => entity.Criticality)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            builder.Property(entity => entity.Description).HasMaxLength(1000);
            builder.Property(entity => entity.Provider)
                .HasConversion(new WorkloadProviderIdConverter(), new WorkloadProviderIdComparer())
                .HasMaxLength(WorkloadProviderId.MaxLength)
                .IsRequired();
            builder.Property(entity => entity.VolumeNamingScheme)
                .HasConversion(new RetainedVolumeSchemeIdConverter(), new RetainedVolumeSchemeIdComparer())
                .HasMaxLength(RetainedVolumeSchemeId.MaxLength)
                .IsRequired()
                .HasDefaultValue(RetainedVolumeSchemeId.Kubernetes);
            builder.Property(entity => entity.SourceGroupInstanceId).IsRequired();
            builder.Property(entity => entity.SourceWorkloadInstanceId).IsRequired();
            builder.Property(entity => entity.RetainedAtUtc).IsRequired();
            builder.Property(entity => entity.ReattachedAtUtc);
            builder.Property(entity => entity.PurgedAtUtc);
            builder.Property(entity => entity.ClaimedByWorkloadInstanceId);
            builder.HasIndex(entity => entity.ClaimedByWorkloadInstanceId)
                .HasDatabaseName("ix_retained_volumes_claimed_by_workload_instance_id");
            builder.HasIndex(entity => new { entity.SourceWorkloadInstanceId, entity.VolumeId })
                .IsUnique()
                .HasDatabaseName("ix_retained_volumes_source_workload_instance_id_volume_id");
            builder.HasIndex(entity => new { entity.GroupId, entity.WorkloadId })
                .HasDatabaseName("ix_retained_volumes_module_id_workload_id");
            builder.HasIndex(entity => entity.RetainedAtUtc)
                .HasDatabaseName("ix_retained_volumes_retained_at_utc");
            builder.HasIndex(entity => entity.ResourceName)
                .IsUnique()
                .HasDatabaseName("ux_retained_volumes_active_resource_name");
        });

        modelBuilder.Entity<WorkloadUpdateIntent>(builder =>
        {
            builder.ToTable("workload_update_intents", _schema);
            builder.HasKey(entity => entity.Id);
            builder.Ignore(entity => entity.IsTerminal);
            builder.Ignore(entity => entity.IsActive);
            builder.Property(entity => entity.GroupInstanceId).IsRequired();
            builder.Property(entity => entity.CohortId);
            builder.Property(entity => entity.CohortOrdinal);
            builder.Property(entity => entity.CohortSize);
            builder.Property(entity => entity.FromVersion).HasMaxLength(128);
            builder.Property(entity => entity.ToVersion).HasMaxLength(128).IsRequired();
            builder.Property(entity => entity.Phase)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            builder.Property(entity => entity.Error).HasMaxLength(WorkloadUpdateIntent.MaxErrorLength);
            builder.Property(entity => entity.CreatedAtUtc).IsRequired();
            builder.Property(entity => entity.CompletedAtUtc);
            builder.Property(entity => entity.PhaseEnteredAtUtc).IsRequired();
            builder.Property(entity => entity.SidecarAppliedAtUtc);
            builder.Property(entity => entity.WalkFault).HasMaxLength(WorkloadUpdateIntent.MaxWalkFaultLength);
            builder.Property(entity => entity.WalkFaultSinceUtc);
            builder.PrimitiveCollection(entity => entity.AdoptedResourceWorkloadIds);
            if (documents)
            {
                builder.ComplexProperty(entity => entity.TargetSpecsDocument, document => SpecMap(document));
                builder.ComplexProperty(entity => entity.PreviousSpecsDocument, document => SpecMap(document));
                builder.ComplexProperty(entity => entity.PreviousParametersDocument, document =>
                {
                    document.ToJson();
                    document.ComplexCollection(map => map.Workloads, workload => workload.ComplexCollection(w => w.Entries));
                });
                builder.ComplexProperty(entity => entity.OperatorParametersDocument, document => StringMap(document));
                builder.ComplexProperty(entity => entity.PreviousPasswordParameterNamesDocument, document => StringMap(document));
                builder.ComplexProperty(entity => entity.CandidatePasswordParameterNamesDocument, document => StringMap(document));
                builder.ComplexProperty(entity => entity.FromDigestsDocument, document => StringMap(document));
                builder.ComplexProperty(entity => entity.ToDigestsDocument, document => StringMap(document));
                builder.ComplexProperty(entity => entity.EvidenceDocument, document =>
                {
                    document.ToJson();
                    document.ComplexCollection(evidence => evidence.Workloads);
                });
                builder.ComplexProperty(entity => entity.StageJournalDocument, document =>
                {
                    document.ToJson();
                    document.ComplexCollection(journal => journal.Entries);
                });
            }
            builder.Property(entity => entity.Version).IsConcurrencyToken().IsRequired();

            static void StringMap(
                ComplexPropertyBuilder<Reeve.Workloads.Abstractions.Domain.Entities.Documents.WorkloadStringMapDocument> document)
            {
                document.ToJson();
                document.ComplexCollection(map => map.Entries);
            }

            static void SpecMap(
                ComplexPropertyBuilder<Reeve.Workloads.Abstractions.Domain.Entities.Documents.WorkloadSpecMapDocument> document)
            {
                document.ToJson();
                document.ComplexCollection(map => map.Workloads, spec =>
                {
                    spec.ComplexCollection(workload => workload.Labels);
                    spec.ComplexCollection(workload => workload.NodeSelector);
                    spec.ComplexProperty(workload => workload.Resources);
                    spec.ComplexProperty(workload => workload.Network, network => network.ComplexCollection(n => n.Ports));
                    spec.ComplexProperty(workload => workload.Privileges, privileges =>
                    {
                        privileges.ComplexCollection(p => p.HostPaths);
                        privileges.ComplexProperty(p => p.NodeLocalState);
                    });
                    spec.ComplexCollection(workload => workload.Volumes);
                    spec.ComplexCollection(workload => workload.ConfigMapMounts);
                    spec.ComplexProperty(workload => workload.Health, health => health.ComplexProperty(h => h.Http));
                    spec.ComplexProperty(workload => workload.Observability, observability => observability.ComplexProperty(o => o.Metrics));
                    spec.ComplexCollection(workload => workload.Env);
                    spec.ComplexCollection(workload => workload.ProvisionedCredentials);
                    spec.ComplexCollection(workload => workload.HostRequirements);
                });
            }

            builder.HasMany(entity => entity.Steps)
                .WithOne()
                .HasForeignKey(step => step.IntentId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(entity => entity.GroupInstanceId)
                .IsUnique()
                .HasDatabaseName("ux_workload_update_intents_active_module_instance_id");
            builder.HasIndex(entity => entity.Phase)
                .HasDatabaseName("ix_workload_update_intents_phase");
            builder.HasIndex(entity => new { entity.CohortId, entity.CohortOrdinal })
                .IsUnique()
                .HasDatabaseName("ux_workload_update_intents_cohort_id_cohort_ordinal");
        });

        modelBuilder.Entity<WorkloadUpdateIntentStep>(builder =>
        {
            builder.ToTable("workload_update_intent_steps", _schema);
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.IntentId).IsRequired();
            builder.Property(entity => entity.WorkloadId).HasMaxLength(200).IsRequired();
            builder.Property(entity => entity.Step)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            builder.Property(entity => entity.State)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            builder.Property(entity => entity.Message).HasMaxLength(WorkloadUpdateIntentStep.MaxMessageLength);
            builder.Property(entity => entity.AppliedSpecHash).HasMaxLength(WorkloadUpdateIntentStep.MaxAppliedSpecHashLength);
            if (documents)
            {
                builder.ComplexProperty(entity => entity.GateClockStateDocument, document => document.ToJson());
            }
            builder.Property(entity => entity.CreatedAtUtc).IsRequired();
            builder.Property(entity => entity.UpdatedAtUtc).IsRequired();
            builder.HasIndex(entity => new { entity.IntentId, entity.WorkloadId })
                .IsUnique()
                .HasDatabaseName("ux_workload_update_intent_steps_intent_id_workload_id");
        });

        modelBuilder.Entity<WorkloadInstallationRecord>(builder =>
        {
            builder.ToTable("workload_installation", _schema);
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id).HasMaxLength(32).IsRequired();
            builder.Property(entity => entity.InstallationId)
                .HasMaxLength(WorkloadInstallationRecord.MaxInstallationIdLength)
                .IsRequired();
            builder.Property(entity => entity.MintedAtUtc).IsRequired();
        });

        modelBuilder.Entity<WorkloadGroup>(builder =>
        {
            builder.ToTable("workload_modules", _schema);
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.GroupId)
                .HasMaxLength(WorkloadGroup.MaxLength)
                .IsRequired();
            builder.Property(entity => entity.SourceKey)
                .HasMaxLength(WorkloadGroup.MaxLength)
                .IsRequired();
            builder.Property(entity => entity.InstalledVersion)
                .HasMaxLength(WorkloadGroup.MaxLength);
            builder.Property(entity => entity.CreatedAtUtc).IsRequired();
            builder.Property(entity => entity.UpdatedAtUtc).IsRequired();
            builder.Property(entity => entity.Version).IsConcurrencyToken().IsRequired();
        });

        if (!documents)
        {
            WorkloadDocumentStringColumns.MapAll(modelBuilder);
        }

        WorkloadsColumnNaming.Apply(modelBuilder);

        foreach (var conventions in WorkloadsDatabaseConventions.For(Database.ProviderName))
        {
            conventions.Apply(modelBuilder);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpInstanceVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpInstanceVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BumpInstanceVersions()
    {
        foreach (var entry in ChangeTracker.Entries<WorkloadInstance>())
        {
            if (entry.State == EntityState.Modified)
            {
                var version = entry.Property(instance => instance.Version);
                version.CurrentValue = version.OriginalValue + 1;
            }
        }

        foreach (var entry in ChangeTracker.Entries<WorkloadGroup>())
        {
            if (entry.State == EntityState.Modified)
            {
                var version = entry.Property(module => module.Version);
                version.CurrentValue = version.OriginalValue + 1;
            }
        }
    }
}
