using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SreAiAgent.Infrastructure.Persistence;

/// <summary>
/// Builds a context for <c>dotnet ef</c> without starting the application.
/// </summary>
/// <remarks>
/// Migrations are generated against a connection string that is never used to
/// connect: EF Core needs a provider to know how to render SQL, not a reachable
/// server. Keeping this separate from the host means generating a migration does
/// not require the full options graph, a Kafka broker or a valid OIDC authority
/// to be present.
/// </remarks>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SreDbContext>
{
    public SreDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<SreDbContext> builder = new();

        builder.UseNpgsql(
            "Host=localhost;Database=sre_ai_agent_design_time;Username=postgres;Password=postgres",
            npgsql => npgsql.MigrationsHistoryTable("__migrations_history"));

        return new SreDbContext(builder.Options);
    }
}
