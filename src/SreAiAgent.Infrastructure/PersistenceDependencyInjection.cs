using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Outbox;
using SreAiAgent.Infrastructure.Persistence.Queries;
using SreAiAgent.Infrastructure.Persistence.Repositories;

namespace SreAiAgent.Infrastructure;

/// <summary>Registers the PostgreSQL business store (ARCHITECTURE.md 9).</summary>
public static class PersistenceDependencyInjection
{
    /// <summary>
    /// Adds the pooled DbContext, repositories, read-side queries and the
    /// outbox/inbox stores.
    /// </summary>
    public static IServiceCollection AddSrePersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Pooled: constructing a DbContext allocates a model reference, a change
        // tracker and a state manager, and the alert path builds one per message.
        // Pooling resets and reuses them instead.
        services.AddDbContextPool<SreDbContext>((provider, options) =>
        {
            DatabaseOptions database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            options.UseNpgsql(database.ConnectionString, npgsql =>
            {
                npgsql.CommandTimeout(database.CommandTimeoutSeconds);

                // Retry only on Npgsql's own transient classification. A blanket
                // retry would repeat a unique-violation, which is how the
                // deduplication index reports "this alert is a duplicate" and is
                // not a failure worth retrying.
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);

                npgsql.MigrationsHistoryTable("__migrations_history");
            });

            // Detailed errors include parameter values, which for this schema
            // means evidence content and alert labels. Off everywhere.
            options.EnableDetailedErrors(false);
            options.EnableSensitiveDataLogging(false);
        });

        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddScoped<IIncidentRepository, IncidentRepository>();
        services.TryAddScoped<IInvestigationStore, InvestigationStore>();
        services.TryAddScoped<IIncidentQueries, IncidentQueries>();
        services.TryAddScoped<IConfigurationSnapshotStore, ConfigurationSnapshotStore>();
        services.TryAddScoped<IIdempotencyStore, IdempotencyStore>();
        services.TryAddScoped<IInboxStore, InboxStore>();
        services.TryAddScoped<IOutboxStore, OutboxStore>();
        services.TryAddScoped<IEventPublisher, EventPublisher>();

        return services;
    }
}
