using System.Text.Json.Serialization;

namespace SreAiAgent.Domain.Shared;

/// <summary>
/// The outcome of an operation that can fail in an expected way. Domain
/// invariant violations still throw <see cref="DomainException"/>; expected
/// failures such as "incident not found" or "policy denied" are values, because
/// they are part of the contract rather than a bug.
/// </summary>
/// <remarks>
/// A struct on purpose: the success path of a request handler should not
/// allocate a wrapper per call. Only the failure path allocates, via
/// <see cref="Shared.Error"/>.
/// </remarks>
/// <typeparam name="T">The success value type.</typeparam>
[JsonConverter(typeof(ResultJsonConverter))]
public readonly record struct Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T value)
    {
        _value = value;
        _error = null;
    }

    private Result(Error error)
    {
        _value = default;
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    /// <summary>The success value. Throws when the result is a failure.</summary>
    public T Value => _error is null
        ? _value!
        : throw new InvalidOperationException($"Result is a failure ({_error.Code}); check IsSuccess first.");

    /// <summary>The failure. Throws when the result is a success.</summary>
    public Error Error => _error
        ?? throw new InvalidOperationException("Result is a success; check IsFailure first.");

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error) => new(error);

    /// <summary>Projects both branches into a single value.</summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
        => _error is null ? onSuccess(_value!) : onFailure(_error);
}

/// <summary>The outcome of an operation with no success value.</summary>
[JsonConverter(typeof(ResultJsonConverter))]
public readonly record struct Result
{
    private readonly Error? _error;

    private Result(Error error) => _error = error;

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    public Error Error => _error
        ?? throw new InvalidOperationException("Result is a success; check IsFailure first.");

    public static Result Success() => default;

    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => new(error);
}
