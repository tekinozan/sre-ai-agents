namespace SreAiAgent.Api.Security;

/// <summary>Roles from ARCHITECTURE.md 15.1.</summary>
public static class SreRoles
{
    public const string Viewer = "SreViewer";
    public const string Investigator = "SreInvestigator";
    public const string Approver = "SreApprover";
    public const string Operator = "SreOperator";
    public const string PlatformAdmin = "SrePlatformAdmin";

    /// <summary>
    /// The CI identity that runs the eval harness and hands its summary back
    /// (D50). Not a person's role: it can complete evaluations and nothing else.
    /// </summary>
    public const string EvaluationRunner = "SreEvaluationRunner";
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

    /// <summary>
    /// Manage the tenant's model provider connections, revisions, deployment
    /// versions and assignment releases.
    /// </summary>
    /// <remarks>
    /// Reads and writes share one policy on purpose. The management surface is
    /// operator-only; a viewer or investigator has no business enumerating
    /// credential references, and reusing <see cref="WriteIncidents"/> would
    /// grant it to <see cref="SreRoles.Investigator"/> too.
    /// </remarks>
    public const string ManageModelProviders = "model-providers:manage";

    /// <summary>Manage the tenant's Slack and Teams notification channels.</summary>
    /// <remarks>
    /// Operator-only for the same reason as <see cref="ManageModelProviders"/>:
    /// the list carries credential references, and a channel is somewhere the
    /// platform will post on the tenant's behalf.
    /// </remarks>
    public const string ManageNotifications = "notifications:manage";

    /// <summary>Complete a model assignment evaluation with the harness's summary.</summary>
    /// <remarks>
    /// Held only by <see cref="SreRoles.EvaluationRunner"/>, deliberately not by
    /// <see cref="SreRoles.Operator"/> or <see cref="SreRoles.PlatformAdmin"/>:
    /// the people who request and activate a release are exactly the ones who
    /// must not be able to declare that it passed (D50).
    /// </remarks>
    public const string CompleteModelEvaluations = "model-evaluations:complete";
}
