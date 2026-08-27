namespace SreAiAgent.Api.Security;

/// <summary>Roles from ARCHITECTURE.md 15.1.</summary>
public static class SreRoles
{
    public const string Viewer = "SreViewer";
    public const string Investigator = "SreInvestigator";
    public const string Approver = "SreApprover";
    public const string Operator = "SreOperator";
    public const string PlatformAdmin = "SrePlatformAdmin";
}

/// <summary>
/// Authorization policies, named after what they permit rather than after who
/// holds them.
/// </summary>
/// <remarks>
/// Endpoints require a capability, not a role. Adding a sixth role then means
/// editing this file once instead of auditing every endpoint to find the ones
/// that should have included it.
/// </remarks>
public static class SrePolicies
{
    /// <summary>Read incidents, evidence, reports and timelines.</summary>
    public const string ReadIncidents = "incidents:read";

    /// <summary>Open, retry or cancel an investigation.</summary>
    public const string WriteIncidents = "incidents:write";

    /// <summary>
    /// Decide an approval.
    /// </summary>
    /// <remarks>
    /// Deliberately not granted to the roles that can start investigations.
    /// Section 15.1 asks for stronger identity on approval, and the least it can
    /// mean is that the person who proposed a change is not automatically the
    /// person who authorizes it.
    /// </remarks>
    public const string DecideApprovals = "approvals:decide";
}
