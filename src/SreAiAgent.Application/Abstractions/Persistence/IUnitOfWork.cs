namespace SreAiAgent.Application.Abstractions.Persistence;

/// <summary>
/// The transaction boundary for a business operation (ARCHITECTURE.md 9.4).
/// </summary>
/// <remarks>
/// An incident state change, its timeline entry and its outbox record must
/// commit together or not at all. That is the entire reason the outbox exists:
/// publishing to Kafka inside the transaction is impossible, and publishing
/// after it without a durable record loses the message whenever the process dies
/// in between.
///
/// There is no generic repository behind this. Repositories are aggregate-scoped,
/// and the change tracker of the underlying context is the unit of work; wrapping
/// it in a second abstraction would add a layer that only forwards calls.
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Commits everything staged in the current scope, including drained domain
    /// events written to the outbox.
    /// </summary>
    /// <returns>Number of affected rows.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="operation"/> inside an explicit transaction, for the
    /// cases that need more than the implicit one around a single save, such as
    /// claiming outbox rows and publishing in one go.
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}
