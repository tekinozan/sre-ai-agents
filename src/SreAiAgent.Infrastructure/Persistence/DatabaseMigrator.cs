using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Persistence;

/// <summary>
/// Applies pending migrations at startup when configuration asks for it.
/// </summary>
/// <remarks>
/// Off by default and off in production. A process that migrates its own schema
/// on boot is convenient on a laptop and dangerous in a cluster: several replicas
/// start at once, each tries to take the migration lock, and a rollout that was
/// meant to be gradual becomes a schema change racing a deployment. Production
/// migrations are a deliberate step in the pipeline, which is also the only way
/// to review the SQL before it runs.
///
/// Runs as a hosted service so the schema exists before the first request is
/// served, and fails the host if it cannot: an API answering queries against a
/// schema that was never created produces a five-hundred per request with a
/// cause several layers away from the symptom.
/// </remarks>
internal sealed partial class DatabaseMigrator(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseMigrator> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.MigrateOnStartup)
        {
            return;
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        SreDbContext context = scope.ServiceProvider.GetRequiredService<SreDbContext>();

        LogMigrating(logger);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Information,
        Message = "Applying pending database migrations.")]
    private static partial void LogMigrating(ILogger logger);
}
