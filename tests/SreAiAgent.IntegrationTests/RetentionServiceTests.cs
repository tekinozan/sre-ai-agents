using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Proves retention sweeps delete expired infrastructure rows only and preserve
/// in-flight counters and dead-letter evidence (ARCHITECTURE.md 10.2).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RetentionServiceTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RetentionSweepDeletesOnlyExpiredRowsAndPreservesInFlightAndDeadLetters()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string runId = Guid.CreateVersion7().ToString("N");

        RetentionOptions options = new()
        {
            Enabled = true,
            IntervalMinutes = 60,
            InboxRetentionDays = 14,
            DispatchedOutboxRetentionDays = 7,
            DeliveryAttemptRetentionDays = 7,
            BatchSize = 1000,
        };

        // Unique identifiers for this test run
        Guid inboxExpiredId = Guid.CreateVersion7();
        Guid inboxActiveId = Guid.CreateVersion7();

        Guid outboxDispatchedExpiredId = Guid.CreateVersion7();
        Guid outboxDispatchedActiveId = Guid.CreateVersion7();
        Guid outboxDeadLetterExpiredId = Guid.CreateVersion7();
        Guid outboxUndispatchedId = Guid.CreateVersion7();

        string keyExpired = $"idem:expired:{runId}";
        string keyActive = $"idem:active:{runId}";

        long offsetSettledExpired = Random.Shared.NextInt64(1, 1_000_000_000);
        long offsetSettledActive = Random.Shared.NextInt64(1, 1_000_000_000);
        long offsetInFlightExpired = Random.Shared.NextInt64(1, 1_000_000_000);

        string topic = $"topic-{runId}";
        string consumer = $"consumer-{runId}";

        // 1. Seed rows on both sides of every horizon
        await using (SreDbContext context = postgres.CreateContext())
        {
            // Inbox: expired (> 14 days) vs active (< 14 days)
            context.InboxMessages.AddRange(
                new InboxMessageEntity
                {
                    EventId = inboxExpiredId,
                    ConsumerName = consumer,
                    EventType = "test.event",
                    ProcessedAt = Now.AddDays(-15),
                },
                new InboxMessageEntity
                {
                    EventId = inboxActiveId,
                    ConsumerName = consumer,
                    EventType = "test.event",
                    ProcessedAt = Now.AddDays(-5),
                });

            // Outbox: dispatched expired (> 7 days), dispatched active (< 7 days),
            // dead-lettered expired (> 7 days, MUST SURVIVE), undispatched (MUST SURVIVE)
            context.OutboxMessages.AddRange(
                new OutboxMessageEntity
                {
                    Id = outboxDispatchedExpiredId,
                    Topic = topic,
                    EventType = "test.event",
                    EventId = Guid.CreateVersion7(),
                    PayloadJson = "{}",
                    PartitionKey = "key",
                    CorrelationId = "corr",
                    OccurredAt = Now.AddDays(-10),
                    DispatchedAt = Now.AddDays(-8),
                    AttemptCount = 1,
                    NextAttemptAt = Now.AddDays(-8),
                    IsDeadLettered = false,
                },
                new OutboxMessageEntity
                {
                    Id = outboxDispatchedActiveId,
                    Topic = topic,
                    EventType = "test.event",
                    EventId = Guid.CreateVersion7(),
                    PayloadJson = "{}",
                    PartitionKey = "key",
                    CorrelationId = "corr",
                    OccurredAt = Now.AddDays(-5),
                    DispatchedAt = Now.AddDays(-3),
                    AttemptCount = 1,
                    NextAttemptAt = Now.AddDays(-3),
                    IsDeadLettered = false,
                },
                new OutboxMessageEntity
                {
                    Id = outboxDeadLetterExpiredId,
                    Topic = topic,
                    EventType = "test.event",
                    EventId = Guid.CreateVersion7(),
                    PayloadJson = "{}",
                    PartitionKey = "key",
                    CorrelationId = "corr",
                    OccurredAt = Now.AddDays(-10),
                    DispatchedAt = Now.AddDays(-8),
                    AttemptCount = 5,
                    NextAttemptAt = Now.AddDays(-8),
                    IsDeadLettered = true, // Dead letter evidence must never be deleted
                },
                new OutboxMessageEntity
                {
                    Id = outboxUndispatchedId,
                    Topic = topic,
                    EventType = "test.event",
                    EventId = Guid.CreateVersion7(),
                    PayloadJson = "{}",
                    PartitionKey = "key",
                    CorrelationId = "corr",
                    OccurredAt = Now.AddDays(-10),
                    DispatchedAt = null,
                    AttemptCount = 0,
                    NextAttemptAt = Now,
                    IsDeadLettered = false,
                });

            // Idempotency: expired (expires_at <= now) vs active (expires_at > now)
            context.IdempotencyKeys.AddRange(
                new IdempotencyKeyEntity
                {
                    Key = keyExpired,
                    ClaimedAt = Now.AddDays(-2),
                    ExpiresAt = Now.AddDays(-1),
                },
                new IdempotencyKeyEntity
                {
                    Key = keyActive,
                    ClaimedAt = Now.AddDays(-1),
                    ExpiresAt = Now.AddDays(1),
                });

            // Delivery attempts:
            // settled expired (> 7 days), settled active (< 7 days),
            // unsettled / in-flight (> 7 days, MUST SURVIVE so poison counter is not reset)
            context.ConsumerDeliveryAttempts.AddRange(
                new ConsumerDeliveryAttemptEntity
                {
                    SourceTopic = topic,
                    SourcePartition = 0,
                    SourceOffset = offsetSettledExpired,
                    ConsumerName = consumer,
                    AttemptCount = 2,
                    FirstFailedAt = Now.AddDays(-10),
                    LastFailedAt = Now.AddDays(-9),
                    SettledAt = Now.AddDays(-8),
                    SettlementKind = "succeeded",
                },
                new ConsumerDeliveryAttemptEntity
                {
                    SourceTopic = topic,
                    SourcePartition = 0,
                    SourceOffset = offsetSettledActive,
                    ConsumerName = consumer,
                    AttemptCount = 1,
                    FirstFailedAt = Now.AddDays(-3),
                    LastFailedAt = Now.AddDays(-3),
                    SettledAt = Now.AddDays(-2),
                    SettlementKind = "succeeded",
                },
                new ConsumerDeliveryAttemptEntity
                {
                    SourceTopic = topic,
                    SourcePartition = 0,
                    SourceOffset = offsetInFlightExpired,
                    ConsumerName = consumer,
                    AttemptCount = 3,
                    FirstFailedAt = Now.AddDays(-10),
                    LastFailedAt = Now.AddDays(-8),
                    SettledAt = null, // In-flight counter must survive!
                    SettlementKind = null,
                });

            await context.SaveChangesAsync(ct);
        }

        // 2. Run retention sweep
        ServiceCollection services = new();
        services.AddScoped(_ => postgres.CreateContext());
        ServiceProvider serviceProvider = services.BuildServiceProvider();

        TestTimeProvider timeProvider = new(Now);
        RetentionService retention = new(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            timeProvider,
            NullLogger<RetentionService>.Instance);

        await retention.SweepAsync(ct);

        // 3. Assert exact survival / deletion rules
        await using (SreDbContext verifyContext = postgres.CreateContext())
        {
            // Inbox assertions
            (await verifyContext.InboxMessages.AnyAsync(m => m.EventId == inboxExpiredId, ct)).ShouldBeFalse();
            (await verifyContext.InboxMessages.AnyAsync(m => m.EventId == inboxActiveId, ct)).ShouldBeTrue();

            // Outbox assertions
            (await verifyContext.OutboxMessages.AnyAsync(m => m.Id == outboxDispatchedExpiredId, ct)).ShouldBeFalse();
            (await verifyContext.OutboxMessages.AnyAsync(m => m.Id == outboxDispatchedActiveId, ct)).ShouldBeTrue();
            (await verifyContext.OutboxMessages.AnyAsync(m => m.Id == outboxDeadLetterExpiredId, ct)).ShouldBeTrue(
                "Dead-lettered outbox rows must never be deleted by retention.");
            (await verifyContext.OutboxMessages.AnyAsync(m => m.Id == outboxUndispatchedId, ct)).ShouldBeTrue();

            // Idempotency assertions
            (await verifyContext.IdempotencyKeys.AnyAsync(k => k.Key == keyExpired, ct)).ShouldBeFalse();
            (await verifyContext.IdempotencyKeys.AnyAsync(k => k.Key == keyActive, ct)).ShouldBeTrue();

            // Delivery attempts assertions
            (await verifyContext.ConsumerDeliveryAttempts.AnyAsync(
                a => a.SourceTopic == topic && a.SourceOffset == offsetSettledExpired, ct)).ShouldBeFalse();
            (await verifyContext.ConsumerDeliveryAttempts.AnyAsync(
                a => a.SourceTopic == topic && a.SourceOffset == offsetSettledActive, ct)).ShouldBeTrue();
            (await verifyContext.ConsumerDeliveryAttempts.AnyAsync(
                a => a.SourceTopic == topic && a.SourceOffset == offsetInFlightExpired, ct)).ShouldBeTrue(
                "In-flight delivery attempt counters must survive retention sweeps.");
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
