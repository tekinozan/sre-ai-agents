using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Persistence;

/// <summary>
/// Periodically purges expired infrastructure records (ARCHITECTURE.md 10.2).
/// </summary>
/// <remarks>
/// Sweeps inbox_messages, outbox_messages (dispatched only; dead letters are kept as evidence),
/// idempotency_keys, consumer_delivery_attempts (settled only; in-flight counters must
/// never be reset mid-flight) and notification_deliveries.
///
/// Uses ctid subqueries with LIMIT because PostgreSQL does not support LIMIT on DELETE.
/// Statements loop while the affected count equals BatchSize so large backlogs are chunked
/// without holding long table locks.
/// </remarks>
internal sealed partial class RetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<RetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RetentionService> logger) : BackgroundService
{
    private readonly RetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromMinutes(_options.IntervalMinutes), timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        catch (Exception exception)
        {
            LogRetentionFailed(logger, exception);
        }
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset inboxCutoff = now.AddDays(-_options.InboxRetentionDays);
        DateTimeOffset outboxCutoff = now.AddDays(-_options.DispatchedOutboxRetentionDays);
        DateTimeOffset attemptCutoff = now.AddDays(-_options.DeliveryAttemptRetentionDays);
        DateTimeOffset claimCutoff = now.AddDays(-_options.NotificationClaimRetentionDays);
        int batchSize = _options.BatchSize;

        using IServiceScope scope = scopeFactory.CreateScope();
        SreDbContext context = scope.ServiceProvider.GetRequiredService<SreDbContext>();

        int totalInbox = 0;
        int totalOutbox = 0;
        int totalIdempotency = 0;
        int totalAttempts = 0;
        int totalClaims = 0;

        try
        {
            // 1. inbox: redelivery cannot arrive after topic retention has passed
            int deleted;
            do
            {
                deleted = await context.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM inbox_messages WHERE ctid IN (
                        SELECT ctid FROM inbox_messages WHERE processed_at < {inboxCutoff} LIMIT {batchSize})
                    """, cancellationToken).ConfigureAwait(false);
                totalInbox += deleted;
            } while (deleted == batchSize && !cancellationToken.IsCancellationRequested);

            // 2. outbox: dispatched rows only. Dead-lettered rows are evidence and are never deleted.
            do
            {
                deleted = await context.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM outbox_messages WHERE ctid IN (
                        SELECT ctid FROM outbox_messages
                        WHERE dispatched_at IS NOT NULL AND dispatched_at < {outboxCutoff} AND is_dead_lettered = false
                        LIMIT {batchSize})
                    """, cancellationToken).ConfigureAwait(false);
                totalOutbox += deleted;
            } while (deleted == batchSize && !cancellationToken.IsCancellationRequested);

            // 3. idempotency: expired keys are reclaimable and no longer needed
            do
            {
                deleted = await context.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM idempotency_keys WHERE ctid IN (
                        SELECT ctid FROM idempotency_keys WHERE expires_at <= {now} LIMIT {batchSize})
                    """, cancellationToken).ConfigureAwait(false);
                totalIdempotency += deleted;
            } while (deleted == batchSize && !cancellationToken.IsCancellationRequested);

            // 4. delivery attempts: settled counters only. In-flight counters must survive.
            do
            {
                deleted = await context.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM consumer_delivery_attempts WHERE ctid IN (
                        SELECT ctid FROM consumer_delivery_attempts
                        WHERE settled_at IS NOT NULL AND settled_at < {attemptCutoff}
                        LIMIT {batchSize})
                    """, cancellationToken).ConfigureAwait(false);
                totalAttempts += deleted;
            } while (deleted == batchSize && !cancellationToken.IsCancellationRequested);

            // 5. notification claims: a claim only has to outlive the redelivery
            // and the budget period that could repeat its notification.
            do
            {
                deleted = await context.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM notification_deliveries WHERE ctid IN (
                        SELECT ctid FROM notification_deliveries WHERE claimed_at < {claimCutoff} LIMIT {batchSize})
                    """, cancellationToken).ConfigureAwait(false);
                totalClaims += deleted;
            } while (deleted == batchSize && !cancellationToken.IsCancellationRequested);

            LogRetentionSweepCompleted(logger, totalInbox, totalOutbox, totalIdempotency, totalAttempts, totalClaims);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRetentionFailed(logger, exception);
        }
    }

    [LoggerMessage(
        EventId = 7501,
        Level = LogLevel.Information,
        Message = "Retention sweep completed: {InboxDeleted} inbox, {OutboxDeleted} outbox, {IdempotencyDeleted} idempotency, {AttemptsDeleted} delivery attempts, {ClaimsDeleted} notification claims deleted.")]
    private static partial void LogRetentionSweepCompleted(
        ILogger logger, int inboxDeleted, int outboxDeleted, int idempotencyDeleted, int attemptsDeleted, int claimsDeleted);

    [LoggerMessage(
        EventId = 7502,
        Level = LogLevel.Error,
        Message = "Retention sweep failed.")]
    private static partial void LogRetentionFailed(ILogger logger, Exception exception);
}
