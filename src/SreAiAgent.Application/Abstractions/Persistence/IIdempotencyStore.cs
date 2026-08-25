namespace SreAiAgent.Application.Abstractions.Persistence;

/// <summary>
/// Enforces at-most-once semantics for operations that would otherwise repeat on
/// a retry (ARCHITECTURE.md 9.3).
/// </summary>
/// <remarks>
/// Backed by a unique index rather than a read-then-write check. Two concurrent
/// deliveries of the same alert can both observe "not seen yet" a microsecond
/// apart; only a database constraint decides which one wins.
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Attempts to claim a key. Returns false when it was already claimed, which
    /// means the caller must not perform the effect again.
    /// </summary>
    /// <param name="key">Scoped, stable key for the operation.</param>
    /// <param name="expiresAt">When the claim may be reclaimed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<bool> TryClaimAsync(string key, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}
