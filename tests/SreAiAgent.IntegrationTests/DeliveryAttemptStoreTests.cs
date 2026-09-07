using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;
using SreAiAgent.Infrastructure.Persistence.Repositories;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Proves durable attempt counting and settlement across transaction boundaries.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryAttemptStoreTests(PostgresFixture postgres)
{
    private const string Topic = "sre.alert.received.v1";
    private const int Partition = 1;
    private const string ConsumerName = "alert-worker";

    [Fact]
    public async Task FailureCounterSurvivesRolledBackCallerTransaction()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        long offset = Random.Shared.NextInt64(1, 1_000_000_000);

        // Simulate handler attempting work inside a transaction that rolls back:
        // Because DeliveryAttemptStore is used from a separate scope/context,
        // its failure recording commits independently of the caller's transaction.
        await using (SreDbContext callerContext = postgres.CreateContext())
        await using (IDbContextTransaction transaction = await callerContext.Database.BeginTransactionAsync(ct))
        {
            // The handler's independent failure path creates its own scope/context:
            await using (SreDbContext failureContext = postgres.CreateContext())
            {
                DeliveryAttemptStore store = new(failureContext, TimeProvider.System);
                int firstAttempt = await store.RecordFailureAsync(
                    Topic, Partition, offset, ConsumerName,
                    Guid.CreateVersion7(), "sre.alert.received", "Transient connection failure", ct);

                firstAttempt.ShouldBe(1);
            }

            // Caller transaction rolls back
            await transaction.RollbackAsync(ct);
        }

        // On redelivery, the next failure recording observes the incremented counter
        await using (SreDbContext verifyContext = postgres.CreateContext())
        {
            DeliveryAttemptStore store = new(verifyContext, TimeProvider.System);
            int secondAttempt = await store.RecordFailureAsync(
                Topic, Partition, offset, ConsumerName,
                Guid.CreateVersion7(), "sre.alert.received", "Second connection failure", ct);

            secondAttempt.ShouldBe(2);

            ConsumerDeliveryAttemptEntity? entity = await verifyContext.ConsumerDeliveryAttempts
                .FirstOrDefaultAsync(a => a.SourceTopic == Topic
                    && a.SourcePartition == Partition
                    && a.SourceOffset == offset
                    && a.ConsumerName == ConsumerName, ct);

            entity.ShouldNotBeNull();
            entity.AttemptCount.ShouldBe(2);
            entity.SettledAt.ShouldBeNull();
            entity.SettlementKind.ShouldBeNull();
        }
    }

    [Fact]
    public async Task SettleAsyncSetsSettledFieldsAndLeavesRowPresent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        long offset = Random.Shared.NextInt64(1, 1_000_000_000);

        await using (SreDbContext context = postgres.CreateContext())
        {
            DeliveryAttemptStore store = new(context, TimeProvider.System);

            int attempt = await store.RecordFailureAsync(
                Topic, Partition, offset, ConsumerName,
                Guid.CreateVersion7(), "sre.alert.received", "Poison message failure", ct);
            attempt.ShouldBe(1);

            await store.SettleAsync(Topic, Partition, offset, ConsumerName, SettlementKind.DeadLetter, ct);

            ConsumerDeliveryAttemptEntity? entity = await context.ConsumerDeliveryAttempts
                .FirstOrDefaultAsync(a => a.SourceTopic == Topic
                    && a.SourcePartition == Partition
                    && a.SourceOffset == offset
                    && a.ConsumerName == ConsumerName, ct);

            entity.ShouldNotBeNull();
            entity.AttemptCount.ShouldBe(1);
            entity.SettledAt.ShouldNotBeNull();
            entity.SettlementKind.ShouldBe("dead_letter");
        }
    }
}
