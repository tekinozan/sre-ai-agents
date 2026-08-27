using Microsoft.EntityFrameworkCore;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Repositories;

/// <summary>
/// Consumer-side deduplication for at-least-once delivery
/// (ARCHITECTURE.md 10.2).
/// </summary>
internal sealed class InboxStore : IInboxStore
{
    private readonly SreDbContext _context;
    private readonly TimeProvider _timeProvider;

    public InboxStore(SreDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stages the row; it does not save it. The row has to commit in the same
    /// transaction as the effect it guards, and saving here would commit it
    /// first: a failure afterwards would then leave a row saying the message was
    /// handled and no trace of the work, so the redelivery that should have
    /// repaired it would be skipped instead. The effect would be lost silently,
    /// which is the exact outcome an inbox exists to prevent.
    ///
    /// The read is only a fast path. Two replicas can both see no row a
    /// microsecond apart, so the primary key is what actually decides; a
    /// violation at commit time means the other delivery won and the caller
    /// treats the message as already processed.
    /// </remarks>
    public async Task<bool> TryBeginAsync(
        Guid eventId,
        string eventType,
        string consumerName,
        CancellationToken cancellationToken)
    {
        bool seen = await _context.InboxMessages
            .AnyAsync(
                message => message.EventId == eventId && message.ConsumerName == consumerName,
                cancellationToken)
            .ConfigureAwait(false);

        if (seen)
        {
            return false;
        }

        _context.InboxMessages.Add(new InboxMessageEntity
        {
            EventId = eventId,
            ConsumerName = consumerName,
            EventType = eventType,
            ProcessedAt = _timeProvider.GetUtcNow(),
        });

        return true;
    }
}

/// <summary>
/// At-most-once claims for operations that would otherwise repeat on a retry
/// (ARCHITECTURE.md 9.3).
/// </summary>
internal sealed class IdempotencyStore : IIdempotencyStore
{
    private readonly SreDbContext _context;
    private readonly TimeProvider _timeProvider;

    public IdempotencyStore(SreDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stages the claim rather than saving it, so it commits with the operation
    /// it guards. A claim written in its own transaction and then followed by a
    /// failure blocks every retry for the lifetime of the key while nothing was
    /// actually done, which is the opposite of what a claim is for.
    ///
    /// The read is a fast path only; the primary key is what decides a race.
    /// </remarks>
    public async Task<bool> TryClaimAsync(
        string key,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // An expired claim is reclaimed rather than blocking forever. An alert
        // fingerprint that recurs months later is a new incident, and a key with
        // no expiry would silently suppress it.
        await _context.IdempotencyKeys
            .Where(entity => entity.Key == key && entity.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        bool claimed = await _context.IdempotencyKeys
            .AnyAsync(entity => entity.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (claimed)
        {
            return false;
        }

        _context.IdempotencyKeys.Add(new IdempotencyKeyEntity
        {
            Key = key,
            ClaimedAt = now,
            ExpiresAt = expiresAt,
        });

        return true;
    }
}

/// <summary>Persists and resolves configuration snapshots.</summary>
internal sealed class ConfigurationSnapshotStore : IConfigurationSnapshotStore
{
    private readonly SreDbContext _context;

    public ConfigurationSnapshotStore(SreDbContext context) => _context = context;

    /// <inheritdoc />
    public void Add(ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _context.ConfigurationSnapshots.Add(new ConfigurationSnapshotEntity
        {
            Id = snapshot.Id,
            AgentRelease = snapshot.AgentRelease,
            PromptRelease = snapshot.PromptRelease,
            ModelRelease = snapshot.ModelRelease,
            ToolRelease = snapshot.ToolRelease,
            PolicyRelease = snapshot.PolicyRelease,
            FlagSnapshot = snapshot.FlagSnapshot,
            CreatedAt = snapshot.CreatedAt,
        });
    }

    /// <inheritdoc />
    public async Task<ConfigurationSnapshot?> GetAsync(
        ConfigurationSnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        ConfigurationSnapshotEntity? entity = await _context.ConfigurationSnapshots
            .FirstOrDefaultAsync(snapshot => snapshot.Id == snapshotId, cancellationToken)
            .ConfigureAwait(false);

        return entity is null
            ? null
            : new ConfigurationSnapshot(
                entity.Id,
                entity.AgentRelease,
                entity.PromptRelease,
                entity.ModelRelease,
                entity.ToolRelease,
                entity.PolicyRelease,
                entity.FlagSnapshot,
                entity.CreatedAt);
    }
}
