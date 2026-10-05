using Microsoft.AspNetCore.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Api.Http;

/// <summary>
/// Answers a request body or parameter the framework could not bind with the
/// same 400 problem as any other validation failure.
/// </summary>
/// <remarks>
/// Minimal APIs throw <see cref="BadHttpRequestException"/> only in Development
/// and answer a bare 400 elsewhere. Thrown, it reached the generic handler and
/// came back as a 500; bare, it carried no error code. Program.cs now makes it
/// throw everywhere so this handler gives one answer in every environment: a
/// missing required member or an unknown enum value is the caller's mistake,
/// reported as <c>VALIDATION_FAILED</c>. The framework's own message is not
/// echoed; it names internal type names.
/// </remarks>
public sealed class BadRequestExceptionHandler : IExceptionHandler
{
    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException { StatusCode: StatusCodes.Status400BadRequest })
        {
            return false;
        }

        await ResultExtensions.Problem(Error.Permanent(
                ApplicationErrorCodes.ValidationFailed,
                "The request could not be read: a required member is missing or a value has the wrong type."))
            .ExecuteAsync(httpContext)
            .ConfigureAwait(false);

        return true;
    }
}
