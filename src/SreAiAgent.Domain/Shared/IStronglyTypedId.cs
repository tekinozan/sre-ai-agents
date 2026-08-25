namespace SreAiAgent.Domain.Shared;

/// <summary>
/// Contract shared by every strongly typed identifier (ARCHITECTURE.md 5.1).
/// The static abstract members let a single generic JSON converter and a single
/// generic EF Core value converter serve all id types, instead of one hand
/// written converter per id.
/// </summary>
/// <typeparam name="TSelf">The concrete identifier type.</typeparam>
public interface IStronglyTypedId<TSelf> : IParsable<TSelf>, IEquatable<TSelf>
    where TSelf : struct, IStronglyTypedId<TSelf>
{
    /// <summary>The underlying value. Version 7 so it sorts by creation time.</summary>
    Guid Value { get; }

    /// <summary>Wraps an existing value, typically when rehydrating from storage.</summary>
    static abstract TSelf From(Guid value);

    /// <summary>Allocates a new time-ordered identifier.</summary>
    static abstract TSelf New();
}
