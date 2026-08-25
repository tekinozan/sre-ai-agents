namespace SreAiAgent.Domain.Shared;

/// <summary>
/// A stable, machine-readable failure code (ARCHITECTURE.md 17). Provider
/// exceptions never escape their adapter; every boundary reports an
/// <see cref="ErrorCode"/>, a retryability flag and a message that is safe to
/// return to a caller.
/// </summary>
/// <param name="Value">The code, in SCREAMING_SNAKE_CASE.</param>
public readonly record struct ErrorCode(string Value)
{
    public override string ToString() => Value;

    public static implicit operator string(ErrorCode code) => code.Value;

    public string ToStringValue() => Value;
}

/// <summary>
/// A failure with the information a caller legitimately needs: what went wrong,
/// whether retrying can help, and a message that leaks nothing.
/// </summary>
/// <param name="Code">The stable error code.</param>
/// <param name="Message">A message safe to expose outside the process.</param>
/// <param name="IsRetryable">
/// True only for transient conditions. Policy denials, invalid credentials and
/// deterministic validation failures are never retryable (ARCHITECTURE.md 14).
/// </param>
public sealed record Error(ErrorCode Code, string Message, bool IsRetryable)
{
    /// <summary>Creates a failure that a bounded retry may resolve.</summary>
    public static Error Transient(ErrorCode code, string message) => new(code, message, IsRetryable: true);

    /// <summary>Creates a failure that will fail identically on every retry.</summary>
    public static Error Permanent(ErrorCode code, string message) => new(code, message, IsRetryable: false);
}
