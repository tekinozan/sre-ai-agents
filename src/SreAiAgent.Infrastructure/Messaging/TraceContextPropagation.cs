using System.Diagnostics;
using System.Text;
using Confluent.Kafka;

namespace SreAiAgent.Infrastructure.Messaging;

/// <summary>
/// Carries W3C trace context across the broker (ARCHITECTURE.md 16.1).
/// </summary>
/// <remarks>
/// Without this a trace ends at the producer and the consumer starts a
/// disconnected one, which removes most of the value of tracing an asynchronous
/// pipeline: the question being asked is almost always "what happened to this
/// incident", and the answer spans an HTTP request, a topic and a worker.
///
/// The header names are the W3C ones rather than anything bespoke, so a message
/// this platform publishes is readable by any other instrumented consumer, and
/// one published by another system is readable here.
/// </remarks>
internal static class TraceContextPropagation
{
    private const string TraceParentHeader = "traceparent";
    private const string TraceStateHeader = "tracestate";

    /// <summary>Writes the current trace context onto a message.</summary>
    public static void Inject(Headers headers, string? traceParent, string? traceState = null)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!string.IsNullOrEmpty(traceParent))
        {
            headers.Add(TraceParentHeader, Encoding.UTF8.GetBytes(traceParent));
        }

        if (!string.IsNullOrEmpty(traceState))
        {
            headers.Add(TraceStateHeader, Encoding.UTF8.GetBytes(traceState));
        }
    }

    /// <summary>
    /// Reads the producer's trace context off a message.
    /// </summary>
    /// <remarks>
    /// Returns the raw header rather than an ActivityContext so the caller can
    /// decide between parenting and linking. A consumer that processes a batch
    /// has several producers to account for and can parent to only one of them.
    /// </remarks>
    public static ActivityContext Extract(Headers? headers)
    {
        if (headers is null)
        {
            return default;
        }

        string? traceParent = ReadHeader(headers, TraceParentHeader);

        if (traceParent is null)
        {
            return default;
        }

        return ActivityContext.TryParse(traceParent, ReadHeader(headers, TraceStateHeader), out ActivityContext parsed)
            ? parsed
            : default;
    }

    private static string? ReadHeader(Headers headers, string name)
        => headers.TryGetLastBytes(name, out byte[]? value) ? Encoding.UTF8.GetString(value) : null;
}
