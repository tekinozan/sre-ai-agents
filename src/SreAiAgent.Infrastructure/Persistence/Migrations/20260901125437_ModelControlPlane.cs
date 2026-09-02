using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SreAiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "model_release",
                table: "configuration_snapshots");

            migrationBuilder.RenameColumn(
                name: "model_provider",
                table: "llm_calls",
                newName: "provider_revision_hash");

            migrationBuilder.RenameColumn(
                name: "model_name",
                table: "llm_calls",
                newName: "tenant");

            migrationBuilder.RenameColumn(
                name: "model_profile_version",
                table: "agent_runs",
                newName: "model_deployment_version");

            migrationBuilder.AddColumn<string>(
                name: "assignment_hash",
                table: "llm_calls",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "assignment_release_id",
                table: "llm_calls",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "deployment_hash",
                table: "llm_calls",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "deployment_version_id",
                table: "llm_calls",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "finish_reason",
                table: "llm_calls",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "observed_model",
                table: "llm_calls",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "observed_provider",
                table: "llm_calls",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "provider_request_id",
                table: "llm_calls",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "provider_revision_id",
                table: "llm_calls",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "requested_model",
                table: "llm_calls",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "requested_provider",
                table: "llm_calls",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "model_assignment_json",
                table: "configuration_snapshots",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "model_control_plane_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_control_plane_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_assignment_agent_slots",
                columns: table => new
                {
                    assignment_release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    agent_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    deployment_version_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_assignment_agent_slots", x => new { x.tenant, x.assignment_release_id, x.agent_name });
                });

            migrationBuilder.CreateTable(
                name: "model_assignment_evaluations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    assignment_release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    baseline = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    passed = table.Column<bool>(type: "boolean", nullable: false),
                    artifact_hash = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_assignment_evaluations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_assignment_releases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    classifier_deployment_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlator_deployment_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_assignment_releases", x => x.id);
                    table.UniqueConstraint("AK_model_assignment_releases_tenant_id", x => new { x.tenant, x.id });
                });

            migrationBuilder.CreateTable(
                name: "tenant_active_model_assignments",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    assignment_release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    activated_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_active_model_assignments", x => x.tenant);
                    table.ForeignKey(
                        name: "FK_tenant_active_model_assignments_model_assignment_releases_t~",
                        columns: x => new { x.tenant, x.assignment_release_id },
                        principalTable: "model_assignment_releases",
                        principalColumns: new[] { "tenant", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "model_deployment_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    logical_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    provider_connection_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_model = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    required_capabilities = table.Column<int>(type: "integer", nullable: false),
                    temperature = table.Column<float>(type: "real", nullable: false),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: false),
                    timeout_milliseconds = table.Column<int>(type: "integer", nullable: false),
                    input_cost_per_million_tokens = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    output_cost_per_million_tokens = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    provider_parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_deployment_versions", x => x.id);
                    table.UniqueConstraint("AK_model_deployment_versions_tenant_id", x => new { x.tenant, x.id });
                });

            migrationBuilder.CreateTable(
                name: "provider_connection_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    adapter_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    region = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    api_version = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    organization = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    credential_ref = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    declared_capabilities = table.Column<int>(type: "integer", nullable: false),
                    configuration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_connection_revisions", x => x.id);
                    table.UniqueConstraint("AK_provider_connection_revisions_tenant_id", x => new { x.tenant, x.id });
                });

            migrationBuilder.CreateTable(
                name: "provider_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    active_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_connections", x => x.id);
                    table.UniqueConstraint("AK_provider_connections_tenant_id", x => new { x.tenant, x.id });
                    table.ForeignKey(
                        name: "FK_provider_connections_provider_connection_revisions_tenant_a~",
                        columns: x => new { x.tenant, x.active_revision_id },
                        principalTable: "provider_connection_revisions",
                        principalColumns: new[] { "tenant", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_revision_verifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_connection_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verified_capabilities = table.Column<int>(type: "integer", nullable: false),
                    request_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    verified_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_revision_verifications", x => x.id);
                    table.ForeignKey(
                        name: "FK_provider_revision_verifications_provider_connection_revisio~",
                        columns: x => new { x.tenant, x.provider_connection_revision_id },
                        principalTable: "provider_connection_revisions",
                        principalColumns: new[] { "tenant", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_assignment_agent_slots_tenant_deployment_version_id",
                table: "model_assignment_agent_slots",
                columns: new[] { "tenant", "deployment_version_id" });

            migrationBuilder.CreateIndex(
                name: "IX_model_assignment_evaluations_tenant_assignment_release_id_r~",
                table: "model_assignment_evaluations",
                columns: new[] { "tenant", "assignment_release_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "IX_model_assignment_releases_tenant_classifier_deployment_vers~",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "classifier_deployment_version_id" });

            migrationBuilder.CreateIndex(
                name: "IX_model_assignment_releases_tenant_correlator_deployment_vers~",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "correlator_deployment_version_id" });

            migrationBuilder.CreateIndex(
                name: "IX_model_assignment_releases_tenant_name_version",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "name", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_control_plane_audit_tenant_occurred_at",
                table: "model_control_plane_audit",
                columns: new[] { "tenant", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_model_deployment_versions_tenant_logical_name_version",
                table: "model_deployment_versions",
                columns: new[] { "tenant", "logical_name", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_deployment_versions_tenant_provider_connection_revisi~",
                table: "model_deployment_versions",
                columns: new[] { "tenant", "provider_connection_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_connection_revisions_tenant_provider_connection_id~",
                table: "provider_connection_revisions",
                columns: new[] { "tenant", "provider_connection_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_connections_tenant_active_revision_id",
                table: "provider_connections",
                columns: new[] { "tenant", "active_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_connections_tenant_display_name",
                table: "provider_connections",
                columns: new[] { "tenant", "display_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_revision_verifications_tenant_provider_connection_~",
                table: "provider_revision_verifications",
                columns: new[] { "tenant", "provider_connection_revision_id", "verified_at" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_active_model_assignments_tenant_assignment_release_id",
                table: "tenant_active_model_assignments",
                columns: new[] { "tenant", "assignment_release_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_model_assignment_agent_slots_model_assignment_releases_tena~",
                table: "model_assignment_agent_slots",
                columns: new[] { "tenant", "assignment_release_id" },
                principalTable: "model_assignment_releases",
                principalColumns: new[] { "tenant", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_model_assignment_agent_slots_model_deployment_versions_tena~",
                table: "model_assignment_agent_slots",
                columns: new[] { "tenant", "deployment_version_id" },
                principalTable: "model_deployment_versions",
                principalColumns: new[] { "tenant", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_model_assignment_evaluations_model_assignment_releases_tena~",
                table: "model_assignment_evaluations",
                columns: new[] { "tenant", "assignment_release_id" },
                principalTable: "model_assignment_releases",
                principalColumns: new[] { "tenant", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_model_assignment_releases_model_deployment_versions_tenant_~",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "classifier_deployment_version_id" },
                principalTable: "model_deployment_versions",
                principalColumns: new[] { "tenant", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_model_assignment_releases_model_deployment_versions_tenant~1",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "correlator_deployment_version_id" },
                principalTable: "model_deployment_versions",
                principalColumns: new[] { "tenant", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_model_deployment_versions_provider_connection_revisions_ten~",
                table: "model_deployment_versions",
                columns: new[] { "tenant", "provider_connection_revision_id" },
                principalTable: "provider_connection_revisions",
                principalColumns: new[] { "tenant", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_provider_connection_revisions_provider_connections_tenant_p~",
                table: "provider_connection_revisions",
                columns: new[] { "tenant", "provider_connection_id" },
                principalTable: "provider_connections",
                principalColumns: new[] { "tenant", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE FUNCTION sre_reject_row_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'MODEL_CONFIGURATION_INVALID: % rows are immutable', TG_TABLE_NAME;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("CREATE TRIGGER prevent_mutation BEFORE UPDATE OR DELETE ON provider_connection_revisions FOR EACH ROW EXECUTE FUNCTION sre_reject_row_mutation();");
            migrationBuilder.Sql("CREATE TRIGGER prevent_mutation BEFORE UPDATE OR DELETE ON model_deployment_versions FOR EACH ROW EXECUTE FUNCTION sre_reject_row_mutation();");
            migrationBuilder.Sql("CREATE TRIGGER prevent_mutation BEFORE UPDATE OR DELETE ON model_assignment_releases FOR EACH ROW EXECUTE FUNCTION sre_reject_row_mutation();");
            migrationBuilder.Sql("CREATE TRIGGER prevent_mutation BEFORE UPDATE OR DELETE ON provider_revision_verifications FOR EACH ROW EXECUTE FUNCTION sre_reject_row_mutation();");
            migrationBuilder.Sql("CREATE TRIGGER prevent_mutation BEFORE UPDATE ON model_assignment_agent_slots FOR EACH ROW EXECUTE FUNCTION sre_reject_row_mutation();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_provider_connections_provider_connection_revisions_tenant_a~",
                table: "provider_connections");

            migrationBuilder.DropTable(
                name: "model_assignment_agent_slots");

            migrationBuilder.DropTable(
                name: "model_assignment_evaluations");

            migrationBuilder.DropTable(
                name: "model_control_plane_audit");

            migrationBuilder.DropTable(
                name: "provider_revision_verifications");

            migrationBuilder.DropTable(
                name: "tenant_active_model_assignments");

            migrationBuilder.DropTable(
                name: "model_assignment_releases");

            migrationBuilder.DropTable(
                name: "model_deployment_versions");

            migrationBuilder.DropTable(
                name: "provider_connection_revisions");

            migrationBuilder.DropTable(
                name: "provider_connections");

            migrationBuilder.DropColumn(
                name: "assignment_hash",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "assignment_release_id",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "deployment_hash",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "deployment_version_id",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "finish_reason",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "observed_model",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "observed_provider",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "provider_request_id",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "provider_revision_id",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "requested_model",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "requested_provider",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "model_assignment_json",
                table: "configuration_snapshots");

            migrationBuilder.RenameColumn(
                name: "tenant",
                table: "llm_calls",
                newName: "model_name");

            migrationBuilder.RenameColumn(
                name: "provider_revision_hash",
                table: "llm_calls",
                newName: "model_provider");

            migrationBuilder.RenameColumn(
                name: "model_deployment_version",
                table: "agent_runs",
                newName: "model_profile_version");

            migrationBuilder.AddColumn<string>(
                name: "model_release",
                table: "configuration_snapshots",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS prevent_mutation ON model_assignment_agent_slots;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS prevent_mutation ON provider_revision_verifications;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS prevent_mutation ON model_assignment_releases;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS prevent_mutation ON model_deployment_versions;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS prevent_mutation ON provider_connection_revisions;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS sre_reject_row_mutation();");
        }
    }
}
