using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SreAiAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds tenant_budgets and the (tenant, called_at) index the budget guard
    /// reads before every model call (D47). Additive: no existing row changes,
    /// and a tenant with no budget row keeps having no ceiling.
    /// </summary>
    public partial class TenantBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_budgets",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    limit_amount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    period = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_budgets", x => x.tenant);
                });

            migrationBuilder.CreateIndex(
                name: "ix_llm_calls_tenant_called_at",
                table: "llm_calls",
                columns: new[] { "tenant", "called_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_budgets");

            migrationBuilder.DropIndex(
                name: "ix_llm_calls_tenant_called_at",
                table: "llm_calls");
        }
    }
}
