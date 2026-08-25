namespace SreAiAgent.Domain.Shared;

/// <summary>
/// Base class for aggregate roots: identity plus a buffer of domain events
/// raised by the current unit of work.
/// </summary>
/// <typeparam name="TId">The strongly typed identifier.</typeparam>
public abstract class Entity<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    /// <summary>
    /// Allocated lazily. Most aggregates loaded during a request are read and
    /// discarded without raising anything, and an investigation touches a lot of
    /// them; an eagerly allocated list per instance is pure garbage.
    /// </summary>
    private List<DomainEvent>? _domainEvents;

    /// <summary>The aggregate identity.</summary>
    public TId Id { get; protected set; }

    /// <summary>Records an event to be published after the transaction commits.</summary>
    protected void Raise(DomainEvent domainEvent)
        => (_domainEvents ??= new List<DomainEvent>(capacity: 2)).Add(domainEvent);

    /// <summary>
    /// Removes and returns the buffered events. Draining rather than exposing
    /// the list prevents the same event being dispatched twice when an aggregate
    /// survives more than one save.
    /// </summary>
    public IReadOnlyList<DomainEvent> DequeueEvents()
    {
        if (_domainEvents is null || _domainEvents.Count == 0)
        {
            return [];
        }

        DomainEvent[] drained = [.. _domainEvents];
        _domainEvents.Clear();
        return drained;
    }
}
