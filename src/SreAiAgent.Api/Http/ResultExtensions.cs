using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Api.Http;

/// <summary>
/// Turns a <see cref="Result"/> into an HTTP response
/// (ARCHITECTURE.md 12.1 and 17).
/// </summary>
/// <remarks>
/// The status code comes from the error catalog rather than from a switch in
/// each endpoint. There is exactly one mapping from error code to status code in
/// the platform, so a new code cannot answer 500 in one endpoint and 409 in
/// another, and the catalog is a frozen dictionary rather than a chain of
/// comparisons.
///
/// The body is a ProblemDetails carrying the stable code and whether a retry
/// could help. Both are contract: a client integrating with this needs to
/// distinguish "the request was wrong" from "try again in a moment" without
/// parsing prose, and the message is the one part that may change.
/// </remarks>
public static class ResultExtensions
{
    private const string CodeExtension = "code";
    private const string RetryableExtension = "retryable";

    /// <summary>Maps a unit result onto a response.</summary>
    public static IResult ToHttpResult(this Result result, int successStatusCode = StatusCodes.Status204NoContent)
        => result.IsSuccess
            ? Results.StatusCode(successStatusCode)
            : Problem(result.Error);

    /// <summary>Maps a result carrying a value onto a response.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, int successStatusCode = StatusCodes.Status200OK)
        => result.IsSuccess
            ? Results.Json(result.Value, statusCode: successStatusCode)
            : Problem(result.Error);

    /// <summary>Maps a nullable read to a response, answering 404 for null.</summary>
    /// <remarks>
    /// A missing incident and an incident belonging to another tenant produce the
    /// same 404 on purpose. The read queries already filter by tenant, so a
    /// distinct 403 would confirm the existence of a resource the caller is not
    /// allowed to know about.
    /// </remarks>
    public static IResult ToHttpResult<T>(this T? value, string missingDetail)
        where T : class
        => value is null
            ? Problem(Error.Permanent(ApplicationErrorCodes.IncidentNotFound, missingDetail))
            : Results.Json(value);

    /// <summary>Builds a ProblemDetails response for an error.</summary>
    public static IResult Problem(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        ErrorDescriptor descriptor = ErrorCatalog.Describe(error.Code);

        return Results.Problem(
            detail: error.Message,
            statusCode: descriptor.HttpStatusCode,
            title: error.Code.Value,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [CodeExtension] = error.Code.Value,

                // The error's own flag, not the catalog's. The catalog says
                // whether a code is retryable in general; the instance knows
                // whether this occurrence was, and a timeout that has already
                // exhausted its budget is not retryable however transient the
                // category is.
                [RetryableExtension] = error.IsRetryable,
            });
    }
}
