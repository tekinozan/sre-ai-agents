using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SreAiAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds notification_channels and notification_deliveries (D48). Additive;
    /// a tenant with no channel is told nothing, as before.
    /// </summary>
    public partial class NotificationChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    webhook_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    events = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_channels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    delivery_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_deliveries", x => new { x.delivery_key, x.channel_id });
                });

            migrationBuilder.CreateIndex(
                name: "ux_notification_channels_tenant_name",
                table: "notification_channels",
                columns: new[] { "tenant", "display_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_claimed_at",
                table: "notification_deliveries",
                column: "claimed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_channels");

            migrationBuilder.DropTable(
                name: "notification_deliveries");
        }
    }
}
