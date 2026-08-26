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
    /// Inserts first and interprets a unique violation as "already processed",
    /// rather than checking and then inserting. Two deliveries of the same message
    /// on two consumer replicas can both pass a check a microsecond apart; only
    /// the primary key decides which one wins.
    /// </remarks>
    public async Task<bool> TryBeginAsync(
        Guid eventId,
        string eventType,
        string consumerName,
        CancellationToken cancellationToken)
    {
        _context.InboxMessages.Add(new InboxMessageEntity
        {
            EventId = eventId,
            ConsumerName = consumerName,
            EventType = eventType,
            ProcessedAt = _timeProvider.GetUtcNow(),
        });

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // Detach so the failed insert does not poison the next SaveChanges on
            // this scope, which would turn a benign duplicate into a broken
            // consumer.
            foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in _context.ChangeTracker.Entries())
            {
                if (entry.Entity is InboxMessageEntity)
                {
                    entry.State = EntityState.Detached;
                }
            }

            return false;
        }
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
    public async Task<bool> TryClaimAsync(
        string key,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // An expired claim is reclaimed rather than blocking forever. An alert
        // fingerprint that recurs months later is a new incident, and a key with
        // no expiry would silently suppress it.
        int reclaimed = await _context.IdempotencyKeys
            .Where(entity => entity.Key == key && entity.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        _ = reclaimed;

        _context.IdempotencyKeys.Add(new IdempotencyKeyEntity
        {
            Key = key,
            ClaimedAt = now,
            ExpiresAt = expiresAt,
        });

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in _context.ChangeTracker.Entries())
            {
                if (entry.Entity is IdempotencyKeyEntity)
                {
                    entry.State = EntityState.Detached;
                }
            }

            return false;
        }
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
