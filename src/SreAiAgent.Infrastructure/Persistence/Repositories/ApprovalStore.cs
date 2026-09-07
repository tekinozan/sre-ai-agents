using Microsoft.EntityFrameworkCore;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Contracts.Approvals;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Persistence.Repositories;

/// <summary>Reads and writes approval requests.</summary>
/// <remarks>
/// Reads join through the incident so a caller cannot reach another tenant's
/// approvals by guessing an identifier. The approval table has no tenant column
/// of its own, and adding one would create a second place for the same fact to
/// be wrong.
/// </remarks>
internal sealed class ApprovalStore(SreDbContext context) : IApprovalStore
{
    /// <inheritdoc />
    public async Task<ApprovalRequest?> GetAsync(
        string tenant,
        ApprovalRequestId approvalRequestId,
        CancellationToken cancellationToken)
        => await context.ApprovalRequests
            .AsTracking()
            .Where(request => request.Id == approvalRequestId
                && context.Incidents.Any(incident =>
                    incident.Id == request.IncidentId && incident.Tenant == tenant))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<ApprovalResponse?> FindAsync(
        string tenant,
        ApprovalRequestId approvalRequestId,
        CancellationToken cancellationToken)
        => await Project(tenant)
            .Where(response => response.ApprovalRequestId == approvalRequestId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApprovalResponse>> ListAsync(
        string tenant,
        ApprovalStatus? status,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<ApprovalResponse> query = Project(tenant);

        if (status is { } required)
        {
            query = query.Where(response => response.Status == required);
        }

        // Identifiers are version 7 GUIDs, so descending by id is
        // newest-first without a sort column or an index of its own.
        return await query
            .OrderByDescending(response => response.ApprovalRequestId)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private IQueryable<ApprovalResponse> Project(string tenant)
        => from request in context.ApprovalRequests
           join incident in context.Incidents on request.IncidentId equals incident.Id
           where incident.Tenant == tenant
           select new ApprovalResponse
           {
               ApprovalRequestId = request.Id,
               IncidentId = request.IncidentId,
               RecommendationId = request.RecommendationId,
               Title = request.Title,
               Justification = request.Justification,
               Risk = request.Risk,
               ProposedActionJson = request.ProposedActionJson,
               Status = request.Status,
               RequestedAt = request.RequestedAt,
               ExpiresAt = request.ExpiresAt,
               DecidedAt = request.DecidedAt,
               DecidedBy = request.DecidedBy,
               DecisionReason = request.DecisionReason,
           };
}
