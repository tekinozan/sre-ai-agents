using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SreAiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EvaluationProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "completed_by",
                table: "model_assignment_evaluations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "release_configuration_hash",
                table: "model_assignment_evaluations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "completed_by",
                table: "model_assignment_evaluations");

            migrationBuilder.DropColumn(
                name: "release_configuration_hash",
                table: "model_assignment_evaluations");
        }
    }
}
