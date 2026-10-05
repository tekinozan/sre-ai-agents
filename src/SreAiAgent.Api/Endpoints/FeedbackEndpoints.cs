using System.Security.Claims;
using Microsoft.Extensions.Primitives;
using SreAiAgent.Api.Http;
using SreAiAgent.Api.Security;
using SreAiAgent.Application.Abstractions.Feedback;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Errors;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Api.Endpoints;

/// <summary>
/// Verdicts on root cause reports (incidents-feedback-evals.md F).
/// </summary>
/// <remarks>
/// Writing needs the same capability as working an incident: the people who
/// open, retry and cancel investigations are the ones who learn whether the
/// report was right. Reading is open to every incident reader, because the
/// verdicts are part of the incident's record.
/// </remarks>
public static class FeedbackEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    /// <summary>Maps the report feedback endpoints.</summary>
    public static IEndpointRouteBuilder MapFeedbackEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder report = builder.MapGroup("/v1/incidents/{incidentId:guid}/report/feedback").WithTags("Incidents");

        report.MapPost("/", SubmitAsync)
            .RequireAuthorization(SrePolicies.WriteIncidents)
            .WithSummary("Records your verdict on a report version, replacing your earlier one.");

        report.MapGet("/", ListForIncidentAsync)
            .RequireAuthorization(SrePolicies.ReadIncidents)
            .WithSummary("Lists the verdicts on an incident's reports.");

        builder.MapGet("/v1/report-feedback", ListAsync)
            .WithTags("Incidents")
            .RequireAuthorization(SrePolicies.ReadIncidents)
            .WithSummary("Lists the tenant's verdicts, newest first; the input for curating eval cases.");

        return builder;
    }

    private static async Task<IResult> SubmitAsync(
        Guid incidentId,
        SubmitReportFeedbackRequest request,
        ClaimsPrincipal principal,
        IReportFeedbackService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<ReportFeedback> result = await service.SubmitAsync(
            principal.Tenant(),
            IncidentId.From(incidentId),
            request.ReportVersion,
            request.Verdict,
            request.ActualRootCause,
            request.Comment,
            principal.Actor(),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Json(ToResponse(result.Value))
            : ResultExtensions.Problem(result.Error);
    }

    private static async Task<IResult> ListForIncidentAsync(
        Guid incidentId,
        ClaimsPrincipal principal,
        IReportFeedbackService service,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<ReportFeedback>> result = await service
            .ListForIncidentAsync(principal.Tenant(), IncidentId.From(incidentId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Json<IReadOnlyList<ReportFeedbackResponse>>([.. result.Value.Select(ToResponse)])
            : ResultExtensions.Problem(result.Error);
    }

    private static async Task<IResult> ListAsync(
        HttpRequest request,
        ClaimsPrincipal principal,
        IReportFeedbackService service,
        CancellationToken cancellationToken)
    {
        IQueryCollection query = request.Query;

        ReportVerdict? verdict = null;
        if (query.TryGetValue("verdict", out StringValues rawVerdict))
        {
            string value = rawVerdict.ToString();
            if (!Enum.TryParse(value, ignoreCase: true, out ReportVerdict parsed)
                || !Enum.IsDefined(parsed)
                || (value.Length > 0 && char.IsAsciiDigit(value[0])))
            {
                return Invalid($"Unknown verdict '{value}'.");
            }

            verdict = parsed;
        }

        DateTimeOffset? since = null;
        if (query.TryGetValue("since", out StringValues rawSince))
        {
            if (!DateTimeOffset.TryParse(rawSince.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
            {
                return Invalid("since must be an ISO 8601 instant with an offset.");
            }

            since = parsed;
        }

        PageCursor? after = null;
        if (query.TryGetValue("cursor", out StringValues rawCursor))
        {
            if (!PageCursor.TryDecode(rawCursor.ToString(), out PageCursor? decoded))
            {
                return Invalid("The cursor is not one this API issued.");
            }

            after = decoded;
        }

        int limit = DefaultLimit;
        if (query.TryGetValue("limit", out StringValues rawLimit)
            && (!int.TryParse(rawLimit.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > MaxLimit))
        {
            return Invalid($"limit must be between 1 and {MaxLimit}.");
        }

        ReportFeedbackPage page = await service
            .ListAsync(principal.Tenant(), verdict, since, after, limit, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(new ReportFeedbackListResponse
        {
            Items = [.. page.Items.Select(ToResponse)],
            NextCursor = page.Next?.Encode(),
        });
    }

    private static IResult Invalid(string message)
        => ResultExtensions.Problem(Error.Permanent(ApplicationErrorCodes.ValidationFailed, message));

    private static ReportFeedbackResponse ToResponse(ReportFeedback feedback) => new()
    {
        FeedbackId = feedback.Id,
        IncidentId = feedback.IncidentId,
        ReportVersion = feedback.ReportVersion,
        Outcome = feedback.Outcome,
        Verdict = feedback.Verdict,
        ActualRootCause = feedback.ActualRootCause,
        Comment = feedback.Comment,
        SubmittedBy = feedback.SubmittedBy,
        SubmittedAt = feedback.SubmittedAt,
        UpdatedAt = feedback.UpdatedAt,
    };
}
