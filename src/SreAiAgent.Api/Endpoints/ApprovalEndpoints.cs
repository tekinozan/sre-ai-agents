using System.Security.Claims;
using SreAiAgent.Api.Http;
using SreAiAgent.Api.Security;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.UseCases;
using SreAiAgent.Contracts.Approvals;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Api.Endpoints;

/// <summary>The approval surface of ARCHITECTURE.md 12.1.</summary>
/// <remarks>
/// V1 never produces an approval request, because every tool is read-only and
/// there is nothing to authorize. These endpoints are here so the contract, the
/// authorization boundary and the audit trail exist before the first write
/// action does, rather than being designed under time pressure alongside it.
/// Until then they correctly return an empty list.
/// </remarks>
public static class ApprovalEndpoints
{
    private const int DefaultReadLimit = 100;

    /// <summary>Maps the approval endpoints.</summary>
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder approvals = builder.MapGroup("/v1/approvals").WithTags("Approvals");

        // Reading a pending approval is part of triage, so it follows the read
        // policy. Deciding one is separated deliberately (ARCHITECTURE.md 15.1).
        approvals.MapGet("/", ListAsync).RequireAuthorization(SrePolicies.ReadIncidents);
        approvals.MapGet("/{approvalId:guid}", GetAsync).RequireAuthorization(SrePolicies.ReadIncidents);

        approvals.MapPost("/{approvalId:guid}/approve", ApproveAsync)
            .RequireAuthorization(SrePolicies.DecideApprovals);

        approvals.MapPost("/{approvalId:guid}/reject", RejectAsync)
            .RequireAuthorization(SrePolicies.DecideApprovals);

        approvals.MapPost("/{approvalId:guid}/edit", EditAsync)
            .RequireAuthorization(SrePolicies.DecideApprovals);

        return builder;
    }

    private static async Task<IResult> ListAsync(
        ApprovalStatus? status,
        ClaimsPrincipal principal,
        IApprovalStore approvals,
        CancellationToken cancellationToken)
        => Results.Json(await approvals
            .ListAsync(principal.Tenant(), status, DefaultReadLimit, cancellationToken)
            .ConfigureAwait(false));

    private static async Task<IResult> GetAsync(
        Guid approvalId,
        ClaimsPrincipal principal,
        IApprovalStore approvals,
        CancellationToken cancellationToken)
        => (await approvals
                .FindAsync(principal.Tenant(), ApprovalRequestId.From(approvalId), cancellationToken)
                .ConfigureAwait(false))
            .ToHttpResult("No such approval request.");

    private static async Task<IResult> ApproveAsync(
        Guid approvalId,
        ApproveRequest request,
        ClaimsPrincipal principal,
        ApprovalDecisionHandler handler,
        CancellationToken cancellationToken)
        => (await handler
                .DecideAsync(
                    principal.Tenant(),
                    ApprovalRequestId.From(approvalId),
                    isApproved: true,
                    principal.Actor(),
                    request.Comment,
                    cancellationToken)
                .ConfigureAwait(false))
            .ToHttpResult();

    private static async Task<IResult> RejectAsync(
        Guid approvalId,
        RejectRequest request,
        ClaimsPrincipal principal,
        ApprovalDecisionHandler handler,
        CancellationToken cancellationToken)
        => (await handler
                .DecideAsync(
                    principal.Tenant(),
                    ApprovalRequestId.From(approvalId),
                    isApproved: false,
                    principal.Actor(),
                    request.Reason,
                    cancellationToken)
                .ConfigureAwait(false))
            .ToHttpResult();

    private static async Task<IResult> EditAsync(
        Guid approvalId,
        EditApprovalRequest request,
        ClaimsPrincipal principal,
        ApprovalDecisionHandler handler,
        CancellationToken cancellationToken)
        => (await handler
                .EditAsync(
                    principal.Tenant(),
                    ApprovalRequestId.From(approvalId),
                    principal.Actor(),
                    request.ProposedActionJson,
                    request.Risk,
                    cancellationToken)
                .ConfigureAwait(false))
            .ToHttpResult();
}
