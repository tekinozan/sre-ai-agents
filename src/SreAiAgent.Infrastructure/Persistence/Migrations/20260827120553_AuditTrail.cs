using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SreAiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditTrail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    prompt_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    prompt_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    duration_milliseconds = table.Column<int>(type: "integer", nullable: false),
                    called_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_calls", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tool_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tool_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    tool_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    agent_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    arguments_json = table.Column<string>(type: "jsonb", nullable: false),
                    duration_milliseconds = table.Column<int>(type: "integer", nullable: false),
                    executed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_calls", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_llm_calls_incident",
                table: "llm_calls",
                columns: new[] { "incident_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_llm_calls_prompt",
                table: "llm_calls",
                columns: new[] { "prompt_name", "prompt_version" });

            migrationBuilder.CreateIndex(
                name: "ix_tool_calls_denied",
                table: "tool_calls",
                columns: new[] { "tool_name", "executed_at" },
                filter: "outcome = 'denied'");

            migrationBuilder.CreateIndex(
                name: "ix_tool_calls_incident",
                table: "tool_calls",
                columns: new[] { "incident_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_calls");

            migrationBuilder.DropTable(
                name: "tool_calls");
        }
    }
}
