using Microsoft.EntityFrameworkCore;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Recommendations;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Persistence.Repositories;

/// <summary>
/// Write access to everything an investigation produces.
/// </summary>
internal sealed class InvestigationStore : IInvestigationStore
{
    private readonly SreDbContext _context;

    public InvestigationStore(SreDbContext context) => _context = context;

    /// <inheritdoc />
    public void AddEvidence(EvidenceRecord evidence) => _context.Evidence.Add(evidence);

    /// <inheritdoc />
    public void AddAgentRun(AgentRun agentRun) => _context.AgentRuns.Add(agentRun);

    /// <inheritdoc />
    public void AddFinding(Finding finding) => _context.Findings.Add(finding);

    /// <inheritdoc />
    public void AddReport(RootCauseReport report) => _context.RootCauseReports.Add(report);

    /// <inheritdoc />
    public void AddRecommendations(IReadOnlyList<Recommendation> recommendations)
        => _context.Recommendations.AddRange(recommendations);

    /// <inheritdoc />
    public Task<AgentRun?> GetAgentRunAsync(AgentRunId agentRunId, CancellationToken cancellationToken)
        => _context.AgentRuns.AsTracking().FirstOrDefaultAsync(run => run.Id == agentRunId, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Shadow findings are excluded here rather than by each caller. Leaving that
    /// filter to every call site is how a shadow result eventually reaches a real
    /// report, and the whole point of shadow mode is that it cannot
    /// (ARCHITECTURE.md 24.6).
    /// </remarks>
    public async Task<IReadOnlyList<Finding>> GetFindingsAsync(
        IncidentId incidentId,
        CancellationToken cancellationToken)
        => await _context.Findings
            .Where(finding => finding.IncidentId == incidentId && !finding.IsShadow)
            .OrderBy(finding => finding.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// Only evidence that passed redaction is returned. Content that was never
    /// redacted, or whose redaction failed, must not reach a prompt regardless of
    /// how badly an agent wants it (ARCHITECTURE.md 15.3).
    /// </remarks>
    public async Task<IReadOnlyList<EvidenceRecord>> GetUsableEvidenceAsync(
        IncidentId incidentId,
        int limit,
        CancellationToken cancellationToken)
        => await _context.Evidence
            .Where(evidence => evidence.IncidentId == incidentId
                               && (evidence.RedactionStatus == RedactionStatus.Clean
                                   || evidence.RedactionStatus == RedactionStatus.Redacted))
            .OrderByDescending(evidence => evidence.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// Best-effort only. The authoritative guard is the unique index on
    /// (incident_id, report_version): two concurrent retries would both read the
    /// same "next" value here, and the database decides which one wins.
    /// </remarks>
    public async Task<int> GetNextReportVersionAsync(IncidentId incidentId, CancellationToken cancellationToken)
    {
        int highest = await _context.RootCauseReports
            .Where(report => report.IncidentId == incidentId)
            .Select(report => (int?)report.Version)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;

        return highest + 1;
    }
}
