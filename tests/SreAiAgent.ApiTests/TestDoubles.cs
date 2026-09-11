using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.ApiTests;

/// <summary>
/// Captures events staged via <see cref="IEventPublisher"/> for assertion in tests.
/// </summary>
internal sealed class RecordingEventPublisher : IEventPublisher
{
    private readonly List<RecordedEvent> _events = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<RecordedEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Enqueue(DomainEvent domainEvent, string correlationId, Guid? causationId = null)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        lock (_gate)
        {
            _events.Add(new RecordedEvent(
                domainEvent.EventType,
                domainEvent.EventType,
                domainEvent,
                correlationId,
                causationId));
        }
    }

    /// <inheritdoc />
    public void Enqueue<TPayload>(
        string topic,
        string eventType,
        TPayload payload,
        string correlationId,
        Guid? causationId = null)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (_gate)
        {
            _events.Add(new RecordedEvent(
                topic,
                eventType,
                payload,
                correlationId,
                causationId));
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }
}

/// <summary>
/// A recorded event publication payload.
/// </summary>
internal sealed record RecordedEvent(
    string Topic,
    string EventType,
    object Payload,
    string CorrelationId,
    Guid? CausationId);

/// <summary>
/// Test double for <see cref="IUnitOfWork"/> that tracks commit calls without requiring a database.
/// </summary>
internal sealed class CountingUnitOfWork : IUnitOfWork
{
    private int _saveChangesCallCount;

    public int SaveChangesCallCount => Volatile.Read(ref _saveChangesCallCount);

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _saveChangesCallCount);
        return Task.FromResult(0);
    }

    /// <inheritdoc />
    public Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation(cancellationToken);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _saveChangesCallCount, 0);
    }
}
