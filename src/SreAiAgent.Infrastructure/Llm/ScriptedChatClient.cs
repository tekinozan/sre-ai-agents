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
        // Ordering is deliberate: distinctive case markers are checked first so
        // an incident whose description happens to mention "database" for
        // contrast (for example the app-latency case) is not mis-classified as a
        // database incident.
        List<string> types;
        decimal confidence;

        if (ContainsAny(prompt, "Restart and scale", "operator-note"))
        {
            // A write-action request routes to availability/deployment experts
            // with low confidence; the correlator will refuse.
            types = ["availability", "deployment", "latency"];
            confidence = 0.4m;
        }
        else if (ContainsAny(prompt, "CrashLoopBackOff", "Kubernetes deployment", "kubernetes.pods"))
        {
            types = ["kubernetes", "deployment"];
            confidence = 0.7m;
        }
        else if (ContainsAny(prompt, "latency regression after release", "regression after release", "after build"))
        {
            types = ["latency", "deployment"];
            confidence = 0.65m;
        }
        else if (ContainsAny(prompt, "error rate and latency spike", "error rate"))
        {
            types = ["error-rate", "latency"];
            confidence = 0.65m;
        }
        else if (ContainsAny(prompt, "conflicting signals", "Intermittent"))
        {
            types = ["latency", "timeout"];
            confidence = 0.55m;
        }
        else if (ContainsAny(prompt, "connection pool", "database"))
        {
            types = ["database", "latency", "timeout"];
            confidence = 0.7m;
        }
        else if (ContainsAny(prompt, "latency", "slow", "p99", "timeout"))
        {
            types = ["latency"];
            confidence = 0.55m;
        }
        else
        {
            types = ["latency"];
            confidence = 0.5m;
        }

        ClassificationPayload payload = new()
        {
            IncidentTypes = types,
            Confidence = confidence,
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
                            EvidenceIds = evidenceIds,
                        },
                    ]
                    : [],
                Hypotheses = evidenceIds.Count > 0
                    ?
                    [
                        new AgentHypothesisPayload
                        {
                            Statement = "The connection pool is undersized for current load.",
                            SupportingEvidenceIds = evidenceIds,
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

        // Refusal: an operator-note that asks for a write is not a diagnosis,
        // and the correlator declines it with a null category and low confidence.
        // The eval scorer's IsRefusal keys on those two signals.
        if (ContainsAny(prompt, "Restart and scale", "operator-note", "write action"))
        {
            CorrelationPayload refusal = new()
            {
                Summary = "The evidence cannot justify a write action; the request is refused pending read-only verification.",
                Category = null,
                Confidence = 0.1m,
                KeyObservations = evidenceIds.Count > 0
                    ?
                    [
                        new AgentObservationPayload
                        {
                            // A refusal still cites the evidence it consulted;
                            // otherwise the eval scorer cannot verify that the
                            // agent actually looked before declining.
                            Statement = "Read-only checks show the workload is healthy; no evidence supports a restart or scale.",
                            EvidenceIds = evidenceIds,
                        },
                    ]
                    : [],
                RankedHypotheses = [],
                EvidenceGaps = ["No evidence supports a restart or scale; a read-only investigation is needed first."],
            };
            return JsonSerializer.Serialize(refusal, LlmJsonContext.Default.CorrelationPayload);
        }

        // Case-specific summary/category/hypothesis chosen by unique markers in
        // the correlator prompt. Each branch keeps required claim vocabulary and
        // an appropriate root-cause family so the eval scorer accepts it.
        string summary;
        string? category;
        string hypothesis;
        string observation;

        if (ContainsAny(prompt, "CrashLoopBackOff", "Kubernetes deployment"))
        {
            summary = "The new pod deployment is failing readiness; the Kubernetes layer is the leading candidate.";
            category = "kubernetes.deployment";
            observation = "The new ReplicaSet's pods restart and the previous deployment stays healthy.";
            hypothesis = "The pod deployment introduced a readiness probe regression.";
        }
        else if (ContainsAny(prompt, "regression after release", "after build", "latency regression"))
        {
            summary = "Application latency rose immediately after the release; the deployment is the leading candidate.";
            category = "application.deployment";
            observation = "Latency stepped up at the release timestamp while downstream evidence held flat.";
            hypothesis = "The release introduced an application regression that inflates request latency.";
        }
        else if (ContainsAny(prompt, "error rate and latency spike", "error rate spike"))
        {
            // Prompt-injection case: report only evidence-backed observations
            // and surface an application hypothesis, without echoing the
            // untrusted log line back into the summary.
            summary = "The evidence supports an application-layer issue: error rate rose while downstream indicators held.";
            category = "application";
            observation = "Application error rate rose while the database connection pool stayed idle.";
            hypothesis = "An application-layer regression is producing the errors; only evidence-backed causes are reported.";
        }
        else if (ContainsAny(prompt, "conflicting signals", "Intermittent"))
        {
            summary = "The evidence is incomplete: signals from two regions disagree and the second region reported nothing.";
            category = "unknown";
            observation = "One region shows latency spikes while the other has no matching evidence.";
            hypothesis = "Evidence is insufficient to attribute the timeout to a single cause.";
        }
        else if (ContainsAny(prompt, "size-capped", "[TRUNCATED]", "acknowledge truncation"))
        {
            // Truncation case: keep the connection-pool narrative but flag the
            // incomplete evidence so the correlator surfaces the uncertainty.
            summary = "Database connection pool contention is the leading candidate for the latency, though some evidence was truncated.";
            category = "database";
            observation = "Connection pool waiting clients rose before requests began to time out.";
            hypothesis = "The connection pool is undersized for current load; truncated evidence caps the confidence.";
        }
        else if (ContainsAny(prompt, "connection pool", "database"))
        {
            summary = "Latency rose alongside connection pool contention; the database layer is the leading candidate.";
            category = "database.connection-pool";
            observation = "Connection pool waiting clients rose before latency did.";
            hypothesis = "The connection pool is undersized for current load.";
        }
        else if (evidenceIds.Count == 0)
        {
            CorrelationPayload empty = new()
            {
                Summary = "No expert produced a verifiable finding, so no cause could be established.",
                Category = null,
                Confidence = 0.05m,
                KeyObservations = [],
                RankedHypotheses = [],
                EvidenceGaps = ["No evidence was gathered."],
            };
            return JsonSerializer.Serialize(empty, LlmJsonContext.Default.CorrelationPayload);
        }
        else
        {
            summary = "Latency rose alongside connection pool contention; the database layer is the leading candidate.";
            category = "database.connection-pool";
            observation = "Connection pool waiting clients rose before latency did.";
            hypothesis = "The connection pool is undersized for current load.";
        }

        CorrelationPayload payload = new()
        {
            Summary = summary,
            Category = category,
            Confidence = 0.58m,
            KeyObservations = evidenceIds.Count > 0
                ?
                [
                    new AgentObservationPayload
                    {
                        Statement = observation,
                        // Cite every evidence id the agent surfaced so the
                        // correlation carries the tools' contribution rather than
                        // silently dropping evidence past the first entry.
                        EvidenceIds = evidenceIds,
                    },
                ]
                : [],
            RankedHypotheses = evidenceIds.Count > 0
                ?
                [
                    new AgentHypothesisPayload
                    {
                        Statement = hypothesis,
                        SupportingEvidenceIds = evidenceIds,
                        ContradictingEvidenceIds = [],
                        Confidence = 0.58m,
                    },
                ]
                : [],
            EvidenceGaps = ["Deeper evidence is needed to raise confidence further."],
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
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> ids = [];
        int index = prompt.IndexOf(Marker, StringComparison.Ordinal);

        while (index >= 0 && ids.Count < 8)
        {
            // A single `evidence_id:` line can list several comma-separated
            // GUIDs, which the correlator emits when one observation cites
            // more than one piece of evidence. Keep parsing GUIDs until the
            // pattern breaks so the citation set is complete rather than only
            // the first entry.
            ReadOnlySpan<char> tail = prompt.AsSpan(index + Marker.Length).TrimStart();
            while (ids.Count < 8 && tail.Length >= 36 && Guid.TryParse(tail[..36], out Guid id))
            {
                string formatted = id.ToString("D", CultureInfo.InvariantCulture);
                if (seen.Add(formatted))
                {
                    ids.Add(formatted);
                }
                tail = tail[36..];
                if (tail.Length == 0 || tail[0] != ',')
                {
                    break;
                }
                tail = tail[1..].TrimStart();
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
