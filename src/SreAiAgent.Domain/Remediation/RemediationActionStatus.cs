namespace SreAiAgent.Domain.Remediation;

/// <summary>Lifecycle of a remediation action.</summary>
public enum RemediationActionStatus
{
    /// <summary>Recorded and authorized, not yet started.</summary>
    Pending = 0,
    Executing = 1,
    Succeeded = 2,
    Failed = 3,

    /// <summary>Blocked by policy at execution time even though it was approved.</summary>
    Denied = 4,

    /// <summary>Abandoned before it ran.</summary>
    Cancelled = 5,
}
