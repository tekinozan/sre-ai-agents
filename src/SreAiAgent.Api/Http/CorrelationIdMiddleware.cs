using SreAiAgent.Application.Diagnostics;

namespace SreAiAgent.Api.Http;

/// <summary>
/// Gives every request a correlation id and puts it on the response and the
/// current span (ARCHITECTURE.md 12.1, 16.2).
/// </summary>
/// <remarks>
/// The caller's id is honoured when it sends one, which is what makes a trace
/// spanning several systems joinable. It is length-capped and character-checked
/// first: the value ends up in log fields, span attributes and Kafka headers, so
/// an unbounded caller-controlled string is a log-injection vector and a
/// potential broker rejection.
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>The request and response header carrying the id.</summary>
    public const string HeaderName = "X-Correlation-Id";

    private const int MaximumLength = 128;

    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string correlationId = Sanitize(context.Request.Headers[HeaderName]);

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        System.Diagnostics.Activity.Current.SetSafeTag(TraceAttributes.CorrelationId, correlationId);

        await next(context).ConfigureAwait(false);
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength)
        {
            return Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture);
        }

        foreach (char character in value)
        {
            // Printable ASCII only. A newline here would let a caller forge log
            // lines, and a non-ASCII byte can be rejected by the Kafka header
            // encoder much later, in a worker, for a reason nobody would connect
            // back to an HTTP header.
            if (character is < ' ' or > '~')
            {
                return Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture);
            }
        }

        return value;
    }
}

/// <summary>Reads the correlation id established for the current request.</summary>
public static class CorrelationId
{
    /// <summary>The id assigned by <see cref="CorrelationIdMiddleware"/>.</summary>
    public static string From(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items[CorrelationIdMiddleware.HeaderName] as string ?? string.Empty;
    }
}
