using System.Text.Json.Serialization;

namespace SreAiAgent.Domain.Shared;

// Strongly typed identifiers (ARCHITECTURE.md 5.1). A bare Guid is never passed
// across a boundary: mistaking an EvidenceId for an IncidentId has to be a
// compile error rather than a production incident.
//
// Every identifier is a version 7 GUID. Version 7 encodes a millisecond
// timestamp in the high bits, so keys are generated in ascending order and
// PostgreSQL appends to the right edge of the index instead of splitting random
// interior pages. Random version 4 keys are a well known source of write
// amplification on high-insert tables such as `evidence` and `tool_calls`.
//
// The declarations below are intentionally uniform: C# has no struct
// inheritance, so the shape repeats. Behaviour lives in IStronglyTypedId,
// StronglyTypedIdJsonConverter and the EF Core value converter, none of which
// are duplicated.

/// <summary>Identifies an incident. Doubles as the durable workflow id and the business correlation id (ARCHITECTURE.md 3.2).</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<IncidentId>))]
public readonly record struct IncidentId(Guid Value) : IStronglyTypedId<IncidentId>
{
    public static IncidentId New() => new(Guid.CreateVersion7());

    public static IncidentId From(Guid value) => new(value);

    public static IncidentId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out IncidentId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new IncidentId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Identifies a single entry on an incident timeline.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<IncidentEventId>))]
public readonly record struct IncidentEventId(Guid Value) : IStronglyTypedId<IncidentEventId>
{
    public static IncidentEventId New() => new(Guid.CreateVersion7());

    public static IncidentEventId From(Guid value) => new(value);

    public static IncidentEventId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out IncidentEventId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new IncidentEventId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Identifies the immutable agent/prompt/model/tool/policy release set pinned at investigation start (ARCHITECTURE.md 8.2).</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<ConfigurationSnapshotId>))]
public readonly record struct ConfigurationSnapshotId(Guid Value) : IStronglyTypedId<ConfigurationSnapshotId>
{
    public static ConfigurationSnapshotId New() => new(Guid.CreateVersion7());

    public static ConfigurationSnapshotId From(Guid value) => new(value);

    public static ConfigurationSnapshotId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out ConfigurationSnapshotId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new ConfigurationSnapshotId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Identifies one piece of collected evidence. Every observation must cite at least one.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<EvidenceId>))]
public readonly record struct EvidenceId(Guid Value) : IStronglyTypedId<EvidenceId>
{
    public static EvidenceId New() => new(Guid.CreateVersion7());

    public static EvidenceId From(Guid value) => new(value);

    public static EvidenceId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out EvidenceId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new EvidenceId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Identifies one execution of one agent within one investigation.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<AgentRunId>))]
public readonly record struct AgentRunId(Guid Value) : IStronglyTypedId<AgentRunId>
{
    public static AgentRunId New() => new(Guid.CreateVersion7());

    public static AgentRunId From(Guid value) => new(value);

    public static AgentRunId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out AgentRunId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new AgentRunId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Identifies a persisted agent finding.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<FindingId>))]
public readonly record struct FindingId(Guid Value) : IStronglyTypedId<FindingId>
{
    public static FindingId New() => new(Guid.CreateVersion7());

    public static FindingId From(Guid value) => new(value);

    public static FindingId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out FindingId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new FindingId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Identifies a versioned root cause report.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<RootCauseReportId>))]
public readonly record struct RootCauseReportId(Guid Value) : IStronglyTypedId<RootCauseReportId>
{
    public static RootCauseReportId New() => new(Guid.CreateVersion7());

    public static RootCauseReportId From(Guid value) => new(value);

    public static RootCauseReportId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out RootCauseReportId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new RootCauseReportId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Identifies a single recommendation attached to a report.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<RecommendationId>))]
public readonly record struct RecommendationId(Guid Value) : IStronglyTypedId<RecommendationId>
{
    public static RecommendationId New() => new(Guid.CreateVersion7());

    public static RecommendationId From(Guid value) => new(value);

    public static RecommendationId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out RecommendationId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new RecommendationId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Identifies a human approval request.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<ApprovalRequestId>))]
public readonly record struct ApprovalRequestId(Guid Value) : IStronglyTypedId<ApprovalRequestId>
{
    public static ApprovalRequestId New() => new(Guid.CreateVersion7());

    public static ApprovalRequestId From(Guid value) => new(value);

    public static ApprovalRequestId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out ApprovalRequestId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new ApprovalRequestId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Identifies a remediation action. Write actions require an approved request.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<RemediationActionId>))]
public readonly record struct RemediationActionId(Guid Value) : IStronglyTypedId<RemediationActionId>
{
    public static RemediationActionId New() => new(Guid.CreateVersion7());

    public static RemediationActionId From(Guid value) => new(value);

    public static RemediationActionId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out RemediationActionId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new RemediationActionId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Identifies one tool gateway execution, for audit and evidence provenance.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<ToolCallId>))]
public readonly record struct ToolCallId(Guid Value) : IStronglyTypedId<ToolCallId>
{
    public static ToolCallId New() => new(Guid.CreateVersion7());

    public static ToolCallId From(Guid value) => new(value);

    public static ToolCallId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out ToolCallId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new ToolCallId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Identifies one LLM gateway call, for cost and token accounting.</summary>
[JsonConverter(typeof(StronglyTypedIdJsonConverter<LlmCallId>))]
public readonly record struct LlmCallId(Guid Value) : IStronglyTypedId<LlmCallId>
{
    public static LlmCallId New() => new(Guid.CreateVersion7());

    public static LlmCallId From(Guid value) => new(value);

    public static LlmCallId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out LlmCallId result)
    {
        bool parsed = Guid.TryParse(s, provider, out Guid value);
        result = new LlmCallId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}
