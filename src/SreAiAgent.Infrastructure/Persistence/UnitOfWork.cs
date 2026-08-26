using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SreAiAgent.Application.Abstractions.Persistence;

namespace SreAiAgent.Infrastructure.Persistence;

/// <summary>
/// The transaction boundary for a business operation (ARCHITECTURE.md 9.4).
/// </summary>
/// <remarks>
/// A thin wrapper over the change tracker, and deliberately nothing more. The
/// context already is a unit of work; adding a second abstraction that tracks its
/// own entities would produce two sources of truth about what is pending.
///
/// What this does add is the guarantee that matters: the state change, its
/// timeline entry and its outbox row commit together or not at all. Kafka cannot
/// participate in a database transaction, so the outbox is how a committed state
/// change and its notification stay consistent across a process crash.
/// </remarks>
internal sealed class UnitOfWork : IUnitOfWork
{
    private readonly SreDbContext _context;

    public UnitOfWork(SreDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        => _context.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Uses an execution strategy so the transaction survives a transient
    /// connection failure. Without it, retrying a block that opened its own
    /// transaction throws: the strategy has to own the retry boundary, not sit
    /// inside it.
    /// </remarks>
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        IExecutionStrategy strategy = _context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            cancellationToken,
            async (token) =>
            {
                await using IDbContextTransaction transaction =
                    await _context.Database.BeginTransactionAsync(token).ConfigureAwait(false);

                T result = await operation(token).ConfigureAwait(false);

                await _context.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);

                return result;
            }).ConfigureAwait(false);
    }
}
