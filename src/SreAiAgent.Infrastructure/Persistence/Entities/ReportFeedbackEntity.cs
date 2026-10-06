using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Persistence.Entities;

public sealed class ReportFeedbackEntity
{
    public Guid Id { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public IncidentId IncidentId { get; set; }
    public int ReportVersion { get; set; }
    public InvestigationOutcome Outcome { get; set; }
    public ReportVerdict Verdict { get; set; }
    public string? ActualRootCause { get; set; }
    public string? Comment { get; set; }
    public string SubmittedBy { get; set; } = string.Empty;
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
