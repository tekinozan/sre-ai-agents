namespace SreAiAgent.Domain.Shared;

/// <summary>
/// Error codes owned by the domain. The remaining codes from ARCHITECTURE.md 17
/// describe adapter and orchestration failures and live in the application
/// layer, so the domain stays unaware of prompts, tools and workflows.
/// </summary>
public static class DomainErrorCodes
{
    public static readonly ErrorCode IncidentFingerprintRequired = new("INCIDENT_FINGERPRINT_REQUIRED");
    public static readonly ErrorCode IncidentFieldRequired = new("INCIDENT_FIELD_REQUIRED");
    public static readonly ErrorCode IncidentInvalidState = new("INCIDENT_INVALID_STATE");
    public static readonly ErrorCode IncidentNotFound = new("INCIDENT_NOT_FOUND");
    public static readonly ErrorCode ConfidenceOutOfRange = new("CONFIDENCE_OUT_OF_RANGE");
    public static readonly ErrorCode EvidenceReferenceRequired = new("EVIDENCE_REFERENCE_REQUIRED");
    public static readonly ErrorCode ApprovalRequired = new("APPROVAL_REQUIRED");
    public static readonly ErrorCode ApprovalInvalidState = new("APPROVAL_INVALID_STATE");
    public static readonly ErrorCode RemediationRiskNotPermitted = new("REMEDIATION_RISK_NOT_PERMITTED");
    public static readonly ErrorCode ReportVersionConflict = new("REPORT_VERSION_CONFLICT");
    public static readonly ErrorCode ModelConfigurationInvalid = new("MODEL_CONFIGURATION_INVALID");
    public static readonly ErrorCode TenantScopeInvalid = new("TENANT_SCOPE_INVALID");
}
