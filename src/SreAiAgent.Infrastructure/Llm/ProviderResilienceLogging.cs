using Microsoft.Extensions.Logging;

namespace SreAiAgent.Infrastructure.Llm;

/// <summary>
/// Structured logging for the model HTTP resilience pipeline.
/// </summary>
/// <remarks>
/// Operational signals for retry exhaustion and circuit-breaker state transitions.
/// Prompts, tokens, credentials, and payload bodies are never logged.
/// </remarks>
internal static partial class ProviderResilienceLogging
{
    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "Model HTTP circuit breaker opened for {BreakDurationSeconds}s.")]
    public static partial void LogModelCircuitBreakerOpened(ILogger logger, double breakDurationSeconds);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "Model HTTP resilience pipeline exhausted {Attempts} retry attempts.")]
    public static partial void LogModelHttpRetryExhausted(ILogger logger, int attempts);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Information,
        Message = "Model HTTP attempt {Attempt} retrying after {DelayMs}ms.")]
    public static partial void LogModelHttpRetry(ILogger logger, int attempt, double delayMs);
}
