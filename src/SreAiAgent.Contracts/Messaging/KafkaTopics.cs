namespace SreAiAgent.Contracts.Messaging;

/// <summary>
/// Topic names from ARCHITECTURE.md 10.2. Constants rather than configuration
/// because the version suffix is part of the contract: a breaking payload change
/// creates <c>.v2</c> alongside the existing topic instead of silently
/// redefining what consumers of <c>.v1</c> receive.
/// </summary>
public static class KafkaTopics
{
    public const string AlertReceived = "sre.alert.received.v1";
    public const string IncidentCreated = "sre.incident.created.v1";
    public const string InvestigationRequested = "sre.incident.investigation.requested.v1";
    public const string IncidentUpdated = "sre.incident.updated.v1";
    public const string IncidentCompleted = "sre.incident.completed.v1";
    public const string RemediationRequested = "sre.remediation.requested.v1";
    public const string RemediationCompleted = "sre.remediation.completed.v1";
    public const string DeadLetter = "sre.deadletter.v1";

    /// <summary>Every topic the platform owns, for provisioning and health checks.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        AlertReceived,
        IncidentCreated,
        InvestigationRequested,
        IncidentUpdated,
        IncidentCompleted,
        RemediationRequested,
        RemediationCompleted,
        DeadLetter,
    ];
}
