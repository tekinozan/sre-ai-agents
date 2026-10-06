using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Contracts.Incidents;

/// <summary>Body of <c>POST /v1/incidents/{id}/report/feedback</c>.</summary>
/// <remarks>
/// Names the report version rather than meaning "the latest": a retry can add a
/// version while someone is still typing about the previous one, and the
/// verdict belongs to the report they read.
/// </remarks>
public sealed record SubmitReportFeedbackRequest
{
    public required int ReportVersion { get; init; }

    public required ReportVerdict Verdict { get; init; }

    /// <summary>What the cause actually was, when known. At most 2000 characters.</summary>
    public string? ActualRootCause { get; init; }

    /// <summary>At most 2000 characters.</summary>
    public string? Comment { get; init; }
}

/// <summary>One person's current verdict on one report version.</summary>
public sealed record ReportFeedbackResponse
{
    public required Guid FeedbackId { get; init; }

    public required IncidentId IncidentId { get; init; }

    public required int ReportVersion { get; init; }

    /// <summary>The judged report's outcome.</summary>
    public required InvestigationOutcome Outcome { get; init; }

    public required ReportVerdict Verdict { get; init; }

    public string? ActualRootCause { get; init; }

    public string? Comment { get; init; }

    public required string SubmittedBy { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A page of <c>GET /v1/report-feedback</c>; see <see cref="IncidentListResponse"/> for the cursor.</summary>
public sealed record ReportFeedbackListResponse
{
    public required IReadOnlyList<ReportFeedbackResponse> Items { get; init; }

    public string? NextCursor { get; init; }
}
