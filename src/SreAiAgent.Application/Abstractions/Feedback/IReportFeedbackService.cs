using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Feedback;

/// <summary>Records and reads verdicts on root cause reports (incidents-feedback-evals.md F).</summary>
public interface IReportFeedbackService
{
    /// <summary>
    /// Records the actor's verdict on one report version, replacing their
    /// earlier verdict on the same version.
    /// </summary>
    Task<Result<ReportFeedback>> SubmitAsync(
        string tenant,
        IncidentId incidentId,
        int reportVersion,
        ReportVerdict verdict,
        string? actualRootCause,
        string? comment,
        string actor,
        CancellationToken cancellationToken);

    /// <summary>The verdicts on an incident's reports, oldest first.</summary>
    Task<Result<IReadOnlyList<ReportFeedback>>> ListForIncidentAsync(
        string tenant,
        IncidentId incidentId,
        CancellationToken cancellationToken);

    /// <summary>A page of the tenant's verdicts, newest first.</summary>
    Task<ReportFeedbackPage> ListAsync(
        string tenant,
        ReportVerdict? verdict,
        DateTimeOffset? since,
        PageCursor? after,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>A page of verdicts; <see cref="Next"/> is null on the last page.</summary>
public sealed record ReportFeedbackPage(IReadOnlyList<ReportFeedback> Items, PageCursor? Next);
