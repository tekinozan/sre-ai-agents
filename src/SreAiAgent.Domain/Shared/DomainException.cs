namespace SreAiAgent.Domain.Shared;

/// <summary>
/// Raised when a domain invariant is violated. Carrying an
/// <see cref="ErrorCode"/> is mandatory so the API layer can map the failure to
/// a stable code without pattern-matching on exception types or messages.
/// </summary>
public class DomainException : Exception
{
    public DomainException(ErrorCode code, string message)
        : base(message)
        => Code = code;

    public DomainException(ErrorCode code, string message, Exception innerException)
        : base(message, innerException)
        => Code = code;

    /// <summary>The stable error code for this violation.</summary>
    public ErrorCode Code { get; }
}
