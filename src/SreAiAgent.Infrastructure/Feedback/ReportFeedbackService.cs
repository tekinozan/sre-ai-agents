using Microsoft.EntityFrameworkCore;
using Npgsql;
using SreAiAgent.Application.Abstractions.Feedback;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Feedback;

/// <summary>Verdicts on root cause reports, backed by PostgreSQL.</summary>
internal sealed class ReportFeedbackService(
    SreDbContext context,
    SreMetrics metrics,
    TimeProvider timeProvider) : IReportFeedbackService
{
    public async Task<Result<ReportFeedback>> SubmitAsync(
        string tenant,
        IncidentId incidentId,
        int reportVersion,
        ReportVerdict verdict,
        string? actualRootCause,
        string? comment,
        string actor,
        CancellationToken cancellationToken)
    {
        // The incident is checked against the tenant before the report is
        // looked up by id alone, so another tenant's report is "not found"
        // rather than a distinguishable answer.
        bool incidentExists = await context.Incidents
            .AnyAsync(incident => incident.Tenant == tenant && incident.Id == incidentId, cancellationToken)
            .ConfigureAwait(false);
        if (!incidentExists)
        {
            return Result<ReportFeedback>.Failure(Error.Permanent(ApplicationErrorCodes.IncidentNotFound, "No such incident."));
        }

        InvestigationOutcome? outcome = await context.RootCauseReports
            .Where(report => report.IncidentId == incidentId && report.Version == reportVersion)
            .Select(report => (InvestigationOutcome?)report.Outcome)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (outcome is null)
        {
            return Result<ReportFeedback>.Failure(Error.Permanent(
                ApplicationErrorCodes.ReportNotFound, "The incident has no report with that version."));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        // Tracked explicitly: the context defaults to no tracking, and without
        // this a revision answered 200 and saved nothing.
        ReportFeedbackEntity? existing = await context.ReportFeedback
            .AsTracking()
            .SingleOrDefaultAsync(
                feedback => feedback.IncidentId == incidentId
                    && feedback.ReportVersion == reportVersion
                    && feedback.SubmittedBy == actor,
                cancellationToken)
            .ConfigureAwait(false);

        ReportFeedback recorded;
        try
        {
            recorded = existing is null
                ? ReportFeedback.Create(tenant, incidentId, reportVersion, outcome.Value, verdict, actualRootCause, comment, actor, now)
                : ToDomain(existing).Revise(verdict, actualRootCause, comment, now);
        }
        catch (DomainException exception)
        {
            return Result<ReportFeedback>.Failure(Error.Permanent(exception.Code, exception.Message));
        }

        if (existing is null)
        {
            context.ReportFeedback.Add(ToEntity(recorded));
        }
        else
        {
            existing.Verdict = recorded.Verdict;
            existing.ActualRootCause = recorded.ActualRootCause;
            existing.Comment = recorded.Comment;
            existing.UpdatedAt = recorded.UpdatedAt;
        }

        // The timeline says that someone judged the report and how, never what
        // they wrote: the entry is served on the incident's event stream.
        context.IncidentEvents.Add(IncidentTimelineEntry.Progress(
            incidentId,
            "report.feedback",
            $"Report version {reportVersion} was judged {recorded.Verdict}.",
            now,
            actor));

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same person submitting twice at once. Either answer is theirs;
            // asking them to repeat it is honest and rare enough.
            return Result<ReportFeedback>.Failure(Error.Transient(
                ApplicationErrorCodes.ReportFeedbackConflict, "A verdict for this report was being recorded at the same time. Try again."));
        }

        metrics.ReportFeedback(tenant, recorded.Verdict.ToString(), recorded.Outcome.ToString());
        return Result<ReportFeedback>.Success(recorded);
    }

    public async Task<Result<IReadOnlyList<ReportFeedback>>> ListForIncidentAsync(
        string tenant,
        IncidentId incidentId,
        CancellationToken cancellationToken)
    {
        bool incidentExists = await context.Incidents
            .AnyAsync(incident => incident.Tenant == tenant && incident.Id == incidentId, cancellationToken)
            .ConfigureAwait(false);
        if (!incidentExists)
        {
            return Result<IReadOnlyList<ReportFeedback>>.Failure(Error.Permanent(ApplicationErrorCodes.IncidentNotFound, "No such incident."));
        }

        List<ReportFeedbackEntity> rows = await context.ReportFeedback
            .AsNoTracking()
            .Where(feedback => feedback.Tenant == tenant && feedback.IncidentId == incidentId)
            .OrderBy(feedback => feedback.ReportVersion)
            .ThenBy(feedback => feedback.SubmittedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<ReportFeedback>>.Success([.. rows.Select(ToDomain)]);
    }

    public async Task<ReportFeedbackPage> ListAsync(
        string tenant,
        ReportVerdict? verdict,
        DateTimeOffset? since,
        PageCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<ReportFeedbackEntity> rows = after is null
            ? context.ReportFeedback
            : context.ReportFeedback.FromSql(
                $"SELECT * FROM report_feedback WHERE (submitted_at, id) < ({after.At}, {after.Id})");

        rows = rows.Where(feedback => feedback.Tenant == tenant);
        if (verdict is { } wanted)
        {
            rows = rows.Where(feedback => feedback.Verdict == wanted);
        }

        if (since is { } from)
        {
            rows = rows.Where(feedback => feedback.SubmittedAt >= from);
        }

        List<ReportFeedbackEntity> page = await rows
            .AsNoTracking()
            .OrderByDescending(feedback => feedback.SubmittedAt)
            .ThenByDescending(feedback => feedback.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (page.Count <= limit)
        {
            return new ReportFeedbackPage([.. page.Select(ToDomain)], null);
        }

        page.RemoveAt(page.Count - 1);
        ReportFeedbackEntity last = page[^1];
        return new ReportFeedbackPage([.. page.Select(ToDomain)], new PageCursor(last.SubmittedAt, last.Id));
    }

    private static ReportFeedback ToDomain(ReportFeedbackEntity entity)
        => ReportFeedback.Restore(
            entity.Id,
            entity.Tenant,
            entity.IncidentId,
            entity.ReportVersion,
            entity.Outcome,
            entity.Verdict,
            entity.ActualRootCause,
            entity.Comment,
            entity.SubmittedBy,
            entity.SubmittedAt,
            entity.UpdatedAt);

    private static ReportFeedbackEntity ToEntity(ReportFeedback feedback) => new()
    {
        Id = feedback.Id,
        Tenant = feedback.Tenant,
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
