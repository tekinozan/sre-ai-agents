namespace SreAiAgent.Domain.Remediation;

/// <summary>State of a human approval request (ARCHITECTURE.md 12.1).</summary>
public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,

    /// <summary>
    /// Nobody decided within the window. Expiry is a rejection, never an
    /// implicit approval: an unattended request must not become permission.
    /// </summary>
    Expired = 3,

    /// <summary>Withdrawn because the incident was cancelled or resolved.</summary>
    Withdrawn = 4,
}
