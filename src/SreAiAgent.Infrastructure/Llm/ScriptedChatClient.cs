using System.Text.Json;
using Microsoft.Extensions.AI;
using SreAiAgent.Application.Agents;

namespace SreAiAgent.Infrastructure.Llm;

/// <summary>
/// A deterministic <see cref="IChatClient"/> that returns schema-valid responses
/// without calling a provider.
/// </summary>
/// <remarks>
/// This is the V1 model adapter (docs/DECISIONS.md D09). It is not a stub that
/// returns empty objects: it produces responses that satisfy the same JSON
/// Schemas a real model must satisfy, cites evidence ids that were genuinely
/// offered to it, and requests tools the agent is genuinely allowed. That makes
/// the whole pipeline, including validation, evidence linking and correlation,
/// exercisable end to end and in CI, with no network, no cost and no flakiness.
///
/// The scripted responses are keyed off the schema the caller asked for, which is
/// carried in the system prompt by the gateway. A real provider is a one-line DI
/// registration replacing this type; nothing else changes.
/// </remarks>
internal sealed class ScriptedChatClient : IChatClient
{
    private const string ProviderName = "scripted";

    private readonly TimeProvider _timeProvider;

    public ScriptedChatClient(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        string prompt = string.Join('\n', messages.Select(static message => message.Text));
        string json = Compose(prompt);

        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, json))
        {
            ResponseId = $"scripted-{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}",
            ModelId = options?.ModelId ?? ProviderName,
            FinishReason = ChatFinishReason.Stop,
            // Token counts are estimated from length so budget accounting and the
            // cost metric exercise real arithmetic rather than zeros, which would
            // hide a budget bug until a real provider was wired up.
            Usage = new UsageDetails
            {
                InputTokenCount = Math.Max(1, prompt.Length / 4),
                OutputTokenCount = Math.Max(1, json.Length / 4),
            },
        };

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: there is no connection behind this client.
    }

    private static string Compose(string prompt)
    {
        // Schema selection is by prompt content because that is what a real
        // provider sees too. Ordering matters: the correlation prompt also
        // mentions observations, so the more specific markers are tested first.
        if (prompt.Contains("Produce ranked recommendations", StringComparison.Ordinal))
        {
            return Recommendations();
        }

        if (prompt.Contains("Produce the correlated explanation", StringComparison.Ordinal))
        {
            return Correlation(prompt);
        }

        if (prompt.Contains("Classify this incident", StringComparison.Ordinal))
        {
            return Classification(prompt);
        }

        return AgentTurn(prompt);
    }

    private static string Classification(string prompt)
    {
        // Mirrors the deterministic routing signals of ARCHITECTURE.md 11.2 so
        // the scripted path exercises the same branches a real classifier would.
        List<string> types = [];

        if (ContainsAny(prompt, "database", "connection pool", "query", "postgres"))
        {
            types.Add("database");
        }

        if (ContainsAny(prompt, "latency", "slow", "p99", "timeout"))
        {
            types.Add("latency");
        }

        if (ContainsAny(prompt, "pod", "kubernetes", "oomkilled", "crashloop"))
        {
            types.Add("kubernetes");
        }

        if (ContainsAny(prompt, "5xx", "error rate", "exception"))
        {
            types.Add("error-rate");
        }

        if (types.Count == 0)
        {
            types.Add("latency");
        }

        ClassificationPayload payload = new()
        {
            IncidentTypes = types,
            Confidence = types.Count > 1 ? 0.7m : 0.55m,
            Rationale = "Matched keyword signals in the alert title and labels.",
        };

        return JsonSerializer.Serialize(payload, LlmJsonContext.Default.ClassificationPayload);
    }

    private static string AgentTurn(string prompt)
    {
        // First turn: ask for the tools the prompt says are available. Second
        // turn: the prompt now carries evidence ids, so produce a finding that
        // cites them. This is what makes the scripted path exercise the real
        // evidence-linking rules instead of trivially passing them.
        List<string> evidenceIds = ExtractEvidenceIds(prompt);

        if (evidenceIds.Count == 0)
        {
            List<AgentToolRequest> requests = [];

            if (prompt.Contains("prometheus.query_range", StringComparison.Ordinal))
            {
                requests.Add(new AgentToolRequest
                {
                    ToolName = "prometheus.query_range",
                    Arguments = JsonDocument.Parse(
                        """{"metric":"db_connection_pool_waiting","service":"checkout","window_minutes":30}""")
                        .RootElement.Clone(),
                });
            }

            if (prompt.Contains("database.health", StringComparison.Ordinal))
            {
                requests.Add(new AgentToolRequest
                {
                    ToolName = "database.health",
                    Arguments = JsonDocument.Parse("""{"service":"checkout","window_minutes":30}""")
                        .RootElement.Clone(),
                });
            }

            if (prompt.Contains("kubernetes.pods", StringComparison.Ordinal))
            {
                requests.Add(new AgentToolRequest
                {
                    ToolName = "kubernetes.pods",
                    Arguments = JsonDocument.Parse("""{"namespace":"shop","service":"checkout"}""")
                        .RootElement.Clone(),
                });
            }

            if (requests.Count == 0 && prompt.Contains("logs.search", StringComparison.Ordinal))
            {
                requests.Add(new AgentToolRequest
                {
                    ToolName = "logs.search",
                    Arguments = JsonDocument.Parse(
                        """{"service":"checkout","window_minutes":30,"minimum_level":"error"}""")
                        .RootElement.Clone(),
                });
            }

            if (requests.Count > 0)
            {
                return JsonSerializer.Serialize(
                    new AgentTurn { ToolRequests = requests },
                    LlmJsonContext.Default.AgentTurn);
            }
        }

        AgentTurn turn = new()
        {
            Finding = new AgentFindingPayload
            {
                Summary = evidenceIds.Count > 0
                    ? "Connection pool saturation coincides with the latency rise."
                    : "No tools were available, so nothing could be verified.",
                Observations = evidenceIds.Count > 0
                    ?
                    [
                        new AgentObservationPayload
                        {
                            Statement = "Waiting clients on the connection pool rose sharply before the alert fired.",
                            EvidenceIds = [evidenceIds[0]],
                        },
                    ]
                    : [],
                Hypotheses = evidenceIds.Count > 0
                    ?
                    [
                        new AgentHypothesisPayload
                        {
                            Statement = "The connection pool is undersized for current load.",
                            SupportingEvidenceIds = [evidenceIds[0]],
                            ContradictingEvidenceIds = [],
                            Confidence = 0.62m,
                        },
                    ]
                    : [],
                EvidenceGaps = evidenceIds.Count > 0
                    ? ["Slow query statistics were not available for the window."]
                    : ["No evidence could be gathered."],
                RecommendedNextChecks = ["Compare pool utilisation against the configured maximum."],
                Confidence = evidenceIds.Count > 0 ? 0.6m : 0.1m,
            },
        };

        return JsonSerializer.Serialize(turn, LlmJsonContext.Default.AgentTurn);
    }

    private static string Correlation(string prompt)
    {
        List<string> evidenceIds = ExtractEvidenceIds(prompt);

        CorrelationPayload payload = new()
        {
            Summary = evidenceIds.Count > 0
                ? "Latency rose alongside connection pool contention; the database layer is the leading candidate."
                : "No expert produced a verifiable finding, so no cause could be established.",
            Category = evidenceIds.Count > 0 ? "database.connection-pool" : null,
            Confidence = evidenceIds.Count > 0 ? 0.58m : 0.05m,
            KeyObservations = evidenceIds.Count > 0
                ?
                [
                    new AgentObservationPayload
                    {
                        Statement = "Connection pool waiting clients rose before latency did.",
                        EvidenceIds = [evidenceIds[0]],
                    },
                ]
                : [],
            RankedHypotheses = evidenceIds.Count > 0
                ?
                [
                    new AgentHypothesisPayload
                    {
                        Statement = "The connection pool is undersized for current load.",
                        SupportingEvidenceIds = [evidenceIds[0]],
                        ContradictingEvidenceIds = [],
                        Confidence = 0.58m,
                    },
                ]
                : [],
            EvidenceGaps = ["Slow query statistics were unavailable for the window."],
        };

        return JsonSerializer.Serialize(payload, LlmJsonContext.Default.CorrelationPayload);
    }

    private static string Recommendations()
    {
        RecommendationSetPayload payload = new()
        {
            Recommendations =
            [
                new RecommendationPayload
                {
                    Rank = 1,
                    Title = "Compare pool utilisation against the configured maximum",
                    Rationale = "Distinguishes a saturated pool from a slow database before anything is changed.",
                    // Read-only first: the highest-ranked step reduces
                    // uncertainty rather than attempting a fix, matching the
                    // ranking rule in the recommendation prompt.
                    Risk = "read_only",
                    Confidence = 0.7m,
                    EvidenceIds = [],
                },
                new RecommendationPayload
                {
                    Rank = 2,
                    Title = "Raise the connection pool ceiling for the checkout service",
                    Rationale = "If utilisation is pinned at the maximum with clients waiting, the ceiling is the constraint.",
                    Risk = "medium",
                    Confidence = 0.45m,
                    EvidenceIds = [],
                },
            ],
        };

        return JsonSerializer.Serialize(payload, LlmJsonContext.Default.RecommendationSetPayload);
    }

    /// <summary>
    /// Pulls evidence ids out of the prompt so scripted findings cite evidence
    /// that genuinely exists. Fabricating ids would make the scripted path pass
    /// checks that a real model would fail.
    /// </summary>
    private static List<string> ExtractEvidenceIds(string prompt)
    {
        const string Marker = "evidence_id:";
        List<string> ids = [];
        int index = prompt.IndexOf(Marker, StringComparison.Ordinal);

        while (index >= 0 && ids.Count < 8)
        {
            ReadOnlySpan<char> tail = prompt.AsSpan(index + Marker.Length).TrimStart();
            if (tail.Length >= 36 && Guid.TryParse(tail[..36], out Guid id))
            {
                ids.Add(id.ToString("D", CultureInfo.InvariantCulture));
            }

            index = prompt.IndexOf(Marker, index + Marker.Length, StringComparison.Ordinal);
        }

        return ids;
    }

    private static bool ContainsAny(string haystack, params ReadOnlySpan<string> needles)
    {
        foreach (string needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
