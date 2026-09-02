using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Persistence;

// Value converters for the strongly typed ids.
//
// EF Core cannot use an open generic converter registered through
// ConfigureConventions, so each id needs a concrete type. They are generated from
// one template for the same reason the ids themselves are: the shape is
// identical and hand-maintaining twelve copies invites one of them to drift.
//
// Every id stores as a plain uuid column, so a strongly typed id costs nothing at
// the database and a DBA reading the schema sees exactly what they expect.

/// <summary>Base converter shared by every strongly typed id.</summary>
/// <typeparam name="TId">The identifier type.</typeparam>
public abstract class StronglyTypedIdConverter<TId> : ValueConverter<TId, Guid>
    where TId : struct, IStronglyTypedId<TId>
{
    protected StronglyTypedIdConverter()
        : base(id => id.Value, value => FromGuid(value))
    {
    }

    /// <summary>
    /// Wraps the static abstract factory. An expression tree cannot contain a
    /// call to a static abstract interface member directly, so the call is made
    /// inside an ordinary static method and the tree calls that instead.
    /// </summary>
    private static TId FromGuid(Guid value) => TId.From(value);
}

/// <summary>Converts <see cref="IncidentId"/>.</summary>
public sealed class IncidentIdConverter : StronglyTypedIdConverter<IncidentId>;

/// <summary>Converts <see cref="IncidentEventId"/>.</summary>
public sealed class IncidentEventIdConverter : StronglyTypedIdConverter<IncidentEventId>;

/// <summary>Converts <see cref="ConfigurationSnapshotId"/>.</summary>
public sealed class ConfigurationSnapshotIdConverter : StronglyTypedIdConverter<ConfigurationSnapshotId>;

/// <summary>Converts <see cref="EvidenceId"/>.</summary>
public sealed class EvidenceIdConverter : StronglyTypedIdConverter<EvidenceId>;

/// <summary>Converts <see cref="AgentRunId"/>.</summary>
public sealed class AgentRunIdConverter : StronglyTypedIdConverter<AgentRunId>;

/// <summary>Converts <see cref="FindingId"/>.</summary>
public sealed class FindingIdConverter : StronglyTypedIdConverter<FindingId>;

/// <summary>Converts <see cref="RootCauseReportId"/>.</summary>
public sealed class RootCauseReportIdConverter : StronglyTypedIdConverter<RootCauseReportId>;

/// <summary>Converts <see cref="RecommendationId"/>.</summary>
public sealed class RecommendationIdConverter : StronglyTypedIdConverter<RecommendationId>;

/// <summary>Converts <see cref="ApprovalRequestId"/>.</summary>
public sealed class ApprovalRequestIdConverter : StronglyTypedIdConverter<ApprovalRequestId>;

/// <summary>Converts <see cref="RemediationActionId"/>.</summary>
public sealed class RemediationActionIdConverter : StronglyTypedIdConverter<RemediationActionId>;

/// <summary>Converts <see cref="ToolCallId"/>.</summary>
public sealed class ToolCallIdConverter : StronglyTypedIdConverter<ToolCallId>;

/// <summary>Converts <see cref="LlmCallId"/>.</summary>
public sealed class LlmCallIdConverter : StronglyTypedIdConverter<LlmCallId>;

public sealed class ProviderConnectionIdConverter : StronglyTypedIdConverter<ProviderConnectionId>;

public sealed class ProviderConnectionRevisionIdConverter : StronglyTypedIdConverter<ProviderConnectionRevisionId>;

public sealed class ModelDeploymentVersionIdConverter : StronglyTypedIdConverter<ModelDeploymentVersionId>;

public sealed class ModelAssignmentReleaseIdConverter : StronglyTypedIdConverter<ModelAssignmentReleaseId>;

public sealed class ModelAssignmentEvaluationIdConverter : StronglyTypedIdConverter<ModelAssignmentEvaluationId>;

/// <summary>
/// Stores an <see cref="ErrorCode"/> as its string value.
/// </summary>
/// <remarks>
/// Stored as text rather than as an enum column on purpose. The catalogue grows
/// with every new failure mode, and a database enum would make adding one a
/// migration; an unrecognised code read back from an old row still round-trips.
/// </remarks>
public sealed class ErrorCodeConverter : ValueConverter<ErrorCode, string>
{
    public ErrorCodeConverter()
        : base(code => code.Value, value => new ErrorCode(value))
    {
    }
}

/// <summary>
/// Stores a <see cref="Confidence"/> as a bounded decimal.
/// </summary>
/// <remarks>
/// Read back through <c>Clamp</c> rather than <c>From</c>. A row written before a
/// bound changed, or one edited by hand, must not make an entity impossible to
/// materialize: failing to load a historical incident because its confidence is
/// 1.0001 helps nobody.
/// </remarks>
public sealed class ConfidenceConverter : ValueConverter<Confidence, decimal>
{
    public ConfidenceConverter()
        : base(confidence => confidence.Value, value => Confidence.Clamp(value))
    {
    }
}
