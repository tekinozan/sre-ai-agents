using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Observability;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Observability.Providers.Shared;

namespace SreAiAgent.Infrastructure.Observability.Providers.Loki;

/// <summary>
/// Searches logs in Loki (ARCHITECTURE.md 6.3).
/// </summary>
/// <remarks>
/// The port takes literal terms, not a query expression, and that is the whole
/// security argument for this adapter: log content is untrusted and a model
/// reasoning over it can be steered, so the worst case of a hostile term has to
/// be an unhelpful search rather than an arbitrary backend query. The terms are
/// escaped and combined here; nothing a model wrote is ever parsed as LogQL.
/// </remarks>
internal sealed class LokiLogProvider : ILogProvider
{
    private const string ProviderName = "loki";

    /// <summary>
    /// The severity ladder from the <c>logs.search</c> schema, weakest first.
    /// </summary>
    /// <remarks>
    /// A minimum level becomes a matcher over every level at or above it. The
    /// ladder is fixed and the schema enumerates it, so nothing here interpolates
    /// anything a model chose — but it is still checked, because a level that
    /// silently matched nothing would look exactly like a quiet service.
    /// </remarks>
    private static readonly string[] Severities = ["trace", "debug", "info", "warn", "error", "fatal"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ObservabilityOptions _options;

    public LokiLogProvider(IHttpClientFactory httpClientFactory, IOptions<ObservabilityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<LogSearchResult> SearchAsync(LogSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        string expression = BuildExpression(query);
        int limit = Math.Clamp(query.Limit, 1, _options.MaxLogEntries);

        LokiResponse? streams = await SendAsync(
            "loki/api/v1/query_range",
            [
                ("query", expression),
                ("start", Nanoseconds(query.Window.Start)),
                ("end", Nanoseconds(query.Window.End)),
                ("limit", limit.ToString(CultureInfo.InvariantCulture)),
                ("direction", "backward"),
            ],
            cancellationToken).ConfigureAwait(false);

        List<LogEntry> entries = ReadEntries(streams, limit);
        long matched = await CountAsync(expression, query.Window, cancellationToken).ConfigureAwait(false);

        // The count is a second request rather than the entry count, because
        // TotalMatched exists so an agent can say "at least N" instead of
        // reasoning over a truncated sample as though it were complete. Deriving
        // it from the page would make it agree with the sample by construction
        // and quietly stop meaning anything.
        return new LogSearchResult(
            entries,
            Math.Max(matched, entries.Count),
            matched > entries.Count,
            ProviderName);
    }

    private static string BuildExpression(LogSearchQuery query)
    {
        int floor = Array.FindIndex(
            Severities,
            level => string.Equals(level, query.MinimumLevel, StringComparison.OrdinalIgnoreCase));

        if (floor < 0)
        {
            throw new ObservabilityQueryException(
                $"Unknown minimum level '{query.MinimumLevel}'. Expected one of {string.Join(", ", Severities)}.");
        }

        StringBuilder expression = new(128);
        expression.Append(PromQuery.BuildSelector(
            query.Service,
            query.Environment,
            new Dictionary<string, string>(StringComparer.Ordinal)));

        // The selector closes with '}', so the level matcher is spliced in before
        // it rather than appended after.
        expression.Remove(expression.Length - 1, 1)
            .Append(",level=~\"")
            .Append(string.Join('|', Severities[floor..]))
            .Append("\"}");

        foreach (string term in query.Terms)
        {
            if (term.Length == 0)
            {
                continue;
            }

            expression.Append(" |= \"").Append(PromQuery.EscapeLiteral(term, "term")).Append('"');
        }

        return expression.ToString();
    }

    private async Task<long> CountAsync(
        string expression,
        TimeWindow window,
        CancellationToken cancellationToken)
    {
        string duration = ((long)window.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        LokiResponse? counted = await SendAsync(
            "loki/api/v1/query",
            [
                ("query", $"sum(count_over_time({expression}[{duration}s]))"),
                ("time", Nanoseconds(window.End)),
            ],
            cancellationToken).ConfigureAwait(false);

        foreach (LokiStream result in counted?.Data?.Result ?? [])
        {
            if (result.Value is { Count: >= 2 }
                && result.Value[1].ValueKind is JsonValueKind.String
                && long.TryParse(
                    result.Value[1].GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long total))
            {
                return total;
            }
        }

        return 0;
    }

    private static List<LogEntry> ReadEntries(LokiResponse? response, int limit)
    {
        List<LogEntry> entries = [];

        foreach (LokiStream stream in response?.Data?.Result ?? [])
        {
            Dictionary<string, string> fields =
                stream.Stream ?? new Dictionary<string, string>(StringComparer.Ordinal);

            string level = fields.TryGetValue("level", out string? reported) ? reported : "unknown";

            foreach (List<JsonElement> pair in stream.Values ?? [])
            {
                if (pair.Count < 2
                    || pair[0].ValueKind is not JsonValueKind.String
                    || pair[1].ValueKind is not JsonValueKind.String
                    || !long.TryParse(
                        pair[0].GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long nanoseconds))
                {
                    continue;
                }

                entries.Add(new LogEntry(
                    DateTimeOffset.FromUnixTimeMilliseconds(nanoseconds / 1_000_000),
                    level,
                    pair[1].GetString() ?? string.Empty,
                    fields));
            }
        }

        // Loki orders within a stream but not across them, and an agent reading a
        // window out of order will build a causal story out of nothing.
        entries.Sort(static (left, right) => right.Timestamp.CompareTo(left.Timestamp));

        return entries.Count > limit ? entries[..limit] : entries;
    }

    private async Task<LokiResponse?> SendAsync(
        string path,
        (string Name, string Value)[] parameters,
        CancellationToken cancellationToken)
    {
        using HttpClient client = _httpClientFactory.CreateClient(ObservabilityHttpClients.Loki);

        StringBuilder uri = new(path);
        char separator = '?';
        foreach ((string name, string value) in parameters)
        {
            uri.Append(separator).Append(name).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        using HttpResponseMessage response = await client
            .GetAsync(new Uri(uri.ToString(), UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ObservabilityQueryException(
                $"Loki answered {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}.");
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            LokiResponse? payload = await JsonSerializer
                .DeserializeAsync(stream, ObservabilityJsonContext.Default.LokiResponse, cancellationToken)
                .ConfigureAwait(false);

            if (payload is null || !string.Equals(payload.Status, "success", StringComparison.Ordinal))
            {
                throw new ObservabilityQueryException(
                    $"Loki reported '{payload?.Status ?? "no status"}'.");
            }

            return payload;
        }
    }

    private static string Nanoseconds(DateTimeOffset instant)
        => (instant.ToUnixTimeMilliseconds() * 1_000_000).ToString(CultureInfo.InvariantCulture);
}
