namespace SreAiAgent.Application.Abstractions.Observability;

/// <summary>A structured log search, expressed without backend syntax.</summary>
/// <param name="Service">Service to scope to. Checked against the allowlist.</param>
/// <param name="Environment">Environment to scope to.</param>
/// <param name="Window">Time range to search.</param>
/// <param name="MinimumLevel">Lowest severity to include, for example <c>error</c>.</param>
/// <param name="Terms">
/// Literal terms to match. Not a query expression: the adapter escapes and
/// combines them, so neither a model nor injected log content can widen the
/// search into an arbitrary backend query.
/// </param>
/// <param name="Limit">Maximum entries to return. Bounded by the adapter.</param>
public sealed record LogSearchQuery(
    string Service,
    string Environment,
    TimeWindow Window,
    string MinimumLevel,
    IReadOnlyList<string> Terms,
    int Limit);

/// <summary>One log entry.</summary>
/// <param name="Timestamp">When it was emitted, UTC.</param>
/// <param name="Level">Severity as reported by the backend.</param>
/// <param name="Message">
/// The message. Untrusted data: ARCHITECTURE.md 15.4 treats retrieved log
/// content as input to reason about, never as instructions to follow.
/// </param>
/// <param name="Fields">Structured fields carried with the entry.</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>The result of a log search.</summary>
/// <param name="Entries">Matching entries, newest first.</param>
/// <param name="TotalMatched">
/// How many entries matched before the limit. Lets an agent state "at least N"
/// instead of quietly reasoning over a truncated sample as if it were complete.
/// </param>
/// <param name="IsTruncated">True when the limit dropped entries.</param>
/// <param name="Provider">Backend that answered.</param>
public sealed record LogSearchResult(
    IReadOnlyList<LogEntry> Entries,
    long TotalMatched,
    bool IsTruncated,
    string Provider);

/// <summary>Read-only access to a log backend (ARCHITECTURE.md 6.3).</summary>
public interface ILogProvider
{
    Task<LogSearchResult> SearchAsync(LogSearchQuery query, CancellationToken cancellationToken);
}
