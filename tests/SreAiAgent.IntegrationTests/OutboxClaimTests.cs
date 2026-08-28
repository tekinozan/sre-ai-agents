using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;
using SreAiAgent.Infrastructure.Persistence.Outbox;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Covers the outbox claim of ARCHITECTURE.md 9.4 against a real database.
/// </summary>
/// <remarks>
/// FOR UPDATE SKIP LOCKED is the property the whole dispatcher design rests on,
/// and it is not expressible in EF Core: the store drops to raw SQL for it. That
/// makes it exactly the kind of thing a unit test cannot check, because a fake
/// would simply return whatever the code asked for. Two dispatchers claiming
/// concurrently is the only way to know it works.
/// </remarks>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutboxClaimTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private async Task<Guid> StageAsync(string partitionKey)
    {
        await using SreDbContext context = postgres.CreateContext();

        OutboxMessageEntity message = new()
        {
            Id = Guid.CreateVersion7(),
            Topic = "sre.incident.updated.v1",
            EventType = "sre.incident.updated",
            EventId = Guid.CreateVersion7(),
            PayloadJson = """{"ok":true}""",
            PartitionKey = partitionKey,
            CorrelationId = "corr-1",
            OccurredAt = Now,
            NextAttemptAt = Now,
        };

        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return message.Id;
    }

    private async Task ClearAsync()
    {
        await using SreDbContext context = postgres.CreateContext();
        await context.OutboxMessages.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TwoDispatchersNeverClaimTheSameMessage()
    {
        await ClearAsync();
        foreach (int index in Enumerable.Range(0, 6))
        {
            await StageAsync($"incident-{index.ToString(CultureInfo.InvariantCulture)}");
        }

        // Two contexts, two transactions, held open at the same time. That is the
        // situation SKIP LOCKED exists for: without it the second dispatcher
        // blocks on the first's row locks instead of moving past them, and a
        // second replica adds latency rather than throughput.
        await using SreDbContext first = postgres.CreateContext();
        await using SreDbContext second = postgres.CreateContext();

        await using var firstTransaction =
            await first.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var secondTransaction =
            await second.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<OutboxMessage> firstBatch = await new OutboxStore(first)
            .ClaimBatchAsync(3, Now, TestContext.Current.CancellationToken);

        IReadOnlyList<OutboxMessage> secondBatch = await new OutboxStore(second)
            .ClaimBatchAsync(3, Now, TestContext.Current.CancellationToken);

        firstBatch.Count.ShouldBe(3);
        secondBatch.Count.ShouldBe(3);

        Guid[] overlap = [.. firstBatch.Select(static m => m.Id).Intersect(secondBatch.Select(static m => m.Id))];
        overlap.ShouldBeEmpty("A message published twice is a duplicate every consumer then has to absorb.");

        await firstTransaction.RollbackAsync(TestContext.Current.CancellationToken);
        await secondTransaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ADispatchedMessageIsNotClaimedAgain()
    {
        await ClearAsync();
        Guid id = await StageAsync("incident-dispatched");

        await using SreDbContext context = postgres.CreateContext();
        OutboxStore store = new(context);

        await store.MarkDispatchedAsync([id], Now, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<OutboxMessage> claimed =
            await store.ClaimBatchAsync(10, Now, TestContext.Current.CancellationToken);

        claimed.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedMessageIsNotClaimedBeforeItsNextAttempt()
    {
        await ClearAsync();
        Guid id = await StageAsync("incident-backoff");

        await using SreDbContext context = postgres.CreateContext();
        OutboxStore store = new(context);

        await store.RecordFailureAsync(
            id,
            Error.Transient(ApplicationErrorCodes.DependencyUnavailable, "Broker unreachable."),
            Now.AddMinutes(5),
            TestContext.Current.CancellationToken);

        // Backoff has to be enforced by the claim query, not by the dispatcher
        // remembering. A replica that did not see the failure would otherwise
        // pick the message straight back up and defeat the backoff entirely.
        (await store.ClaimBatchAsync(10, Now, TestContext.Current.CancellationToken)).ShouldBeEmpty();

        IReadOnlyList<OutboxMessage> later =
            await store.ClaimBatchAsync(10, Now.AddMinutes(6), TestContext.Current.CancellationToken);

        later.Count.ShouldBe(1);
        later[0].AttemptCount.ShouldBe(1, "The retry has to know how many attempts came before it.");
    }

    [Fact]
    public async Task ADeadLetteredMessageIsParkedRatherThanRetried()
    {
        await ClearAsync();
        Guid id = await StageAsync("incident-poisoned");

        await using SreDbContext context = postgres.CreateContext();
        OutboxStore store = new(context);

        await store.MarkDeadLetteredAsync(
            id,
            Error.Permanent(ApplicationErrorCodes.ConfigInvalid, "Unroutable."),
            TestContext.Current.CancellationToken);

        (await store.ClaimBatchAsync(10, Now.AddDays(1), TestContext.Current.CancellationToken))
            .ShouldBeEmpty("A poisoned message retried forever stalls everything behind it.");

        // Parked, not deleted. The payload is what makes a replay possible once
        // the cause is fixed, and it is also the evidence of what went wrong.
        await using SreDbContext reader = postgres.CreateContext();
        OutboxMessageEntity parked = await reader.OutboxMessages
            .SingleAsync(message => message.Id == id, TestContext.Current.CancellationToken);

        parked.IsDeadLettered.ShouldBeTrue();
        parked.LastError.ShouldNotBeNullOrWhiteSpace();

        // Compared as JSON rather than as bytes. The column is jsonb, so
        // PostgreSQL reparses and reformats what it is given: key order and
        // whitespace are not preserved. That is fine for a message a consumer
        // will parse, and worth knowing before anyone hashes or signs a payload
        // and expects the stored copy to match.
        using JsonDocument payload = JsonDocument.Parse(parked.PayloadJson);
        payload.RootElement.GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task MessagesAreClaimedInTheOrderTheyWereStaged()
    {
        await ClearAsync();

        List<Guid> staged = [];
        foreach (int index in Enumerable.Range(0, 5))
        {
            staged.Add(await StageAsync($"incident-{index.ToString(CultureInfo.InvariantCulture)}"));
        }

        await using SreDbContext context = postgres.CreateContext();

        IReadOnlyList<OutboxMessage> claimed = await new OutboxStore(context)
            .ClaimBatchAsync(5, Now, TestContext.Current.CancellationToken);

        // Identifiers are version 7, so ordering by id is chronological. A
        // consumer that saw an incident completed before it started would have to
        // reconstruct the order itself.
        claimed.Select(static m => m.Id).ShouldBe(staged);
    }
}
