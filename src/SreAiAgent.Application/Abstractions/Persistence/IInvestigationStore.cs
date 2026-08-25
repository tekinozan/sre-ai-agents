using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Recommendations;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Persistence;

/// <summary>
/// Write access to everything an investigation produces: evidence, agent runs,
/// findings, reports and recommendations.
/// </summary>
/// <remarks>
/// Grouped into one port because these are written together by workflow
/// activities and never independently. Splitting them into five interfaces would
/// produce five constructor parameters that are always injected as a set.
/// </remarks>
public interface IInvestigationStore
{
    void AddEvidence(EvidenceRecord evidence);

    void AddAgentRun(AgentRun agentRun);

    void AddFinding(Finding finding);

    void AddReport(RootCauseReport report);

    void AddRecommendations(IReadOnlyList<Recommendation> recommendations);

    /// <summary>Loads an agent run for accounting updates during execution.</summary>
    Task<AgentRun?> GetAgentRunAsync(AgentRunId agentRunId, CancellationToken cancellationToken);

    /// <summary>
    /// Non-shadow findings for an incident, used by correlation. Shadow runs are
    /// excluded by the implementation, not by the caller: leaving that filter to
    /// each call site is how a shadow result eventually reaches a real report
    /// (ARCHITECTURE.md 24.6).
    /// </summary>
    Task<IReadOnlyList<Finding>> GetFindingsAsync(IncidentId incidentId, CancellationToken cancellationToken);

    /// <summary>
    /// Evidence collected so far, for handing prior context to a later agent.
    /// Only entries that passed redaction are returned.
    /// </summary>
    Task<IReadOnlyList<EvidenceRecord>> GetUsableEvidenceAsync(
        IncidentId incidentId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// The next report version for an incident, so a retry produces n+1 rather
    /// than colliding with UNIQUE(incident_id, report_version).
    /// </summary>
    Task<int> GetNextReportVersionAsync(IncidentId incidentId, CancellationToken cancellationToken);
}
