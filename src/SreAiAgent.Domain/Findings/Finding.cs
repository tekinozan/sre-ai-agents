using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Findings;

/// <summary>
/// A persisted <see cref="AgentFinding"/>, stored in <c>findings</c>. The
/// structured content is kept as written by the agent so a report can always be
/// traced back to the exact claims that produced it.
/// </summary>
public sealed class Finding : Entity<FindingId>
{
    private Finding()
    {
    }

    private Finding(
        FindingId id,
        IncidentId incidentId,
        AgentRunId agentRunId,
        string agentName,
        string agentVersion,
        AgentFinding content,
        bool isShadow,
        DateTimeOffset createdAt)
    {
        Id = id;
        IncidentId = incidentId;
        AgentRunId = agentRunId;
        AgentName = Guard.AgainstNullOrWhiteSpace(agentName, DomainErrorCodes.IncidentFieldRequired);
        AgentVersion = Guard.AgainstNullOrWhiteSpace(agentVersion, DomainErrorCodes.IncidentFieldRequired);
        Content = content;
        IsShadow = isShadow;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public IncidentId IncidentId { get; private set; }

    public AgentRunId AgentRunId { get; private set; }

    public string AgentName { get; private set; } = null!;

    public string AgentVersion { get; private set; } = null!;

    /// <summary>The structured finding, mapped to a JSONB column.</summary>
    public AgentFinding Content { get; private set; } = null!;

    /// <summary>Shadow findings are excluded from correlation and from the report.</summary>
    public bool IsShadow { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Finding Create(
        IncidentId incidentId,
        AgentRunId agentRunId,
        string agentName,
        string agentVersion,
        AgentFinding content,
        bool isShadow,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new Finding(
            FindingId.New(), incidentId, agentRunId, agentName, agentVersion, content, isShadow, createdAt);
    }
}
