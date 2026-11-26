using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reeve.Workloads.EntityFrameworkCore.PostgreSql.Migrations;

/// <inheritdoc />
public partial class InitialWorkloadsSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "workloads");

        migrationBuilder.CreateTable(
            name: "retained_volumes",
            schema: "workloads",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                module_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                workload_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                volume_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                resource_name = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                size = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                criticality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                volume_naming_scheme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Kubernetes"),
                source_module_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                source_workload_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                retained_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                reattached_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                purged_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                claimed_by_workload_instance_id = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_retained_volumes", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "workload_installation",
            schema: "workloads",
            columns: table => new
            {
                id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                installation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                minted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workload_installation", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "workload_instances",
            schema: "workloads",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                module_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                module_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                workload_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                state_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                state_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                desired_spec_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                applied_spec_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                pending_scan_run_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                last_scan_failed_nodes = table.Column<string[]>(type: "text[]", nullable: true),
                provisioning_percent = table.Column<int>(type: "integer", nullable: true),
                observe_only = table.Column<bool>(type: "boolean", nullable: false),
                restart_count = table.Column<int>(type: "integer", nullable: false),
                restart_generation = table.Column<long>(type: "bigint", nullable: false),
                retention = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                reference_rollout_state = table.Column<string>(type: "text", nullable: true),
                restore_binding_unresolved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                last_healthy_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                last_transition_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                deploy_started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                last_rejected_apply_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false),
                attachment_pin_document = table.Column<string>(type: "jsonb", nullable: true),
                parameters_document = table.Column<string>(type: "jsonb", nullable: true),
                retained_volumes_snapshot_document = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workload_instances", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "workload_modules",
            schema: "workloads",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                module_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                source_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                installed_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workload_modules", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "workload_update_intents",
            schema: "workloads",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                module_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                cohort_id = table.Column<Guid>(type: "uuid", nullable: true),
                cohort_ordinal = table.Column<int>(type: "integer", nullable: true),
                cohort_size = table.Column<int>(type: "integer", nullable: true),
                from_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                to_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                phase_entered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                sidecar_applied_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                adopted_resource_workload_ids = table.Column<string[]>(type: "text[]", nullable: true),
                walk_fault = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                walk_fault_since_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                version = table.Column<int>(type: "integer", nullable: false),
                candidate_password_parameter_names_document = table.Column<string>(type: "jsonb", nullable: true),
                evidence_document = table.Column<string>(type: "jsonb", nullable: true),
                from_digests_document = table.Column<string>(type: "jsonb", nullable: true),
                operator_parameters_document = table.Column<string>(type: "jsonb", nullable: true),
                previous_parameters_document = table.Column<string>(type: "jsonb", nullable: true),
                previous_password_parameter_names_document = table.Column<string>(type: "jsonb", nullable: true),
                previous_specs_document = table.Column<string>(type: "jsonb", nullable: true),
                stage_journal_document = table.Column<string>(type: "jsonb", nullable: true),
                target_specs_document = table.Column<string>(type: "jsonb", nullable: true),
                to_digests_document = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workload_update_intents", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "workload_update_intent_steps",
            schema: "workloads",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                workload_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                step = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                applied_spec_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                gate_clock_state_document = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workload_update_intent_steps", x => x.id);
                table.ForeignKey(
                    name: "FK_workload_update_intent_steps_workload_update_intents_intent~",
                    column: x => x.intent_id,
                    principalSchema: "workloads",
                    principalTable: "workload_update_intents",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_retained_volumes_claimed_by_workload_instance_id",
            schema: "workloads",
            table: "retained_volumes",
            column: "claimed_by_workload_instance_id");

        migrationBuilder.CreateIndex(
            name: "ix_retained_volumes_module_id_workload_id",
            schema: "workloads",
            table: "retained_volumes",
            columns: new[] { "module_id", "workload_id" });

        migrationBuilder.CreateIndex(
            name: "ix_retained_volumes_retained_at_utc",
            schema: "workloads",
            table: "retained_volumes",
            column: "retained_at_utc");

        migrationBuilder.CreateIndex(
            name: "ix_retained_volumes_source_workload_instance_id_volume_id",
            schema: "workloads",
            table: "retained_volumes",
            columns: new[] { "source_workload_instance_id", "volume_id" },
            unique: true,
            filter: "\"reattached_at_utc\" IS NULL AND \"purged_at_utc\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "ux_retained_volumes_active_resource_name",
            schema: "workloads",
            table: "retained_volumes",
            column: "resource_name",
            unique: true,
            filter: "\"reattached_at_utc\" IS NULL AND \"purged_at_utc\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "ix_workload_instances_module_instance_id_workload_id",
            schema: "workloads",
            table: "workload_instances",
            columns: new[] { "module_instance_id", "workload_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_workload_instances_state",
            schema: "workloads",
            table: "workload_instances",
            column: "state");

        migrationBuilder.CreateIndex(
            name: "ux_workload_update_intent_steps_intent_id_workload_id",
            schema: "workloads",
            table: "workload_update_intent_steps",
            columns: new[] { "intent_id", "workload_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_workload_update_intents_phase",
            schema: "workloads",
            table: "workload_update_intents",
            column: "phase");

        migrationBuilder.CreateIndex(
            name: "ux_workload_update_intents_active_module_instance_id",
            schema: "workloads",
            table: "workload_update_intents",
            column: "module_instance_id",
            unique: true,
            filter: "\"phase\" NOT IN ('Completed', 'RolledBack', 'RollbackFailed')");

        migrationBuilder.CreateIndex(
            name: "ux_workload_update_intents_cohort_id_cohort_ordinal",
            schema: "workloads",
            table: "workload_update_intents",
            columns: new[] { "cohort_id", "cohort_ordinal" },
            unique: true);

        migrationBuilder.Sql(
            "CREATE UNIQUE INDEX \"IX_workload_modules_lower_module_id\" ON workloads.workload_modules (lower(module_id));");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "retained_volumes",
            schema: "workloads");

        migrationBuilder.DropTable(
            name: "workload_installation",
            schema: "workloads");

        migrationBuilder.DropTable(
            name: "workload_instances",
            schema: "workloads");

        migrationBuilder.DropTable(
            name: "workload_modules",
            schema: "workloads");

        migrationBuilder.DropTable(
            name: "workload_update_intent_steps",
            schema: "workloads");

        migrationBuilder.DropTable(
            name: "workload_update_intents",
            schema: "workloads");
    }
}
