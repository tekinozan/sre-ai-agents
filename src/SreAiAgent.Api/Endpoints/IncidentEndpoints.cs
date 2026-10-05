
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SreAiAgent.Api.Http;
using SreAiAgent.Api.Security;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Errors;
using SreAiAgent.Application.UseCases;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Domain.Incidents;
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
    private const int DefaultListLimit = 50;
    private const int MaxListLimit = 200;

    /// <summary>Maps the incident endpoints.</summary>
    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder incidents = builder.MapGroup("/v1/incidents").WithTags("Incidents");

        incidents.MapPost("/", CreateAsync)
            .RequireAuthorization(SrePolicies.WriteIncidents)
            .WithSummary("Reports an alert, opening an incident or folding into the existing one.");

        incidents.MapGet("/", ListAsync)
            .RequireAuthorization(SrePolicies.ReadIncidents)
            .WithSummary("Lists the tenant's incidents, newest first, with filters and a cursor.");

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

    private static async Task<IResult> ListAsync(
        HttpRequest request,
        ClaimsPrincipal principal,
        IIncidentQueries queries,
        CancellationToken cancellationToken)
    {
        Result<IncidentListQuery> query = ParseListQuery(request.Query);
        if (query.IsFailure)
        {
            return ResultExtensions.Problem(query.Error);
        }

        return Results.Json(await queries
            .ListIncidentsAsync(principal.Tenant(), query.Value, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <remarks>
    /// Read from the raw query rather than bound parameters so that every bad
    /// value is answered with the same problem shape. An unknown status is a
    /// 400 rather than ignored: a filter that silently matched everything would
    /// look like an answer.
    /// </remarks>
    private static Result<IncidentListQuery> ParseListQuery(IQueryCollection query)
    {
        List<IncidentStatus> statuses = [];
        foreach (string? value in query["status"])
        {
            if (!Enum.TryParse(value, ignoreCase: true, out IncidentStatus status) || !Enum.IsDefined(status) || IsNumeric(value))
            {
                return Invalid($"Unknown status '{value}'.");
            }

            statuses.Add(status);
        }

        List<IncidentSeverity> severities = [];
        foreach (string? value in query["severity"])
        {
            if (!Enum.TryParse(value, ignoreCase: true, out IncidentSeverity severity) || !Enum.IsDefined(severity) || IsNumeric(value))
            {
                return Invalid($"Unknown severity '{value}'.");
            }

            severities.Add(severity);
        }

        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        if (!TryInstant(query["detected_from"], out from) || !TryInstant(query["detected_to"], out to))
        {
            return Invalid("detected_from and detected_to must be ISO 8601 instants with an offset.");
        }

        PageCursor? after = null;
        if (query.TryGetValue("cursor", out StringValues cursor))
        {
            if (!PageCursor.TryDecode(cursor.ToString(), out PageCursor? decoded))
            {
                return Invalid("The cursor is not one this API issued.");
            }

            after = decoded;
        }

        int limit = DefaultListLimit;
        if (query.TryGetValue("limit", out StringValues rawLimit)
            && (!int.TryParse(rawLimit.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > MaxListLimit))
        {
            return Invalid($"limit must be between 1 and {MaxListLimit}.");
        }

        return Result<IncidentListQuery>.Success(new IncidentListQuery
        {
            Statuses = statuses,
            Severities = severities,
            Service = Single(query["service"]),
            Environment = Single(query["environment"]),
            Source = Single(query["source"]),
            DetectedFrom = from,
            DetectedTo = to,
            After = after,
            Limit = limit,
        });

        static Result<IncidentListQuery> Invalid(string message)
            => Result<IncidentListQuery>.Failure(Error.Permanent(ApplicationErrorCodes.ValidationFailed, message));

        static bool IsNumeric(string? value) => value is not null && value.Length > 0 && char.IsAsciiDigit(value[0]);

        static string? Single(StringValues values) => string.IsNullOrWhiteSpace(values.ToString()) ? null : values.ToString();

        static bool TryInstant(StringValues values, out DateTimeOffset? instant)
        {
            instant = null;
            if (StringValues.IsNullOrEmpty(values))
            {
                return true;
            }

            if (!DateTimeOffset.TryParse(values.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
            {
                return false;
            }

            instant = parsed;
            return true;
        }
    }

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
