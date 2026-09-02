using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Errors;

/// <summary>
/// The error taxonomy of ARCHITECTURE.md 17.
/// </summary>
/// <remarks>
/// Provider exceptions never escape their adapter. A caller receives a stable
/// code, a retryability flag and a safe message, which is what lets the API map
/// failures without pattern-matching on exception types and lets a client build
/// behaviour on top of them without parsing English.
/// </remarks>
public static class ApplicationErrorCodes
{
    // Configuration and registries
    public static readonly ErrorCode ConfigInvalid = new("CONFIG_INVALID");
    public static readonly ErrorCode PromptNotFound = new("PROMPT_NOT_FOUND");
    public static readonly ErrorCode PromptRenderFailed = new("PROMPT_RENDER_FAILED");
    public static readonly ErrorCode ModelProfileNotFound = new("MODEL_PROFILE_NOT_FOUND");
    public static readonly ErrorCode ModelCapabilityMismatch = new("MODEL_CAPABILITY_MISMATCH");
    public static readonly ErrorCode AgentNotFound = new("AGENT_NOT_FOUND");
    public static readonly ErrorCode ConfigurationSnapshotNotFound = new("CONFIG_SNAPSHOT_NOT_FOUND");
    public static readonly ErrorCode ProviderConnectionNotFound = new("PROVIDER_CONNECTION_NOT_FOUND");
    public static readonly ErrorCode ProviderRevisionNotFound = new("PROVIDER_REVISION_NOT_FOUND");
    public static readonly ErrorCode ModelDeploymentNotFound = new("MODEL_DEPLOYMENT_NOT_FOUND");
    public static readonly ErrorCode ModelAssignmentNotFound = new("MODEL_ASSIGNMENT_NOT_FOUND");
    public static readonly ErrorCode ModelAssignmentSlotMissing = new("MODEL_ASSIGNMENT_SLOT_MISSING");
    public static readonly ErrorCode ModelAssignmentEvaluationRequired = new("MODEL_ASSIGNMENT_EVALUATION_REQUIRED");
    public static readonly ErrorCode CredentialUnavailable = new("CREDENTIAL_UNAVAILABLE");
    public static readonly ErrorCode ModelResourceConflict = new("MODEL_RESOURCE_CONFLICT");

    // Language model
    public static readonly ErrorCode LlmRateLimited = new("LLM_RATE_LIMITED");
    public static readonly ErrorCode LlmTimeout = new("LLM_TIMEOUT");
    public static readonly ErrorCode LlmProviderError = new("LLM_PROVIDER_ERROR");
    public static readonly ErrorCode LlmStructuredOutputInvalid = new("LLM_STRUCTURED_OUTPUT_INVALID");
    public static readonly ErrorCode LlmAuthenticationFailed = new("LLM_AUTHENTICATION_FAILED");
    public static readonly ErrorCode LlmInvalidRequest = new("LLM_INVALID_REQUEST");
    public static readonly ErrorCode LlmUnavailable = new("LLM_UNAVAILABLE");

    // Tools
    public static readonly ErrorCode ToolNotFound = new("TOOL_NOT_FOUND");
    public static readonly ErrorCode ToolPolicyDenied = new("TOOL_POLICY_DENIED");
    public static readonly ErrorCode ToolTimeout = new("TOOL_TIMEOUT");
    public static readonly ErrorCode ToolRateLimited = new("TOOL_RATE_LIMITED");
    public static readonly ErrorCode ToolExecutionFailed = new("TOOL_EXECUTION_FAILED");
    public static readonly ErrorCode ToolArgumentsInvalid = new("TOOL_ARGUMENTS_INVALID");

    // Incidents
    public static readonly ErrorCode IncidentNotFound = new("INCIDENT_NOT_FOUND");
    public static readonly ErrorCode IncidentInvalidState = new("INCIDENT_INVALID_STATE");
    public static readonly ErrorCode ValidationFailed = new("VALIDATION_FAILED");

    // Workflow
    public static readonly ErrorCode WorkflowDeadlineExceeded = new("WORKFLOW_DEADLINE_EXCEEDED");
    public static readonly ErrorCode WorkflowCancelled = new("WORKFLOW_CANCELLED");
    public static readonly ErrorCode WorkflowAlreadyRunning = new("WORKFLOW_ALREADY_RUNNING");
    public static readonly ErrorCode BudgetExceeded = new("BUDGET_EXCEEDED");

    /// <summary>
    /// Routing found nobody able to investigate: every candidate was disabled,
    /// kill-switched or missing a required tool (ARCHITECTURE.md 24.3).
    /// </summary>
    public static readonly ErrorCode InsufficientAgentCapability = new("INSUFFICIENT_AGENT_CAPABILITY");

    // Approval and remediation
    public static readonly ErrorCode ApprovalRejected = new("APPROVAL_REJECTED");
    public static readonly ErrorCode ApprovalNotFound = new("APPROVAL_NOT_FOUND");
    public static readonly ErrorCode RemediationFailed = new("REMEDIATION_FAILED");

    // Authorization and infrastructure
    public static readonly ErrorCode Forbidden = new("FORBIDDEN");
    public static readonly ErrorCode DependencyUnavailable = new("DEPENDENCY_UNAVAILABLE");
}
