using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SreAiAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Widens every (Tenant, ...) composite key, foreign key and index in
    /// ModelProviderConfigurations.cs to (Tenant, Product, Environment, ...)
    /// (D40, F1-2). Every existing row gets Product = "" and Environment = ""
    /// (the column default, not a backfilled guess): a row written before
    /// this migration ran has nothing narrower to report, so it becomes
    /// TenantScope.TenantWide for its tenant and reads back unchanged by
    /// every query that still passes a bare tenant (F1-5 has not cut
    /// IModelProviderControlPlane's 13 members over to a real TenantScope
    /// yet). tenant_active_model_assignments' primary key widens from
    /// (Tenant) to (Tenant, Product, Environment), so "one active assignment
    /// per tenant" becomes "one active assignment per scope" as a
    /// schema-enforced invariant rather than an application check.
    /// model_control_plane_audit is untouched: it holds no alternate key
    /// anyone references and no foreign key into a widened principal key.
    /// </summary>
    public partial class TenantScopeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_model_assignment_agent_slots_model_assignment_releases_tena~",
                table: "model_assignment_agent_slots");

            migrationBuilder.DropForeignKey(
                name: "FK_model_assignment_agent_slots_model_deployment_versions_tena~",
                table: "model_assignment_agent_slots");

            migrationBuilder.DropForeignKey(
                name: "FK_model_assignment_evaluations_model_assignment_releases_tena~",
                table: "model_assignment_evaluations");

            migrationBuilder.DropForeignKey(
                name: "FK_model_assignment_releases_model_deployment_versions_tenant_~",
                table: "model_assignment_releases");

            migrationBuilder.DropForeignKey(
                name: "FK_model_assignment_releases_model_deployment_versions_tenant~1",
                table: "model_assignment_releases");

            migrationBuilder.DropForeignKey(
                name: "FK_model_deployment_versions_provider_connection_revisions_ten~",
                table: "model_deployment_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_provider_connection_revisions_provider_connections_tenant_p~",
                table: "provider_connection_revisions");

            migrationBuilder.DropForeignKey(
                name: "FK_provider_connections_provider_connection_revisions_tenant_a~",
                table: "provider_connections");

            migrationBuilder.DropForeignKey(
                name: "FK_provider_revision_verifications_provider_connection_revisio~",
                table: "provider_revision_verifications");

            migrationBuilder.DropForeignKey(
                name: "FK_tenant_active_model_assignments_model_assignment_releases_t~",
                table: "tenant_active_model_assignments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_tenant_active_model_assignments",
                table: "tenant_active_model_assignments");

            migrationBuilder.DropIndex(
                name: "IX_tenant_active_model_assignments_tenant_assignment_release_id",
                table: "tenant_active_model_assignments");

            migrationBuilder.DropIndex(
                name: "IX_provider_revision_verifications_tenant_provider_connection_~",
                table: "provider_revision_verifications");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_provider_connections_tenant_id",
                table: "provider_connections");

            migrationBuilder.DropIndex(
                name: "IX_provider_connections_tenant_active_revision_id",
                table: "provider_connections");

            migrationBuilder.DropIndex(
                name: "IX_provider_connections_tenant_display_name",
                table: "provider_connections");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_provider_connection_revisions_tenant_id",
                table: "provider_connection_revisions");

            migrationBuilder.DropIndex(
                name: "IX_provider_connection_revisions_tenant_provider_connection_id~",
                table: "provider_connection_revisions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_model_deployment_versions_tenant_id",
                table: "model_deployment_versions");

            migrationBuilder.DropIndex(
                name: "IX_model_deployment_versions_tenant_logical_name_version",
                table: "model_deployment_versions");

            migrationBuilder.DropIndex(
                name: "IX_model_deployment_versions_tenant_provider_connection_revisi~",
                table: "model_deployment_versions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_model_assignment_releases_tenant_id",
                table: "model_assignment_releases");

            migrationBuilder.DropIndex(
                name: "IX_model_assignment_releases_tenant_classifier_deployment_vers~",
                table: "model_assignment_releases");

            migrationBuilder.DropIndex(
                name: "IX_model_assignment_releases_tenant_correlator_deployment_vers~",
                table: "model_assignment_releases");

            migrationBuilder.DropIndex(
                name: "IX_model_assignment_releases_tenant_name_version",
                table: "model_assignment_releases");

            migrationBuilder.DropIndex(
                name: "IX_model_assignment_evaluations_tenant_assignment_release_id_r~",
                table: "model_assignment_evaluations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_model_assignment_agent_slots",
                table: "model_assignment_agent_slots");

            migrationBuilder.DropIndex(
                name: "IX_model_assignment_agent_slots_tenant_deployment_version_id",
                table: "model_assignment_agent_slots");

            migrationBuilder.AddColumn<string>(
                name: "product",
                table: "tenant_active_model_assignments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "environment",
                table: "tenant_active_model_assignments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "environment",
                table: "provider_revision_verifications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "product",
                table: "provider_revision_verifications",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "environment",
                table: "provider_connections",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "product",
                table: "provider_connections",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "environment",
                table: "provider_connection_revisions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "product",
                table: "provider_connection_revisions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "environment",
                table: "model_deployment_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "product",
                table: "model_deployment_versions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "environment",
                table: "model_assignment_releases",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "product",
                table: "model_assignment_releases",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "environment",
                table: "model_assignment_evaluations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "product",
                table: "model_assignment_evaluations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "product",
                table: "model_assignment_agent_slots",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "environment",
                table: "model_assignment_agent_slots",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_tenant_active_model_assignments",
                table: "tenant_active_model_assignments",
                columns: new[] { "tenant", "product", "environment" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_provider_connections_tenant_product_environment_id",
                table: "provider_connections",
                columns: new[] { "tenant", "product", "environment", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_provider_connection_revisions_tenant_product_environment_id",
                table: "provider_connection_revisions",
                columns: new[] { "tenant", "product", "environment", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_model_deployment_versions_tenant_product_environment_id",
                table: "model_deployment_versions",
                columns: new[] { "tenant", "product", "environment", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_model_assignment_releases_tenant_product_environment_id",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "product", "environment", "id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_model_assignment_agent_slots",
                table: "model_assignment_agent_slots",
                columns: new[] { "tenant", "product", "environment", "assignment_release_id", "agent_name" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_active_model_assignments_tenant_product_environment_~",
                table: "tenant_active_model_assignments",
                columns: new[] { "tenant", "product", "environment", "assignment_release_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_revision_verifications_tenant_product_environment_~",
                table: "provider_revision_verifications",
                columns: new[] { "tenant", "product", "environment", "provider_connection_revision_id", "verified_at" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_connections_tenant_product_environment_active_revi~",
                table: "provider_connections",
                columns: new[] { "tenant", "product", "environment", "active_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_connections_tenant_product_environment_display_name",
                table: "provider_connections",
                columns: new[] { "tenant", "product", "environment", "display_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_connection_revisions_tenant_product_environment_pr~",
                table: "provider_connection_revisions",
                columns: new[] { "tenant", "product", "environment", "provider_connection_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_deployment_versions_tenant_product_environment_logica~",
                table: "model_deployment_versions",
                columns: new[] { "tenant", "product", "environment", "logical_name", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_deployment_versions_tenant_product_environment_provid~",
                table: "model_deployment_versions",
                columns: new[] { "tenant", "product", "environment", "provider_connection_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_model_assignment_releases_tenant_product_environment_classi~",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "product", "environment", "classifier_deployment_version_id" });

            migrationBuilder.CreateIndex(
                name: "IX_model_assignment_releases_tenant_product_environment_correl~",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "product", "environment", "correlator_deployment_version_id" });

            migrationBuilder.CreateIndex(
                name: "IX_model_assignment_releases_tenant_product_environment_name_v~",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "product", "environment", "name", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_assignment_evaluations_tenant_product_environment_ass~",
                table: "model_assignment_evaluations",
                columns: new[] { "tenant", "product", "environment", "assignment_release_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "IX_model_assignment_agent_slots_tenant_product_environment_dep~",
                table: "model_assignment_agent_slots",
                columns: new[] { "tenant", "product", "environment", "deployment_version_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_model_assignment_agent_slots_model_assignment_releases_tena~",
                table: "model_assignment_agent_slots",
                columns: new[] { "tenant", "product", "environment", "assignment_release_id" },
                principalTable: "model_assignment_releases",
                principalColumns: new[] { "tenant", "product", "environment", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_model_assignment_agent_slots_model_deployment_versions_tena~",
                table: "model_assignment_agent_slots",
                columns: new[] { "tenant", "product", "environment", "deployment_version_id" },
                principalTable: "model_deployment_versions",
                principalColumns: new[] { "tenant", "product", "environment", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_model_assignment_evaluations_model_assignment_releases_tena~",
                table: "model_assignment_evaluations",
                columns: new[] { "tenant", "product", "environment", "assignment_release_id" },
                principalTable: "model_assignment_releases",
                principalColumns: new[] { "tenant", "product", "environment", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_model_assignment_releases_model_deployment_versions_tenant_~",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "product", "environment", "classifier_deployment_version_id" },
                principalTable: "model_deployment_versions",
                principalColumns: new[] { "tenant", "product", "environment", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_model_assignment_releases_model_deployment_versions_tenant~1",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "product", "environment", "correlator_deployment_version_id" },
                principalTable: "model_deployment_versions",
                principalColumns: new[] { "tenant", "product", "environment", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_model_deployment_versions_provider_connection_revisions_ten~",
                table: "model_deployment_versions",
                columns: new[] { "tenant", "product", "environment", "provider_connection_revision_id" },
                principalTable: "provider_connection_revisions",
                principalColumns: new[] { "tenant", "product", "environment", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_provider_connection_revisions_provider_connections_tenant_p~",
                table: "provider_connection_revisions",
                columns: new[] { "tenant", "product", "environment", "provider_connection_id" },
                principalTable: "provider_connections",
                principalColumns: new[] { "tenant", "product", "environment", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_provider_connections_provider_connection_revisions_tenant_p~",
                table: "provider_connections",
                columns: new[] { "tenant", "product", "environment", "active_revision_id" },
                principalTable: "provider_connection_revisions",
                principalColumns: new[] { "tenant", "product", "environment", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_provider_revision_verifications_provider_connection_revisio~",
                table: "provider_revision_verifications",
                columns: new[] { "tenant", "product", "environment", "provider_connection_revision_id" },
                principalTable: "provider_connection_revisions",
                principalColumns: new[] { "tenant", "product", "environment", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_tenant_active_model_assignments_model_assignment_releases_t~",
                table: "tenant_active_model_assignments",
                columns: new[] { "tenant", "product", "environment", "assignment_release_id" },
                principalTable: "model_assignment_releases",
                principalColumns: new[] { "tenant", "product", "environment", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_model_assignment_agent_slots_model_assignment_releases_tena~",
                table: "model_assignment_agent_slots");

            migrationBuilder.DropForeignKey(
                name: "FK_model_assignment_agent_slots_model_deployment_versions_tena~",
                table: "model_assignment_agent_slots");

            migrationBuilder.DropForeignKey(
                name: "FK_model_assignment_evaluations_model_assignment_releases_tena~",
                table: "model_assignment_evaluations");

            migrationBuilder.DropForeignKey(
                name: "FK_model_assignment_releases_model_deployment_versions_tenant_~",
                table: "model_assignment_releases");

            migrationBuilder.DropForeignKey(
                name: "FK_model_assignment_releases_model_deployment_versions_tenant~1",
                table: "model_assignment_releases");

            migrationBuilder.DropForeignKey(
                name: "FK_model_deployment_versions_provider_connection_revisions_ten~",
                table: "model_deployment_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_provider_connection_revisions_provider_connections_tenant_p~",
                table: "provider_connection_revisions");

            migrationBuilder.DropForeignKey(
                name: "FK_provider_connections_provider_connection_revisions_tenant_p~",
                table: "provider_connections");

            migrationBuilder.DropForeignKey(
                name: "FK_provider_revision_verifications_provider_connection_revisio~",
                table: "provider_revision_verifications");

            migrationBuilder.DropForeignKey(
                name: "FK_tenant_active_model_assignments_model_assignment_releases_t~",
                table: "tenant_active_model_assignments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_tenant_active_model_assignments",
                table: "tenant_active_model_assignments");

            migrationBuilder.DropIndex(
                name: "IX_tenant_active_model_assignments_tenant_product_environment_~",
                table: "tenant_active_model_assignments");

            migrationBuilder.DropIndex(
                name: "IX_provider_revision_verifications_tenant_product_environment_~",
                table: "provider_revision_verifications");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_provider_connections_tenant_product_environment_id",
                table: "provider_connections");

            migrationBuilder.DropIndex(
                name: "IX_provider_connections_tenant_product_environment_active_revi~",
                table: "provider_connections");

            migrationBuilder.DropIndex(
                name: "IX_provider_connections_tenant_product_environment_display_name",
                table: "provider_connections");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_provider_connection_revisions_tenant_product_environment_id",
                table: "provider_connection_revisions");

            migrationBuilder.DropIndex(
                name: "IX_provider_connection_revisions_tenant_product_environment_pr~",
                table: "provider_connection_revisions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_model_deployment_versions_tenant_product_environment_id",
                table: "model_deployment_versions");

            migrationBuilder.DropIndex(
                name: "IX_model_deployment_versions_tenant_product_environment_logica~",
                table: "model_deployment_versions");

            migrationBuilder.DropIndex(
                name: "IX_model_deployment_versions_tenant_product_environment_provid~",
                table: "model_deployment_versions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_model_assignment_releases_tenant_product_environment_id",
                table: "model_assignment_releases");

            migrationBuilder.DropIndex(
                name: "IX_model_assignment_releases_tenant_product_environment_classi~",
                table: "model_assignment_releases");

            migrationBuilder.DropIndex(
                name: "IX_model_assignment_releases_tenant_product_environment_correl~",
                table: "model_assignment_releases");

            migrationBuilder.DropIndex(
                name: "IX_model_assignment_releases_tenant_product_environment_name_v~",
                table: "model_assignment_releases");

            migrationBuilder.DropIndex(
                name: "IX_model_assignment_evaluations_tenant_product_environment_ass~",
                table: "model_assignment_evaluations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_model_assignment_agent_slots",
                table: "model_assignment_agent_slots");

            migrationBuilder.DropIndex(
                name: "IX_model_assignment_agent_slots_tenant_product_environment_dep~",
                table: "model_assignment_agent_slots");

            migrationBuilder.DropColumn(
                name: "product",
                table: "tenant_active_model_assignments");

            migrationBuilder.DropColumn(
                name: "environment",
                table: "tenant_active_model_assignments");

            migrationBuilder.DropColumn(
                name: "environment",
                table: "provider_revision_verifications");

            migrationBuilder.DropColumn(
                name: "product",
                table: "provider_revision_verifications");

            migrationBuilder.DropColumn(
                name: "environment",
                table: "provider_connections");

            migrationBuilder.DropColumn(
                name: "product",
                table: "provider_connections");

            migrationBuilder.DropColumn(
                name: "environment",
                table: "provider_connection_revisions");

            migrationBuilder.DropColumn(
                name: "product",
                table: "provider_connection_revisions");

            migrationBuilder.DropColumn(
                name: "environment",
                table: "model_deployment_versions");

            migrationBuilder.DropColumn(
                name: "product",
                table: "model_deployment_versions");

            migrationBuilder.DropColumn(
                name: "environment",
                table: "model_assignment_releases");

            migrationBuilder.DropColumn(
                name: "product",
                table: "model_assignment_releases");

            migrationBuilder.DropColumn(
                name: "environment",
                table: "model_assignment_evaluations");

            migrationBuilder.DropColumn(
                name: "product",
                table: "model_assignment_evaluations");

            migrationBuilder.DropColumn(
                name: "product",
                table: "model_assignment_agent_slots");

            migrationBuilder.DropColumn(
                name: "environment",
                table: "model_assignment_agent_slots");

            migrationBuilder.AddPrimaryKey(
                name: "PK_tenant_active_model_assignments",
                table: "tenant_active_model_assignments",
                column: "tenant");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_provider_connections_tenant_id",
                table: "provider_connections",
                columns: new[] { "tenant", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_provider_connection_revisions_tenant_id",
                table: "provider_connection_revisions",
                columns: new[] { "tenant", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_model_deployment_versions_tenant_id",
                table: "model_deployment_versions",
                columns: new[] { "tenant", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_model_assignment_releases_tenant_id",
                table: "model_assignment_releases",
                columns: new[] { "tenant", "id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_model_assignment_agent_slots",
                table: "model_assignment_agent_slots",
                columns: new[] { "tenant", "assignment_release_id", "agent_name" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_active_model_assignments_tenant_assignment_release_id",
                table: "tenant_active_model_assignments",
                columns: new[] { "tenant", "assignment_release_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_revision_verifications_tenant_provider_connection_~",
                table: "provider_revision_verifications",
                columns: new[] { "tenant", "provider_connection_revision_id", "verified_at" });

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
                name: "IX_provider_connection_revisions_tenant_provider_connection_id~",
                table: "provider_connection_revisions",
                columns: new[] { "tenant", "provider_connection_id", "revision" },
                unique: true);

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
                name: "IX_model_assignment_evaluations_tenant_assignment_release_id_r~",
                table: "model_assignment_evaluations",
                columns: new[] { "tenant", "assignment_release_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "IX_model_assignment_agent_slots_tenant_deployment_version_id",
                table: "model_assignment_agent_slots",
                columns: new[] { "tenant", "deployment_version_id" });

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

            migrationBuilder.AddForeignKey(
                name: "FK_provider_connections_provider_connection_revisions_tenant_a~",
                table: "provider_connections",
                columns: new[] { "tenant", "active_revision_id" },
                principalTable: "provider_connection_revisions",
                principalColumns: new[] { "tenant", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_provider_revision_verifications_provider_connection_revisio~",
                table: "provider_revision_verifications",
                columns: new[] { "tenant", "provider_connection_revision_id" },
                principalTable: "provider_connection_revisions",
                principalColumns: new[] { "tenant", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_tenant_active_model_assignments_model_assignment_releases_t~",
                table: "tenant_active_model_assignments",
                columns: new[] { "tenant", "assignment_release_id" },
                principalTable: "model_assignment_releases",
                principalColumns: new[] { "tenant", "id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
