using System.Runtime.CompilerServices;

namespace SreAiAgent.Domain.Shared;

/// <summary>
/// Invariant checks that fail with a domain error code rather than a framework
/// exception, so a violation surfaces to the caller as a stable code instead of
/// an ArgumentException message that the API layer would have to parse.
/// </summary>
public static class Guard
{
    /// <summary>Rejects null, empty and whitespace-only values.</summary>
    public static string AgainstNullOrWhiteSpace(
        string? value,
        ErrorCode code,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException(code, $"'{parameterName}' is required.");
        }

        return value;
    }

    /// <summary>Rejects values longer than the storage column allows.</summary>
    public static string AgainstTooLong(
        string value,
        int maximumLength,
        ErrorCode code,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (value.Length > maximumLength)
        {
            throw new DomainException(
                code,
                $"'{parameterName}' must be at most {maximumLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        return value;
    }

    /// <summary>Rejects a value that is not a declared member of its enum.</summary>
    public static TEnum AgainstUndefined<TEnum>(
        TEnum value,
        ErrorCode code,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new DomainException(code, $"'{parameterName}' is not a defined {typeof(TEnum).Name} value.");
        }

        return value;
    }
}
