using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SreAiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConsumerDeliveryAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "consumer_delivery_attempts",
                columns: table => new
                {
                    source_topic = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_partition = table.Column<int>(type: "integer", nullable: false),
                    source_offset = table.Column<long>(type: "bigint", nullable: false),
                    consumer_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    first_failed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    last_failed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    settlement_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consumer_delivery_attempts", x => new { x.source_topic, x.source_partition, x.source_offset, x.consumer_name });
                });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_last_failed_at",
                table: "consumer_delivery_attempts",
                column: "last_failed_at");

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_settled_at",
                table: "consumer_delivery_attempts",
                column: "settled_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consumer_delivery_attempts");
        }
    }
}
