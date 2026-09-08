using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Polly;

namespace SreAiAgent.Infrastructure.Llm;

/// <summary>
/// Reports when the model HTTP resilience pipeline retried a request and the request
/// was still failing once it gave up.
/// </summary>
/// <remarks>
/// Registered around, not inside, the pipeline <c>AddStandardResilienceHandler</c> installs
/// (it must be added to the <c>IHttpClientBuilder</c> before that call, which makes it the
/// outer handler): this class calls the inner chain exactly once per logical request, so it
/// only ever inspects the terminal outcome after every retry the pipeline was going to make
/// has already happened. <c>HttpRetryStrategyOptions.OnRetry</c>
/// cannot do this on its own — it only fires before a retry, so it cannot know whether that
/// retry, or a later one, goes on to succeed. Reporting "exhausted" from there would be a
/// guess made before the outcome it describes has happened.
/// </remarks>
internal sealed class RetryExhaustionHandler(ILogger logger) : DelegatingHandler
{
    // A mutable box, not a plain counter: HttpRequestMessage.Options is copied when the
    // resilience pipeline clones the request for a retry (a request whose content has
    // already been sent cannot be resent as-is), so the box must be a reference type for
    // every clone's copy of the option to keep pointing at the one counter this handler reads.
    private static readonly HttpRequestOptionsKey<StrongBox<int>> AttemptCountKey = new(
        "SreAiAgent.Llm.RetryAttemptCount");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        StrongBox<int> attempts = new(0);
        request.Options.Set(AttemptCountKey, attempts);

        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode && attempts.Value > 0)
            {
                ProviderResilienceLogging.LogModelHttpRetryExhausted(logger, attempts.Value);
            }

            return response;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && exception is not Polly.CircuitBreaker.BrokenCircuitException
            && attempts.Value > 0)
        {
            // An open circuit is reported by CircuitBreaker.OnOpened at the moment it trips,
            // and caller/timeout cancellation is not a retry outcome; both are excluded here
            // so this signal means only "retried, and still failing".
            ProviderResilienceLogging.LogModelHttpRetryExhausted(logger, attempts.Value);
            throw;
        }
    }

    /// <summary>
    /// Called from <c>HttpRetryStrategyOptions.OnRetry</c>
    /// to record that a retry is about to run, so the count is available if the retried
    /// attempt also fails.
    /// </summary>
    public static void RecordRetry(ResilienceContext context, int attemptNumber)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Outcome.Result is null for an exception-based retry (e.g. HttpRequestException),
        // so the failed response cannot be used to reach the current attempt's request.
        // GetRequestMessage is marked experimental (EXTEXP0001) in 10.9.0, but it is the only
        // documented way to reach it from here; verified against the pinned package version
        // to return the same per-attempt request for both response-based and exception-based
        // outcomes (a synthetic HttpRequestException-throwing handler reaches the same box a
        // 503 response does).
#pragma warning disable EXTEXP0001
        HttpRequestMessage? request = HttpResilienceContextExtensions.GetRequestMessage(context);
#pragma warning restore EXTEXP0001

        if (request?.Options.TryGetValue(AttemptCountKey, out StrongBox<int>? box) == true && box is not null)
        {
            box.Value = attemptNumber;
        }
    }
}
