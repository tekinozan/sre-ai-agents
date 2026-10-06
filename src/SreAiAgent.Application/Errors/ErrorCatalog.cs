using System.Collections.Frozen;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Errors;

/// <summary>
/// How each error code behaves: what it maps to over HTTP, and whether retrying
/// can help.
/// </summary>
/// <param name="HttpStatusCode">Status the API returns (ARCHITECTURE.md 17).</param>
/// <param name="IsRetryable">
/// Whether a bounded retry has any chance of a different outcome. Section 14 is
/// explicit that invalid credentials, policy denials and deterministic validation
/// failures are not retryable; retrying them burns budget and, for a rate limit
/// that was actually a quota exhaustion, makes the situation worse.
/// </param>
public readonly record struct ErrorDescriptor(int HttpStatusCode, bool IsRetryable);

/// <summary>
/// Maps every error code to its behaviour, so the API and the retry policy read
/// from one table rather than each maintaining their own switch.
/// </summary>
public static class ErrorCatalog
{
    private const int BadRequest = 400;
    private const int Forbidden = 403;
    private const int NotFound = 404;
    private const int Conflict = 409;
    private const int InternalServerError = 500;
    private const int ServiceUnavailable = 503;
    private const int GatewayTimeout = 504;

    private static readonly FrozenDictionary<string, ErrorDescriptor> Descriptors =
        new Dictionary<string, ErrorDescriptor>(StringComparer.Ordinal)
        {
            // Configuration errors are deterministic: the same input fails identically.
            [ApplicationErrorCodes.ConfigInvalid.Value] = new(InternalServerError, false),
            [ApplicationErrorCodes.PromptNotFound.Value] = new(InternalServerError, false),
            [ApplicationErrorCodes.PromptRenderFailed.Value] = new(InternalServerError, false),
            [ApplicationErrorCodes.ModelProfileNotFound.Value] = new(InternalServerError, false),
            [ApplicationErrorCodes.ModelCapabilityMismatch.Value] = new(InternalServerError, false),
            [ApplicationErrorCodes.AgentNotFound.Value] = new(InternalServerError, false),
            [ApplicationErrorCodes.ConfigurationSnapshotNotFound.Value] = new(InternalServerError, false),
            [ApplicationErrorCodes.ProviderConnectionNotFound.Value] = new(NotFound, false),
            [ApplicationErrorCodes.ProviderRevisionNotFound.Value] = new(NotFound, false),
            [ApplicationErrorCodes.ModelDeploymentNotFound.Value] = new(NotFound, false),
            [ApplicationErrorCodes.ModelAssignmentNotFound.Value] = new(NotFound, false),
            [ApplicationErrorCodes.ModelAssignmentSlotMissing.Value] = new(Conflict, false),
            [ApplicationErrorCodes.ModelAssignmentEvaluationRequired.Value] = new(Conflict, false),
            [ApplicationErrorCodes.ModelEvaluationSummaryInvalid.Value] = new(BadRequest, false),
            [ApplicationErrorCodes.ModelEvaluationSeparationOfDuties.Value] = new(Forbidden, false),
            [ApplicationErrorCodes.ModelAssignmentScopeUnreachable.Value] = new(Conflict, false),
            [ApplicationErrorCodes.CredentialUnavailable.Value] = new(ServiceUnavailable, false),
            [ApplicationErrorCodes.ModelResourceConflict.Value] = new(Conflict, false),

            // Transient provider conditions.
            [ApplicationErrorCodes.LlmRateLimited.Value] = new(ServiceUnavailable, true),
            [ApplicationErrorCodes.LlmTimeout.Value] = new(GatewayTimeout, true),
            [ApplicationErrorCodes.LlmProviderError.Value] = new(ServiceUnavailable, true),
            [ApplicationErrorCodes.LlmAuthenticationFailed.Value] = new(ServiceUnavailable, false),
            [ApplicationErrorCodes.LlmInvalidRequest.Value] = new(BadRequest, false),
            [ApplicationErrorCodes.LlmUnavailable.Value] = new(ServiceUnavailable, true),

            // A model that ignored its schema will usually do so again, but not
            // always: sampling makes this the one model failure worth one retry.
            [ApplicationErrorCodes.LlmStructuredOutputInvalid.Value] = new(InternalServerError, true),

            [ApplicationErrorCodes.ToolNotFound.Value] = new(InternalServerError, false),
            [ApplicationErrorCodes.ToolPolicyDenied.Value] = new(Forbidden, false),
            [ApplicationErrorCodes.ToolTimeout.Value] = new(GatewayTimeout, true),
            [ApplicationErrorCodes.ToolRateLimited.Value] = new(ServiceUnavailable, true),
            [ApplicationErrorCodes.ToolExecutionFailed.Value] = new(ServiceUnavailable, true),
            [ApplicationErrorCodes.ToolArgumentsInvalid.Value] = new(BadRequest, false),

            [ApplicationErrorCodes.IncidentNotFound.Value] = new(NotFound, false),
            [ApplicationErrorCodes.IncidentInvalidState.Value] = new(Conflict, false),
            [ApplicationErrorCodes.ValidationFailed.Value] = new(BadRequest, false),

            [ApplicationErrorCodes.WorkflowDeadlineExceeded.Value] = new(GatewayTimeout, false),
            [ApplicationErrorCodes.WorkflowCancelled.Value] = new(Conflict, false),
            [ApplicationErrorCodes.WorkflowAlreadyRunning.Value] = new(Conflict, false),
            [ApplicationErrorCodes.BudgetExceeded.Value] = new(Conflict, false),
            [ApplicationErrorCodes.BudgetCurrencyMismatch.Value] = new(Conflict, false),
            [ApplicationErrorCodes.BudgetNotFound.Value] = new(NotFound, false),
            [ApplicationErrorCodes.NotificationChannelNotFound.Value] = new(NotFound, false),
            [ApplicationErrorCodes.NotificationChannelConflict.Value] = new(Conflict, false),
            [ApplicationErrorCodes.ReportNotFound.Value] = new(NotFound, false),
            [ApplicationErrorCodes.ReportFeedbackConflict.Value] = new(Conflict, true),
            [ApplicationErrorCodes.NotificationDeliveryFailed.Value] = new(ServiceUnavailable, true),
            [ApplicationErrorCodes.InsufficientAgentCapability.Value] = new(Conflict, false),

            [ApplicationErrorCodes.ApprovalRejected.Value] = new(Conflict, false),
            [ApplicationErrorCodes.ApprovalNotFound.Value] = new(NotFound, false),
            [ApplicationErrorCodes.RemediationFailed.Value] = new(InternalServerError, false),

            [ApplicationErrorCodes.Forbidden.Value] = new(Forbidden, false),
            [ApplicationErrorCodes.DependencyUnavailable.Value] = new(ServiceUnavailable, true),

            [DomainErrorCodes.IncidentNotFound.Value] = new(NotFound, false),
            [DomainErrorCodes.IncidentInvalidState.Value] = new(Conflict, false),
            [DomainErrorCodes.IncidentFingerprintRequired.Value] = new(BadRequest, false),
            [DomainErrorCodes.IncidentFieldRequired.Value] = new(BadRequest, false),
            [DomainErrorCodes.ConfidenceOutOfRange.Value] = new(BadRequest, false),
            [DomainErrorCodes.EvidenceReferenceRequired.Value] = new(BadRequest, false),
            [DomainErrorCodes.ApprovalRequired.Value] = new(Forbidden, false),
            [DomainErrorCodes.ApprovalInvalidState.Value] = new(Conflict, false),
            [DomainErrorCodes.RemediationRiskNotPermitted.Value] = new(Forbidden, false),
            [DomainErrorCodes.ReportVersionConflict.Value] = new(Conflict, false),
            [DomainErrorCodes.ModelConfigurationInvalid.Value] = new(BadRequest, false),
            [DomainErrorCodes.TenantScopeInvalid.Value] = new(BadRequest, false),
            [DomainErrorCodes.BudgetInvalid.Value] = new(BadRequest, false),
            [DomainErrorCodes.NotificationChannelInvalid.Value] = new(BadRequest, false),
            [DomainErrorCodes.ReportFeedbackInvalid.Value] = new(BadRequest, false),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Describes a code. An unknown code is treated as a non-retryable internal
    /// error: failing closed is the only safe default, since guessing that an
    /// unrecognised failure is retryable is how a broken dependency turns into a
    /// retry storm.
    /// </summary>
    public static ErrorDescriptor Describe(ErrorCode code)
        => Descriptors.TryGetValue(code.Value, out ErrorDescriptor descriptor)
            ? descriptor
            : new ErrorDescriptor(InternalServerError, IsRetryable: false);

    /// <summary>Whether a bounded retry is worth attempting.</summary>
    public static bool IsRetryable(ErrorCode code) => Describe(code).IsRetryable;

    /// <summary>The HTTP status this code maps to.</summary>
    public static int ToHttpStatusCode(ErrorCode code) => Describe(code).HttpStatusCode;

    /// <summary>Every code the catalog knows, for the test that asserts full coverage.</summary>
    public static IReadOnlyCollection<string> KnownCodes => Descriptors.Keys;
}
