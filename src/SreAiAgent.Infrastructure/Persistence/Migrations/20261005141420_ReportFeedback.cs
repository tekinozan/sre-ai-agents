using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SreAiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "report_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_version = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    verdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actual_root_cause = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    submitted_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_feedback", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_report_feedback_tenant_submitted",
                table: "report_feedback",
                columns: new[] { "tenant", "submitted_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_report_feedback_report_actor",
                table: "report_feedback",
                columns: new[] { "incident_id", "report_version", "submitted_by" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_feedback");
        }
    }
}
