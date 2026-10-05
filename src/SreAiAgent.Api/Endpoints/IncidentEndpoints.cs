using System.Security.Claims;
using Microsoft.Extensions.Options;
using SreAiAgent.Api.Http;
using SreAiAgent.Api.Security;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.UseCases;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Api.Endpoints;

/// <summary>The incident surface of ARCHITECTURE.md 12.1.</summary>
/// <remarks>
/// Every endpoint here is a delegation. Nothing decides anything: the write
/// paths call a handler and map its Result, and the read paths call a query port
/// that already projects into the response contract. A rule that lived in an
/// endpoint would apply only to callers that arrived through HTTP, which is the
/// one guarantee an alert consumer or a worker could not rely on.
/// </remarks>
public static class IncidentEndpoints
{
    private const int DefaultReadLimit = 200;

    /// <summary>Maps the incident endpoints.</summary>
    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder incidents = builder.MapGroup("/v1/incidents").WithTags("Incidents");

        incidents.MapPost("/", CreateAsync)
            .RequireAuthorization(SrePolicies.WriteIncidents)
            .WithSummary("Reports an alert, opening an incident or folding into the existing one.");

        incidents.MapGet("/{incidentId:guid}", GetAsync)
            .RequireAuthorization(SrePolicies.ReadIncidents);

        incidents.MapGet("/{incidentId:guid}/timeline", GetTimelineAsync)
            .RequireAuthorization(SrePolicies.ReadIncidents);

        incidents.MapGet("/{incidentId:guid}/evidence", GetEvidenceAsync)
            .RequireAuthorization(SrePolicies.ReadIncidents);

        incidents.MapGet("/{incidentId:guid}/report", GetReportAsync)
            .RequireAuthorization(SrePolicies.ReadIncidents);

        incidents.MapPost("/{incidentId:guid}/investigate", InvestigateAsync)
            .RequireAuthorization(SrePolicies.WriteIncidents);

        incidents.MapPost("/{incidentId:guid}/retry", RetryAsync)
            .RequireAuthorization(SrePolicies.WriteIncidents);

        incidents.MapPost("/{incidentId:guid}/cancel", CancelAsync)
            .RequireAuthorization(SrePolicies.WriteIncidents);

        return builder;
    }

    /// <remarks>
    /// Answers 202 rather than 201. The work is asynchronous, and a 201 would
    /// promise a resource that is complete when what exists so far is an incident
    /// whose investigation has not started (ARCHITECTURE.md 12.1).
    /// </remarks>
    private static async Task<IResult> CreateAsync(
        CreateIncidentRequest request,
        ClaimsPrincipal principal,
        CreateIncidentHandler handler,
        IOptions<DatabaseOptions> database,
        CancellationToken cancellationToken)
    {
        Result<IncidentAcceptedResponse> result = await handler.HandleAsync(
            request,
            principal.Tenant(),
            TimeSpan.FromMinutes(database.Value.IncidentDeduplicationWindowMinutes),
            cancellationToken).ConfigureAwait(false);

        return result.ToHttpResult(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> GetAsync(
        Guid incidentId,
        ClaimsPrincipal principal,
        IIncidentQueries queries,
        CancellationToken cancellationToken)
        => (await queries
                .GetIncidentAsync(principal.Tenant(), IncidentId.From(incidentId), cancellationToken)
                .ConfigureAwait(false))
            .ToHttpResult("No such incident.");

    private static async Task<IResult> GetTimelineAsync(
        Guid incidentId,
        ClaimsPrincipal principal,
        IIncidentQueries queries,
        CancellationToken cancellationToken)
        => Results.Json(await queries
            .GetTimelineAsync(principal.Tenant(), IncidentId.From(incidentId), DefaultReadLimit, cancellationToken)
            .ConfigureAwait(false));

    private static async Task<IResult> GetEvidenceAsync(
        Guid incidentId,
        ClaimsPrincipal principal,
        IIncidentQueries queries,
        CancellationToken cancellationToken)
        => Results.Json(await queries
            .GetEvidenceAsync(principal.Tenant(), IncidentId.From(incidentId), DefaultReadLimit, cancellationToken)
            .ConfigureAwait(false));

    private static async Task<IResult> GetReportAsync(
        Guid incidentId,
        int? version,
        ClaimsPrincipal principal,
        IIncidentQueries queries,
        CancellationToken cancellationToken)
        => (await queries
                .GetReportAsync(principal.Tenant(), IncidentId.From(incidentId), version, cancellationToken)
                .ConfigureAwait(false))
            .ToHttpResult("No report has been produced for this incident yet.");

    private static async Task<IResult> InvestigateAsync(
        Guid incidentId,
        ClaimsPrincipal principal,
        StartInvestigationHandler handler,
        CancellationToken cancellationToken)
        => (await handler.HandleAsync(principal.Tenant(), IncidentId.From(incidentId), cancellationToken).ConfigureAwait(false))
            .ToHttpResult(StatusCodes.Status202Accepted);

    private static async Task<IResult> RetryAsync(
        Guid incidentId,
        IncidentActionRequest request,
        ClaimsPrincipal principal,
        RetryInvestigationHandler handler,
        CancellationToken cancellationToken)
        => (await handler
                .HandleAsync(principal.Tenant(), IncidentId.From(incidentId), principal.Actor(), request.Reason, cancellationToken)
                .ConfigureAwait(false))
            .ToHttpResult(StatusCodes.Status202Accepted);

    private static async Task<IResult> CancelAsync(
        Guid incidentId,
        IncidentActionRequest request,
        ClaimsPrincipal principal,
        CancelInvestigationHandler handler,
        CancellationToken cancellationToken)
        => (await handler
                .HandleAsync(principal.Tenant(), IncidentId.From(incidentId), principal.Actor(), request.Reason, cancellationToken)
                .ConfigureAwait(false))
            .ToHttpResult();
}
