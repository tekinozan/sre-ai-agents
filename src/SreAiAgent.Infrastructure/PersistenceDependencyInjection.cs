using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm;
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
    /// Applies the database migrations using the same pooled context options as
    /// the running hosts.
    /// </summary>
    /// <remarks>
    /// The migration executable calls this rather than constructing a context of
    /// its own. Keeping the context internal prevents infrastructure details
    /// leaking into a host while one registration remains responsible for the
    /// Npgsql retry, timeout and migrations-history settings.
    /// </remarks>
    public static async Task MigrateSreDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        SreDbContext database = scope.ServiceProvider.GetRequiredService<SreDbContext>();
        await database.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds only the pooled DbContext required for migration execution.
    /// </summary>
    public static IServiceCollection AddSreDatabasePool(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptionsWithValidation<DatabaseOptions>(DatabaseOptions.SectionName);

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

        return services;
    }

    /// <summary>
    /// Adds the pooled DbContext, repositories, read-side queries and the
    /// outbox/inbox stores.
    /// </summary>
    public static IServiceCollection AddSrePersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSreDatabasePool();

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
        services.TryAddScoped<IDeliveryAttemptStore, DeliveryAttemptStore>();
        services.TryAddScoped<IOutboxStore, OutboxStore>();
        services.TryAddScoped<IEventPublisher, EventPublisher>();
        services.TryAddScoped<ModelProviderRepository>();
        services.TryAddScoped<TenantBudgetService>();
        services.TryAddScoped<ITenantBudgetService>(provider => provider.GetRequiredService<TenantBudgetService>());
        services.AddScoped<ITenantBudgetGuard>(provider => provider.GetRequiredService<TenantBudgetService>());
        services.TryAddScoped<IProviderRevisionReader>(provider => provider.GetRequiredService<ModelProviderRepository>());
        services.TryAddScoped<IConfigurationPinner, ConfigurationPinner>();

        return services;
    }

    /// <summary>
    /// Registers the retention sweep background service.
    /// </summary>
    /// <remarks>
    /// Called from the alert worker only; the investigation worker is left free of
    /// periodic sweep overhead because it is under model-call load. Every statement
    /// is a time-filtered delete, so a second replica running concurrently is harmless.
    /// </remarks>
    public static IServiceCollection AddSreRetention(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<RetentionService>();

        return services;
    }
}
