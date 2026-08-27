using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Tests.Incidents;

/// <summary>Builders that keep the lifecycle tests focused on the transition under test.</summary>
internal static class TestIncident
{
    private static readonly DateTimeOffset DefaultDetectedAt = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    public static Incident Open(
        string fingerprint = "fp-1",
        IncidentSeverity severity = IncidentSeverity.High,
        DateTimeOffset? detectedAt = null)
        => new(
            IncidentId.New(),
            new NewIncident(
                Tenant: "tenant-a",
                Source: "alertmanager",
                ExternalId: "alert-1",
                Fingerprint: fingerprint,
                Title: "Checkout latency above SLO",
                Description: null,
                Service: "checkout",
                Environment: "production",
                Severity: severity,
                DetectedAt: detectedAt ?? DefaultDetectedAt),
            detectedAt ?? DefaultDetectedAt);

    /// <summary>Drives an incident to a target status through legal transitions only.</summary>
    public static Incident InStatus(IncidentStatus status)
    {
        Incident incident = Open();
        DateTimeOffset now = DefaultDetectedAt;

        switch (status)
        {
            case IncidentStatus.Open:
                break;

            case IncidentStatus.Investigating:
                incident.StartInvestigation(ConfigurationSnapshotId.New(), now);
                break;

            case IncidentStatus.WaitingApproval:
                incident.StartInvestigation(ConfigurationSnapshotId.New(), now);
                incident.RequireApproval(ApprovalRequestId.New(), now);
                break;

            case IncidentStatus.Remediating:
                incident.StartInvestigation(ConfigurationSnapshotId.New(), now);
                ApprovalRequestId approvalId = ApprovalRequestId.New();
                incident.RequireApproval(approvalId, now);
                incident.StartRemediation(approvalId, now);
                break;

            case IncidentStatus.Resolved:
                incident.StartInvestigation(ConfigurationSnapshotId.New(), now);
                incident.MarkResolved(now);
                break;

            case IncidentStatus.Failed:
                incident.StartInvestigation(ConfigurationSnapshotId.New(), now);
                incident.MarkFailed(new ErrorCode("WORKFLOW_DEADLINE_EXCEEDED"), "deadline", now);
                break;

            case IncidentStatus.Cancelled:
                incident.Cancel("operator@example.com", "not actionable", now);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled status.");
        }

        incident.DequeueEvents();
        return incident;
    }
}
