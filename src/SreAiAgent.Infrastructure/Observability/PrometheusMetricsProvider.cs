using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Observability;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Observability;

/// <summary>
/// Reads metrics from Prometheus over its HTTP API (ARCHITECTURE.md 6.3).
/// </summary>
/// <remarks>
/// The port carries a registry key, never an expression, so the translation lives
/// here. The expressions themselves come from the tool artifacts rather than from
/// this file: adding a metric is a configuration release, and startup validation
/// guarantees one agreed expression per selector, which is what makes looking one
/// up without knowing which tool asked well-defined.
/// </remarks>
internal sealed class PrometheusMetricsProvider : IMetricsProvider
{
    private const string ProviderName = "prometheus";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FrozenDictionary<string, string> _expressions;
    private readonly ObservabilityOptions _options;

    public PrometheusMetricsProvider(
        IHttpClientFactory httpClientFactory,
        IReadOnlyToolRegistry tools,
        IOptions<ObservabilityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(options);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;

        Dictionary<string, string> expressions = new(StringComparer.Ordinal);
        foreach (ToolDescriptor tool in tools.All)
        {
            if (!string.Equals(tool.Provider, ProviderName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach ((string selector, string expression) in tool.ProviderExpressions)
            {
                expressions[selector] = expression;
            }
        }

        _expressions = expressions.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public Task<MetricQueryResult> QueryAsync(MetricQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        string expression = Render(query.MetricSelector, query.Service, query.Environment, query.Labels);

        return SendAsync(
            "api/v1/query",
            [("query", expression), ("time", Timestamp(query.At))],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<MetricQueryResult> QueryRangeAsync(MetricRangeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        string expression = Render(query.MetricSelector, query.Service, query.Environment, query.Labels);
        TimeSpan step = ResolveStep(query.Window, query.Step);

        return SendAsync(
            "api/v1/query_range",
            [
                ("query", expression),
                ("start", Timestamp(query.Window.Start)),
                ("end", Timestamp(query.Window.End)),
                ("step", step.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)),
            ],
            cancellationToken);
    }

    private string Render(
        string selector,
        string service,
        string environment,
        IReadOnlyDictionary<string, string> labels)
    {
        if (!_expressions.TryGetValue(selector, out string? expression))
        {
            // Startup validation covers the shipped release, so reaching this
            // means the tool artifact and the running process disagree.
            throw new ObservabilityQueryException(
                $"No {ProviderName} expression is configured for metric '{selector}'.");
        }

        return PromQuery.Render(expression, service, environment, labels);
    }

    /// <summary>
    /// Widens the step until the window fits inside the point budget.
    /// </summary>
    /// <remarks>
    /// The schema already bounds the step, but a legal step and a legal window
    /// still multiply: six hours at fifteen seconds is 1,440 points per series,
    /// and Prometheus refuses outright above eleven thousand. Widening returns a
    /// coarser answer, which is a better outcome than an error mid-investigation.
    /// </remarks>
    private TimeSpan ResolveStep(TimeWindow window, TimeSpan requested)
    {
        TimeSpan step = requested < TimeSpan.FromSeconds(_options.MinStepSeconds)
            ? TimeSpan.FromSeconds(_options.MinStepSeconds)
            : requested;

        double points = window.Duration / step;
        if (points > _options.MaxPointsPerSeries)
        {
            step = TimeSpan.FromSeconds(Math.Ceiling(window.Duration.TotalSeconds / _options.MaxPointsPerSeries));
        }

        return step;
    }

    private async Task<MetricQueryResult> SendAsync(
        string path,
        (string Name, string Value)[] parameters,
        CancellationToken cancellationToken)
    {
        using HttpClient client = _httpClientFactory.CreateClient(ObservabilityHttpClients.Prometheus);

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

        // The resilience pipeline has already retried what is worth retrying, so
        // a failure here is final and belongs in the tool call audit record.
        if (!response.IsSuccessStatusCode)
        {
            throw new ObservabilityQueryException(
                $"Prometheus answered {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}.");
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            PrometheusResponse? payload = await JsonSerializer
                .DeserializeAsync(stream, ObservabilityJsonContext.Default.PrometheusResponse, cancellationToken)
                .ConfigureAwait(false);

            return Parse(payload);
        }
    }

    private MetricQueryResult Parse(PrometheusResponse? payload)
    {
        if (payload is null || !string.Equals(payload.Status, "success", StringComparison.Ordinal))
        {
            throw new ObservabilityQueryException(
                $"Prometheus reported '{payload?.Status ?? "no status"}': {payload?.Error ?? "no detail"}.");
        }

        List<MetricSeries> series = [];
        List<PrometheusSeries> results = payload.Data?.Result ?? [];

        // Truncation is folded into IsPartial rather than reported separately,
        // because the port has one flag and the consequence is the same: an agent
        // must not read a complete story out of this.
        bool isPartial = payload.Warnings is { Count: > 0 } || results.Count > _options.MaxSeries;

        foreach (PrometheusSeries result in results.Take(_options.MaxSeries))
        {
            List<MetricSample> points = [];

            if (result.Values is not null)
            {
                foreach (List<JsonElement> pair in result.Values.Take(_options.MaxPointsPerSeries))
                {
                    if (TryReadSample(pair, out MetricSample sample))
                    {
                        points.Add(sample);
                    }
                }

                isPartial |= result.Values.Count > _options.MaxPointsPerSeries;
            }
            else if (result.Value is not null && TryReadSample(result.Value, out MetricSample instant))
            {
                points.Add(instant);
            }

            series.Add(new MetricSeries(
                result.Metric ?? new Dictionary<string, string>(StringComparer.Ordinal),
                points));
        }

        return new MetricQueryResult(series, isPartial, ProviderName);
    }

    private static bool TryReadSample(List<JsonElement> pair, out MetricSample sample)
    {
        sample = default;

        if (pair.Count < 2
            || pair[0].ValueKind is not JsonValueKind.Number
            || pair[1].ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        // Prometheus writes absent and non-finite values as "NaN", "+Inf" and
        // "-Inf". Carrying them forward would put a value an agent cannot reason
        // about into evidence; dropping the sample leaves a visible gap instead.
        string raw = pair[1].GetString() ?? string.Empty;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
        {
            return false;
        }

        sample = new MetricSample(
            DateTimeOffset.FromUnixTimeMilliseconds((long)(pair[0].GetDouble() * 1000)),
            value);

        return true;
    }

    private static string Timestamp(DateTimeOffset instant)
        => instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
