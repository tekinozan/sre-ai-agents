using Microsoft.EntityFrameworkCore;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Persistence.Queries;

/// <summary>
/// Read-side projections straight into response contracts.
/// </summary>
/// <remarks>
/// Every query here projects in the database and materializes the response shape
/// directly. The alternative, loading aggregates and mapping them, would pull
/// whole rows across the wire so six fields could be copied off each one, and
/// <c>GET /v1/incidents/{id}/evidence</c> can face thousands of rows whose content
/// column is the largest thing in the schema.
///
/// Every method is tenant-scoped. Passing the tenant explicitly rather than
/// applying a global filter keeps the isolation visible at the call site: a
/// missing tenant is a compile error, whereas a global filter someone disabled for
/// a background job is a cross-tenant read nobody notices.
/// </remarks>
internal sealed class IncidentQueries : IIncidentQueries
{
    private readonly SreDbContext _context;

    public IncidentQueries(SreDbContext context) => _context = context;

    /// <inheritdoc />
    public Task<IncidentResponse?> GetIncidentAsync(
        string tenant,
        IncidentId incidentId,
        CancellationToken cancellationToken)
        => _context.Incidents
            .Where(incident => incident.Tenant == tenant && incident.Id == incidentId)
            .Select(incident => new IncidentResponse
            {
                IncidentId = incident.Id,
                Tenant = incident.Tenant,
                Source = incident.Source,
                Fingerprint = incident.Fingerprint,
                Title = incident.Title,
                Description = incident.Description,
                Service = incident.Service,
                Environment = incident.Environment,
                Severity = incident.Severity,
                Status = incident.Status,
                AlertCount = incident.AlertCount,
                DetectedAt = incident.DetectedAt,
                CreatedAt = incident.CreatedAt,
                UpdatedAt = incident.UpdatedAt,
                ResolvedAt = incident.ResolvedAt,
                ConfigurationSnapshotId = incident.ConfigurationSnapshotId,
                PendingApprovalRequestId = incident.PendingApprovalRequestId,
                FailureCode = incident.FailureCode == null ? null : incident.FailureCode.Value.Value,
                FailureReason = incident.FailureReason,
                Labels = incident.Labels,
            })
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<IncidentTimelineEntryResponse>> GetTimelineAsync(
        string tenant,
        IncidentId incidentId,
        int limit,
        CancellationToken cancellationToken)
        // Joined against incidents so a caller cannot read another tenant's
        // timeline by guessing an incident id.
        => await (from entry in _context.IncidentEvents
                  join incident in _context.Incidents on entry.IncidentId equals incident.Id
                  where incident.Tenant == tenant && entry.IncidentId == incidentId
                  orderby entry.Id
                  select new IncidentTimelineEntryResponse(
                      entry.Id, entry.EventType, entry.Summary, entry.OccurredAt, entry.Actor))
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<IncidentTimelineEntryResponse>> GetStreamEventsAsync(
        string tenant,
        IncidentId incidentId,
        int delivered,
        int limit,
        CancellationToken cancellationToken)
        => await (from entry in _context.IncidentEvents
                  join incident in _context.Incidents on entry.IncidentId equals incident.Id
                  where incident.Tenant == tenant && entry.IncidentId == incidentId
                  orderby entry.Id
                  select new IncidentTimelineEntryResponse(
                      entry.Id, entry.EventType, entry.Summary, entry.OccurredAt, entry.Actor))
            .Skip(delivered)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IncidentStatus?> GetStatusAsync(
        string tenant,
        IncidentId incidentId,
        CancellationToken cancellationToken)
    {
        // Projected to a nullable so "no such incident" and "status is Open" stay
        // distinguishable; a stream that cannot tell them apart would poll a
        // deleted incident forever.
        List<IncidentStatus> status = await _context.Incidents
            .Where(incident => incident.Tenant == tenant && incident.Id == incidentId)
            .Select(incident => incident.Status)
            .Take(1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return status.Count == 0 ? null : status[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvidenceResponse>> GetEvidenceAsync(
        string tenant,
        IncidentId incidentId,
        int limit,
        CancellationToken cancellationToken)
        => await (from evidence in _context.Evidence
                  join incident in _context.Incidents on evidence.IncidentId equals incident.Id
                  where incident.Tenant == tenant && evidence.IncidentId == incidentId
                  orderby evidence.Id
                  select new EvidenceResponse
                  {
                      EvidenceId = evidence.Id,
                      SourceType = evidence.SourceType,
                      Provider = evidence.Provider,
                      ToolName = evidence.ToolName,
                      ToolVersion = evidence.ToolVersion,
                      Summary = evidence.Summary,
                      ContentJson = evidence.ContentJson,
                      ContentHash = evidence.ContentHash,
                      RedactionStatus = evidence.RedactionStatus,
                      RedactedValueCount = evidence.RedactedValueCount,
                      IsTruncated = evidence.IsTruncated,
                      ObservationWindowStart = evidence.ObservationWindowStart,
                      ObservationWindowEnd = evidence.ObservationWindowEnd,
                      CollectedAt = evidence.CollectedAt,
                  })
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<RootCauseReportResponse?> GetReportAsync(
        string tenant,
        IncidentId incidentId,
        int? version,
        CancellationToken cancellationToken)
    {
        var report = await (from item in _context.RootCauseReports
                            join incident in _context.Incidents on item.IncidentId equals incident.Id
                            where incident.Tenant == tenant
                                  && item.IncidentId == incidentId
                                  && (version == null || item.Version == version)
                            orderby item.Version descending
                            select new
                            {
                                item.Id,
                                item.IncidentId,
                                item.Version,
                                item.Outcome,
                                item.Summary,
                                item.Category,
                                item.Confidence,
                                item.KeyObservations,
                                item.RankedHypotheses,
                                item.EvidenceGaps,
                                item.ContributingAgentNames,
                                item.ConfigurationSnapshotId,
                                item.CreatedAt,
                            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (report is null)
        {
            return null;
        }

        // Recommendations are a separate query rather than a join. Joining would
        // multiply the report's JSONB columns by the number of recommendations,
        // and those columns hold the observations and hypotheses.
        List<RecommendationResponse> recommendations = await _context.Recommendations
            .Where(recommendation => recommendation.RootCauseReportId == report.Id)
            .OrderBy(recommendation => recommendation.Rank)
            .Select(recommendation => new RecommendationResponse(
                recommendation.Id,
                recommendation.Rank,
                recommendation.Title,
                recommendation.Rationale,
                recommendation.Risk,
                recommendation.Confidence.Value,
                recommendation.EvidenceIds,
                recommendation.Risk > Domain.Remediation.RiskLevel.ReadOnly))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new RootCauseReportResponse
        {
            ReportId = report.Id,
            IncidentId = report.IncidentId,
            Version = report.Version,
            Outcome = report.Outcome,
            Summary = report.Summary,
            Category = report.Category,
            Confidence = report.Confidence.Value,
            KeyObservations = [.. report.KeyObservations.Select(static observation =>
                new ObservationResponse(observation.Statement, observation.EvidenceIds, observation.IsVerified))],
            RankedHypotheses = [.. report.RankedHypotheses.Select(static hypothesis =>
                new HypothesisResponse(
                    hypothesis.Statement,
                    hypothesis.SupportingEvidenceIds,
                    hypothesis.ContradictingEvidenceIds,
                    hypothesis.Confidence.Value))],
            EvidenceGaps = report.EvidenceGaps,
            Recommendations = recommendations,
            ContributingAgents = report.ContributingAgentNames,
            ConfigurationSnapshotId = report.ConfigurationSnapshotId,
            CreatedAt = report.CreatedAt,
        };
    }
}
