using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SreAiAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Makes ix_incidents_active_fingerprint unique and adds the index the
    /// cooldown half of the fingerprint lookup reads (D43, BUG-001).
    ///
    /// Existing data may already hold two active incidents for one alert
    /// identity; that is the defect this closes. The migration refuses to run
    /// rather than pick a survivor: which of two open incidents to cancel is an
    /// operator's call, and the error names the identity so it can be made.
    /// </summary>
    public partial class IncidentFingerprintUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    duplicate record;
                BEGIN
                    SELECT tenant, source, fingerprint, count(*) AS active
                    INTO duplicate
                    FROM incidents
                    WHERE status NOT IN ('Resolved', 'Cancelled')
                    GROUP BY tenant, source, fingerprint
                    HAVING count(*) > 1
                    LIMIT 1;

                    IF FOUND THEN
                        RAISE EXCEPTION
                            'Cannot make ix_incidents_active_fingerprint unique: tenant %, source %, fingerprint % has % active incidents. Cancel or resolve all but one, then rerun the migration.',
                            duplicate.tenant, duplicate.source, duplicate.fingerprint, duplicate.active;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_incidents_active_fingerprint",
                table: "incidents");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_active_fingerprint",
                table: "incidents",
                columns: new[] { "tenant", "source", "fingerprint" },
                unique: true,
                filter: "status NOT IN ('Resolved', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_fingerprint_last_alert",
                table: "incidents",
                columns: new[] { "tenant", "source", "fingerprint", "last_alert_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_incidents_active_fingerprint",
                table: "incidents");

            migrationBuilder.DropIndex(
                name: "ix_incidents_fingerprint_last_alert",
                table: "incidents");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_active_fingerprint",
                table: "incidents",
                columns: new[] { "tenant", "source", "fingerprint" },
                filter: "status NOT IN ('Resolved', 'Cancelled')");
        }
    }
}
