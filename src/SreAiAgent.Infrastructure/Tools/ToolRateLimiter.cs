using System.Collections.Concurrent;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Tools;

/// <summary>
/// Per-tenant, per-incident and per-tool ceilings on tool execution
/// (ARCHITECTURE.md 14).
/// </summary>
/// <remarks>
/// The purpose is blast radius, not fairness. One investigation that decides to
/// query Prometheus in a loop must not degrade the metrics backend for every
/// other tenant, and the backend being degraded here is frequently the same one
/// the incident is about.
///
/// A fixed window rather than a sliding one: the accounting is a single
/// interlocked increment against a bucket keyed by minute, which costs nothing on
/// a path that runs for every tool call. A sliding window would be more accurate
/// at the boundary and would require retaining per-request timestamps for a
/// benefit nobody would notice at these limits.
/// </remarks>
internal sealed class ToolRateLimiter
{
    private readonly ConcurrentDictionary<Bucket, int> _counters = new();
    private readonly TimeProvider _timeProvider;

    public ToolRateLimiter(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Records one execution and reports whether any ceiling was exceeded.
    /// </summary>
    public Result TryAcquire(
        string tenant,
        IncidentId incidentId,
        string toolName,
        PolicyRateLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        long minute = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / 60;
        PruneExpired(minute);

        if (Increment(new Bucket("tenant", tenant, minute)) > limits.PerTenantPerMinute)
        {
            return Denied($"Tenant '{tenant}' exceeded {limits.PerTenantPerMinute} tool calls per minute.");
        }

        if (Increment(new Bucket("incident", incidentId.ToString(), minute)) > limits.PerIncidentPerMinute)
        {
            return Denied($"Incident exceeded {limits.PerIncidentPerMinute} tool calls per minute.");
        }

        if (Increment(new Bucket("tool", $"{tenant}|{toolName}", minute)) > limits.PerToolPerMinute)
        {
            return Denied($"Tool '{toolName}' exceeded {limits.PerToolPerMinute} calls per minute for this tenant.");
        }

        return Result.Success();
    }

    private int Increment(Bucket bucket)
        => _counters.AddOrUpdate(bucket, 1, static (_, current) => current + 1);

    /// <summary>
    /// Drops buckets from previous minutes. Without this the dictionary grows for
    /// the life of the process, one entry per tenant per tool per minute, which
    /// on a long-running worker is an unbounded leak rather than a cache.
    /// </summary>
    private void PruneExpired(long currentMinute)
    {
        foreach (KeyValuePair<Bucket, int> entry in _counters)
        {
            if (entry.Key.Minute < currentMinute)
            {
                _counters.TryRemove(entry.Key, out _);
            }
        }
    }

    private static Result Denied(string reason)
        => Result.Failure(Error.Transient(
            Application.Errors.ApplicationErrorCodes.ToolRateLimited, reason));

    private readonly record struct Bucket(string Kind, string Key, long Minute);
}
