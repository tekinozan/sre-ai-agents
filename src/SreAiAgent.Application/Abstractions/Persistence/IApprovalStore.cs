using SreAiAgent.Contracts.Approvals;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Persistence;

/// <summary>
/// Reads and writes approval requests (ARCHITECTURE.md 12.1).
/// </summary>
/// <remarks>
/// V1 has no path that creates one: every tool is read-only, so nothing is ever
/// proposed that a human would need to authorize. The store, the endpoints and
/// the table exist anyway, because the alternative is to discover on the day
/// write actions ship that approval was designed around whatever was convenient
/// then. Section 20 puts execution in V2 and the approval surface in V1
/// deliberately.
/// </remarks>
public interface IApprovalStore
{
    /// <summary>Loads a request for a decision, with change tracking on.</summary>
    Task<ApprovalRequest?> GetAsync(
        string tenant,
        ApprovalRequestId approvalRequestId,
        CancellationToken cancellationToken);

    /// <summary>Projects one request into its response shape.</summary>
    Task<ApprovalResponse?> FindAsync(
        string tenant,
        ApprovalRequestId approvalRequestId,
        CancellationToken cancellationToken);

    /// <summary>Lists requests, most recent first, optionally filtered by status.</summary>
    Task<IReadOnlyList<ApprovalResponse>> ListAsync(
        string tenant,
        ApprovalStatus? status,
        int limit,
        CancellationToken cancellationToken);
}
