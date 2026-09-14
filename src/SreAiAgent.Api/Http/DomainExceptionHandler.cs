using Microsoft.AspNetCore.Diagnostics;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Api.Http;

/// <summary>
/// Turns an unhandled <see cref="DomainException"/> into the same
/// ProblemDetails response a <see cref="Result"/> failure would produce
/// (ARCHITECTURE.md 17).
/// </summary>
/// <remarks>
/// A caller whose validated claims cannot form a legal <c>TenantScope</c>
/// (D40) fails inside <c>principal.TenantScope()</c>, before any endpoint
/// handler has a <see cref="Result"/> to return -- <c>TenantScope.Create</c>
/// throws rather than returning one, because it runs on the boundary between
/// authentication and the request, not inside a use case. Left uncaught, that
/// exception reached the framework's generic exception handler and came back
/// as an opaque 500: a rejected caller reported as a server failure. Routing
/// it through <see cref="ResultExtensions.Problem"/> instead gives it the same
/// status code, the same stable error code and the same retryability flag any
/// other domain-invariant violation already produces -- <see cref="SreAiAgent.Application.Errors.ErrorCatalog"/>
/// already maps <see cref="DomainErrorCodes.TenantScopeInvalid"/> to 400, it was
/// simply never reached.
/// </remarks>
public sealed class DomainExceptionHandler : IExceptionHandler
{
    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DomainException domainException)
        {
            return false;
        }

        await ResultExtensions.Problem(Error.Permanent(domainException.Code, domainException.Message))
            .ExecuteAsync(httpContext)
            .ConfigureAwait(false);

        return true;
    }
}
