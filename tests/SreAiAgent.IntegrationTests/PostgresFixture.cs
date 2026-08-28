using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// A real PostgreSQL for the suite, migrated once.
/// </summary>
/// <remarks>
/// The container runs the same image the compose stack does. A different major
/// version would let a test pass against behaviour the platform never sees, and
/// the things under test here, SKIP LOCKED and partial indexes among them, are
/// exactly where versions differ.
///
/// Migrations are applied rather than EnsureCreated. What has to work in
/// production is the migration, so the schema the tests run against is the one a
/// deployment produces, including anything a migration got wrong.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("sre")
        .WithUsername("sre")
        .WithPassword("sre")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);

        await using SreDbContext context = CreateContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// A context bound to the container.
    /// </summary>
    /// <remarks>
    /// Not pooled, and one per caller. Several of these tests hold two contexts
    /// open at once on purpose, because that is the only way to observe two
    /// transactions racing, which is the whole subject.
    /// </remarks>
    public SreDbContext CreateContext()
    {
        DbContextOptionsBuilder<SreDbContext> options = new();

        options.UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__migrations_history"));
        options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution);

        return new SreDbContext(options.Options);
    }

    public static IOptions<DatabaseOptions> DatabaseOptions(string connectionString)
        => Microsoft.Extensions.Options.Options.Create(new DatabaseOptions
        {
            ConnectionString = connectionString,
            IncidentDeduplicationWindowMinutes = 60,
        });
}

// CA1711 objects to the Collection suffix, which xunit requires here: the type
// exists only to name a collection.
#pragma warning disable CA1711

/// <summary>Shares one container across the suite.</summary>
/// <remarks>
/// Starting PostgreSQL costs seconds; doing it per class would dominate the run
/// and tempt someone to delete the suite. The tests clean up after themselves
/// instead.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
#pragma warning restore CA1711
