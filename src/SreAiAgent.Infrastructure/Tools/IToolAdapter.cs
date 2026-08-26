using System.Text.Json;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Domain.Evidence;

namespace SreAiAgent.Infrastructure.Tools;

/// <summary>
/// Translates one validated tool request into a backend call.
/// </summary>
/// <remarks>
/// An adapter is reached only after the gateway has run the whole policy
/// pipeline, so it can treat its arguments as trustworthy and concentrate on the
/// backend. Equally, an adapter must never be called from anywhere but the
/// gateway: doing so would skip authorization, the risk ceiling, rate limiting,
/// the result cap, redaction and the audit record in one step.
/// </remarks>
internal interface IToolAdapter
{
    /// <summary>Registry name this adapter serves, for example <c>logs.search</c>.</summary>
    string ToolName { get; }

    /// <summary>Runs the backend call and returns content ready for redaction.</summary>
    Task<ToolAdapterResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// What an adapter produces: a human-readable summary plus structured content.
/// </summary>
/// <remarks>
/// The summary matters more than it looks. It is what an agent reads first and
/// what appears on the evidence row, so an adapter that returns raw JSON with no
/// summary forces the model to interpret the shape of a payload before it can
/// reason about the incident.
/// </remarks>
/// <param name="Summary">One line describing what the backend returned.</param>
/// <param name="ContentJson">Structured result, before redaction and capping.</param>
/// <param name="SourceType">Which kind of evidence this is.</param>
/// <param name="Provider">Backend that answered, for example <c>prometheus</c>.</param>
/// <param name="WindowStart">Start of the observation window, when applicable.</param>
/// <param name="WindowEnd">End of the observation window, when applicable.</param>
/// <param name="IsPartial">
/// True when the backend answered from incomplete data. Propagated onto the
/// evidence so an agent cannot read "no errors" from a partial scrape.
/// </param>
internal sealed record ToolAdapterResult(
    string Summary,
    string ContentJson,
    EvidenceSourceType SourceType,
    string Provider,
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd,
    bool IsPartial = false);
