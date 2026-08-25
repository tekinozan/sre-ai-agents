using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Workflow;

/// <summary>
/// What kind of failure this appears to be, and how that was decided.
/// </summary>
/// <remarks>
/// <paramref name="Method"/> is recorded because it matters operationally.
/// Deterministic routing that matched on a component label is auditable and free;
/// an LLM classifier is neither. ARCHITECTURE.md 11.2 puts the classifier behind
/// deterministic signals, and keeping the method on the result is what lets the
/// evaluation suite measure how often the fallback was actually needed.
/// </remarks>
/// <param name="IncidentTypes">Matched types, for example <c>database</c> or <c>latency</c>.</param>
/// <param name="Method">How the classification was reached.</param>
/// <param name="Confidence">Confidence in the classification.</param>
/// <param name="Rationale">Short, redacted explanation for the timeline.</param>
public sealed record IncidentClassification(
    IReadOnlyList<string> IncidentTypes,
    ClassificationMethod Method,
    Confidence Confidence,
    string Rationale);

/// <summary>How an incident was classified.</summary>
public enum ClassificationMethod
{
    /// <summary>Matched a deterministic rule on labels, component or signal shape.</summary>
    Deterministic = 0,

    /// <summary>Deterministic signals were insufficient, so a model was consulted.</summary>
    LlmClassifier = 1,

    /// <summary>Neither produced a usable answer; a default agent set applies.</summary>
    Fallback = 2,
}
