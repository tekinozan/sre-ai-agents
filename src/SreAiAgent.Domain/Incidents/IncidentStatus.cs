namespace SreAiAgent.Domain.Incidents;

/// <summary>Incident lifecycle states (ARCHITECTURE.md 5.3).</summary>
public enum IncidentStatus
{
    /// <summary>Created from an alert, not yet picked up by a workflow.</summary>
    Open = 0,

    /// <summary>A durable investigation workflow is running.</summary>
    Investigating = 1,

    /// <summary>Paused until a human approves or rejects a proposed action.</summary>
    WaitingApproval = 2,

    /// <summary>An approved remediation is executing.</summary>
    Remediating = 3,

    /// <summary>Closed with a report. Reopening is a separate, explicit operation.</summary>
    Resolved = 4,

    /// <summary>The investigation could not complete. Retry is permitted.</summary>
    Failed = 5,

    /// <summary>Abandoned by an operator. Terminal.</summary>
    Cancelled = 6,
}
