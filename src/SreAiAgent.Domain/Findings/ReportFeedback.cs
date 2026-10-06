using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Findings;

/// <summary>What a person who worked the incident says about a report.</summary>
public enum ReportVerdict
{
    /// <summary>The report's leading hypothesis was the cause.</summary>
    Confirmed = 0,

    /// <summary>Right area, wrong or incomplete cause.</summary>
    PartiallyCorrect = 1,

    /// <summary>The report pointed somewhere else.</summary>
    Incorrect = 2,
}

/// <summary>
/// One person's verdict on one version of a root cause report
/// (incidents-feedback-evals.md F).
/// </summary>
/// <remarks>
/// The verdict is the measurement; the text is for the person who later turns a
/// wrong report into an eval case. Neither text field ever reaches a model, a
/// span or a log line: they are free text typed during an incident and may hold
/// anything.
/// </remarks>
public sealed record ReportFeedback
{
    public const int MaximumTextLength = 2000;

    private ReportFeedback(
        Guid id,
        string tenant,
        IncidentId incidentId,
        int reportVersion,
        InvestigationOutcome outcome,
        ReportVerdict verdict,
        string? actualRootCause,
        string? comment,
        string submittedBy,
        DateTimeOffset submittedAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        Tenant = tenant;
        IncidentId = incidentId;
        ReportVersion = reportVersion;
        Outcome = outcome;
        Verdict = verdict;
        ActualRootCause = actualRootCause;
        Comment = comment;
        SubmittedBy = submittedBy;
        SubmittedAt = submittedAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }
    public string Tenant { get; }
    public IncidentId IncidentId { get; }
    public int ReportVersion { get; }

    /// <summary>The report's outcome, kept so verdicts can be read per outcome without a join.</summary>
    public InvestigationOutcome Outcome { get; }

    public ReportVerdict Verdict { get; }
    public string? ActualRootCause { get; }
    public string? Comment { get; }
    public string SubmittedBy { get; }

    /// <summary>When this person first judged this version. Never moves, so it orders pages.</summary>
    public DateTimeOffset SubmittedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Validates and creates a verdict.</summary>
    /// <exception cref="DomainException">A field is out of range.</exception>
    public static ReportFeedback Create(
        string tenant,
        IncidentId incidentId,
        int reportVersion,
        InvestigationOutcome outcome,
        ReportVerdict verdict,
        string? actualRootCause,
        string? comment,
        string submittedBy,
        DateTimeOffset now)
    {
        Guard.AgainstNullOrWhiteSpace(tenant, DomainErrorCodes.ReportFeedbackInvalid);
        Guard.AgainstNullOrWhiteSpace(submittedBy, DomainErrorCodes.ReportFeedbackInvalid);
        if (reportVersion < 1)
        {
            throw new DomainException(DomainErrorCodes.ReportFeedbackInvalid, "Report versions start at 1.");
        }

        if (!Enum.IsDefined(verdict))
        {
            throw new DomainException(DomainErrorCodes.ReportFeedbackInvalid, "The verdict is not known.");
        }

        DateTimeOffset at = now.ToUniversalTime();
        return new ReportFeedback(
            Guid.CreateVersion7(),
            tenant,
            incidentId,
            reportVersion,
            outcome,
            verdict,
            Text(actualRootCause, "actual_root_cause"),
            Text(comment, "comment"),
            submittedBy,
            at,
            at);
    }

    /// <summary>The same person changing their mind: the first submission time stays.</summary>
    /// <exception cref="DomainException">A field is out of range.</exception>
    public ReportFeedback Revise(ReportVerdict verdict, string? actualRootCause, string? comment, DateTimeOffset now)
    {
        if (!Enum.IsDefined(verdict))
        {
            throw new DomainException(DomainErrorCodes.ReportFeedbackInvalid, "The verdict is not known.");
        }

        return new ReportFeedback(
            Id,
            Tenant,
            IncidentId,
            ReportVersion,
            Outcome,
            verdict,
            Text(actualRootCause, "actual_root_cause"),
            Text(comment, "comment"),
            SubmittedBy,
            SubmittedAt,
            now.ToUniversalTime());
    }

    /// <summary>Recreates a verdict read from storage.</summary>
    public static ReportFeedback Restore(
        Guid id,
        string tenant,
        IncidentId incidentId,
        int reportVersion,
        InvestigationOutcome outcome,
        ReportVerdict verdict,
        string? actualRootCause,
        string? comment,
        string submittedBy,
        DateTimeOffset submittedAt,
        DateTimeOffset updatedAt)
        => new(id, tenant, incidentId, reportVersion, outcome, verdict, actualRootCause, comment, submittedBy, submittedAt, updatedAt);

    private static string? Text(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= MaximumTextLength
            ? trimmed
            : throw new DomainException(
                DomainErrorCodes.ReportFeedbackInvalid,
                $"{field} is longer than {MaximumTextLength} characters.");
    }
}
