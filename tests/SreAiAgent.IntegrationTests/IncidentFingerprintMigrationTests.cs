using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Proves the upgrade that makes ix_incidents_active_fingerprint unique against
/// data written before it (docs/DECISIONS.md D43).
/// </summary>
/// <remarks>
/// The data this migration has to survive is exactly the data the defect
/// produced: two active incidents for one alert. Every other suite starts from a
/// migrated, empty schema and could never see it.
/// </remarks>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class IncidentFingerprintMigrationTests(PostgresFixture postgres)
{
    private const string PreMigrationTarget = "20260914080335_TenantScopeColumns";

    [Fact]
    public async Task TheUpgradeRefusesExistingDuplicatesAndNamesThemThenSucceedsOnceTheyAreResolved()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string databaseName = "fingerprint_" + Guid.NewGuid().ToString("N");
        string adminConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = "postgres",
        }.ConnectionString;
        string upgradeConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        try
        {
            await using (NpgsqlConnection admin = new(adminConnectionString))
            {
                await admin.OpenAsync(ct);
                await using NpgsqlCommand create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync(ct);
            }

            DbContextOptionsBuilder<SreDbContext> optionsBuilder = new();
            optionsBuilder.UseNpgsql(
                upgradeConnectionString, npgsql => npgsql.MigrationsHistoryTable("__migrations_history"));

            string fingerprint = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Incident first = NewIncident(fingerprint, now);
            Incident second = NewIncident(fingerprint, now.AddMinutes(5));

            await using (SreDbContext oldSchema = new(optionsBuilder.Options))
            {
                await oldSchema.GetService<IMigrator>().MigrateAsync(PreMigrationTarget, ct);

                // The incidents table is unchanged between the target and this
                // migration, so the current model can write the old shape.
                oldSchema.Incidents.AddRange(first, second);
                await oldSchema.SaveChangesAsync(ct);
            }

            await using (SreDbContext upgrading = new(optionsBuilder.Options))
            {
                PostgresException refused = await Should.ThrowAsync<PostgresException>(
                    async () => await upgrading.GetService<IMigrator>().MigrateAsync(cancellationToken: ct));

                // Named, so an operator can decide which of the two to close;
                // the migration never picks a survivor itself.
                refused.MessageText.ShouldContain(fingerprint);
                refused.MessageText.ShouldContain("2 active incidents");
            }

            await using (NpgsqlConnection connection = new(upgradeConnectionString))
            {
                await connection.OpenAsync(ct);
                await using NpgsqlCommand cancel = connection.CreateCommand();
                cancel.CommandText = "UPDATE incidents SET status = 'Cancelled' WHERE id = @id";
                cancel.Parameters.AddWithValue("id", second.Id.Value);
                (await cancel.ExecuteNonQueryAsync(ct)).ShouldBe(1);
            }

            await using (SreDbContext upgraded = new(optionsBuilder.Options))
            {
                await upgraded.GetService<IMigrator>().MigrateAsync(cancellationToken: ct);

                (await upgraded.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty();
                (await upgraded.Incidents.CountAsync(incident => incident.Fingerprint == fingerprint, ct)).ShouldBe(2);
            }
        }
        finally
        {
            // Npgsql pools connections by connection string; WITH (FORCE)
            // terminates any that are still open.
            await using NpgsqlConnection admin = new(adminConnectionString);
            await admin.OpenAsync(CancellationToken.None);
            await using NpgsqlCommand drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static Incident NewIncident(string fingerprint, DateTimeOffset now)
    {
        Incident incident = new(
            IncidentId.New(),
            new NewIncident(
                "acme",
                "alertmanager",
                "alert-1",
                fingerprint,
                "Checkout latency above threshold",
                null,
                "checkout",
                "production",
                IncidentSeverity.High,
                now),
            now);
        incident.DequeueEvents();
        return incident;
    }
}
