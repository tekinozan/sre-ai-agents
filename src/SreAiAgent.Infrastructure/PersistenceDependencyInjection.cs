using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm.Providers;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Outbox;
using SreAiAgent.Infrastructure.Persistence.Queries;
using SreAiAgent.Infrastructure.Persistence.Repositories;
using SreAiAgent.Infrastructure.Registries;

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

            // Identity resolution keeps a no-tracking query from materializing the
            // same row as several distinct objects, which would otherwise make
            // reference equality quietly wrong in a projection that joins.
            //
            // Set here rather than in the context's OnConfiguring: a pooled
            // context refuses to have its options modified on activation, and the
            // refusal surfaces as an exception on the first query rather than at
            // startup.
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution);

            // Lazy loading is not enabled anywhere, and the proxies package is
            // deliberately not referenced: it turns a report projection into one
            // query per evidence row with nothing at the call site hinting so.

            // Detailed errors include parameter values, which for this schema
            // means evidence content and alert labels. Off everywhere.
            options.EnableDetailedErrors(false);
            options.EnableSensitiveDataLogging(false);
        });

        // Registered before everything else so the schema exists before any
        // other hosted service touches it.
        services.AddHostedService<DatabaseMigrator>();
        services.AddHostedService<SreAiAgent.Infrastructure.Llm.ModelAssignmentBootstrapper>();

        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddScoped<IIncidentRepository, IncidentRepository>();
        services.TryAddScoped<IInvestigationStore, InvestigationStore>();
        services.TryAddScoped<IIncidentQueries, IncidentQueries>();
        services.TryAddScoped<IApprovalStore, ApprovalStore>();
        services.TryAddScoped<IConfigurationSnapshotStore, ConfigurationSnapshotStore>();
        services.TryAddScoped<IIdempotencyStore, IdempotencyStore>();
        services.TryAddScoped<IInboxStore, InboxStore>();
        services.TryAddScoped<IOutboxStore, OutboxStore>();
        services.TryAddScoped<IEventPublisher, EventPublisher>();
        services.TryAddScoped<ModelProviderRepository>();
        services.TryAddScoped<IProviderRevisionReader>(provider => provider.GetRequiredService<ModelProviderRepository>());
        services.TryAddScoped<IConfigurationPinner, ConfigurationPinner>();

        return services;
    }
}
