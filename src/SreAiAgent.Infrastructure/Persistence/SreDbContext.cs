using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Recommendations;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence;

/// <summary>
/// The business store (ARCHITECTURE.md 9).
/// </summary>
/// <remarks>
/// Deliberately separate from the workflow engine's own tables. Business state
/// lives here and workflow history lives in Temporal; conflating them would make
/// recovering an incident mean replaying a workflow, and would tie the durability
/// of an operator-facing report to the retention policy of an orchestration
/// engine.
///
/// Query tracking defaults to no-tracking. The overwhelming majority of reads are
/// projections for the API, and paying for change-tracking snapshots of thousands
/// of evidence rows to serialize six fields off each one is indefensible. Write
/// paths opt in explicitly through the repositories.
/// </remarks>
public sealed class SreDbContext : DbContext
{
    public SreDbContext(DbContextOptions<SreDbContext> options)
        : base(options)
    {
    }

    public DbSet<Incident> Incidents => Set<Incident>();

    public DbSet<IncidentTimelineEntry> IncidentEvents => Set<IncidentTimelineEntry>();

    public DbSet<ConfigurationSnapshotEntity> ConfigurationSnapshots => Set<ConfigurationSnapshotEntity>();

    public DbSet<EvidenceRecord> Evidence => Set<EvidenceRecord>();

    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

    public DbSet<Finding> Findings => Set<Finding>();

    public DbSet<RootCauseReport> RootCauseReports => Set<RootCauseReport>();

    public DbSet<Recommendation> Recommendations => Set<Recommendation>();

    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

    public DbSet<RemediationAction> RemediationActions => Set<RemediationAction>();

    /// <summary>Append-only audit of every tool execution (ARCHITECTURE.md 9.2).</summary>
    public DbSet<ToolCallEntity> ToolCalls => Set<ToolCallEntity>();

    /// <summary>Append-only audit of every model call.</summary>
    public DbSet<LlmCallEntity> LlmCalls => Set<LlmCallEntity>();

    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();

    public DbSet<InboxMessageEntity> InboxMessages => Set<InboxMessageEntity>();

    public DbSet<IdempotencyKeyEntity> IdempotencyKeys => Set<IdempotencyKeyEntity>();

    public DbSet<ConsumerDeliveryAttemptEntity> ConsumerDeliveryAttempts => Set<ConsumerDeliveryAttemptEntity>();

    public DbSet<ProviderConnectionEntity> ProviderConnections => Set<ProviderConnectionEntity>();
    public DbSet<ProviderConnectionRevisionEntity> ProviderConnectionRevisions => Set<ProviderConnectionRevisionEntity>();
    public DbSet<ProviderRevisionVerificationEntity> ProviderRevisionVerifications => Set<ProviderRevisionVerificationEntity>();
    public DbSet<ModelDeploymentVersionEntity> ModelDeploymentVersions => Set<ModelDeploymentVersionEntity>();
    public DbSet<ModelAssignmentReleaseEntity> ModelAssignmentReleases => Set<ModelAssignmentReleaseEntity>();
    public DbSet<ModelAssignmentAgentSlotEntity> ModelAssignmentAgentSlots => Set<ModelAssignmentAgentSlotEntity>();
    public DbSet<TenantActiveModelAssignmentEntity> TenantActiveModelAssignments => Set<TenantActiveModelAssignmentEntity>();
    public DbSet<ModelAssignmentEvaluationEntity> ModelAssignmentEvaluations => Set<ModelAssignmentEvaluationEntity>();
    public DbSet<ModelControlPlaneAuditEntity> ModelControlPlaneAudits => Set<ModelControlPlaneAuditEntity>();
    public DbSet<TenantBudgetEntity> TenantBudgets => Set<TenantBudgetEntity>();

    /// <inheritdoc />
    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // One converter registration per id type rather than one per property.
        // Twelve strongly typed ids across a dozen entities would otherwise be
        // several dozen identical Convert calls in the configurations.
        configurationBuilder.Properties<IncidentId>().HaveConversion<IncidentIdConverter>();
        configurationBuilder.Properties<IncidentEventId>().HaveConversion<IncidentEventIdConverter>();
        configurationBuilder.Properties<ConfigurationSnapshotId>()
            .HaveConversion<ConfigurationSnapshotIdConverter>();
        configurationBuilder.Properties<EvidenceId>().HaveConversion<EvidenceIdConverter>();
        configurationBuilder.Properties<AgentRunId>().HaveConversion<AgentRunIdConverter>();
        configurationBuilder.Properties<FindingId>().HaveConversion<FindingIdConverter>();
        configurationBuilder.Properties<RootCauseReportId>().HaveConversion<RootCauseReportIdConverter>();
        configurationBuilder.Properties<RecommendationId>().HaveConversion<RecommendationIdConverter>();
        configurationBuilder.Properties<ApprovalRequestId>().HaveConversion<ApprovalRequestIdConverter>();
        configurationBuilder.Properties<RemediationActionId>().HaveConversion<RemediationActionIdConverter>();
        configurationBuilder.Properties<ToolCallId>().HaveConversion<ToolCallIdConverter>();
        configurationBuilder.Properties<LlmCallId>().HaveConversion<LlmCallIdConverter>();
        configurationBuilder.Properties<ProviderConnectionId>().HaveConversion<ProviderConnectionIdConverter>();
        configurationBuilder.Properties<ProviderConnectionRevisionId>().HaveConversion<ProviderConnectionRevisionIdConverter>();
        configurationBuilder.Properties<ModelDeploymentVersionId>().HaveConversion<ModelDeploymentVersionIdConverter>();
        configurationBuilder.Properties<ModelAssignmentReleaseId>().HaveConversion<ModelAssignmentReleaseIdConverter>();
        configurationBuilder.Properties<ModelAssignmentEvaluationId>().HaveConversion<ModelAssignmentEvaluationIdConverter>();

        configurationBuilder.Properties<ErrorCode>().HaveConversion<ErrorCodeConverter>().HaveMaxLength(64);
        configurationBuilder.Properties<Confidence>().HaveConversion<ConfidenceConverter>().HavePrecision(5, 4);

        // Every string column is bounded. An unbounded text column is how a
        // hostile or merely verbose payload turns into a row that cannot be
        // replicated, and PostgreSQL will not warn about it.
        configurationBuilder.Properties<string>().HaveMaxLength(1024);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SreDbContext).Assembly);

        foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entity
                 in modelBuilder.Model.GetEntityTypes())
        {
            foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableProperty property in entity.GetProperties())
            {
                // Columns are snake_case. This is not cosmetic: the partial index
                // filters and the outbox dispatcher's FOR UPDATE SKIP LOCKED query
                // are hand-written SQL that names columns directly, and PostgreSQL
                // folds unquoted identifiers to lower case. A PascalCase column
                // would make every one of them fail at runtime.
                if (property.GetColumnName() is { } current && !current.Contains('_', StringComparison.Ordinal))
                {
                    property.SetColumnName(ToSnakeCase(current));
                }

                // Timestamps are timestamptz throughout. ARCHITECTURE.md 5.2
                // requires UTC everywhere, and `timestamp without time zone`
                // silently discards the offset.
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetColumnType("timestamptz");
                }

                // JSONB columns must not inherit the global string length
                // convention. A 1024-character cap on an evidence payload or an
                // event envelope would truncate it at the database, after every
                // application-level size check has already passed.
                if (string.Equals(property.GetColumnType(), "jsonb", StringComparison.Ordinal))
                {
                    property.SetMaxLength(null);
                }
            }
        }
    }

    /// <summary>Converts a PascalCase member name to snake_case.</summary>
    private static string ToSnakeCase(string name)
    {
        System.Text.StringBuilder builder = new(name.Length + 8);

        for (int i = 0; i < name.Length; i++)
        {
            char current = name[i];

            if (char.IsUpper(current))
            {
                // Only insert a separator between a lower-case or digit character
                // and an upper-case one, so an acronym such as `IncidentId` becomes
                // `incident_id` rather than `incident_i_d`.
                if (i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// Comparer for the label dictionaries stored as JSONB.
/// </summary>
/// <remarks>
/// EF Core needs a value comparer for a mutable reference type used as a value,
/// otherwise it cannot tell whether the dictionary changed and will either miss
/// an update or rewrite the column on every save.
/// </remarks>
internal static class DictionaryComparers
{
    public static ValueComparer<IReadOnlyDictionary<string, string>> Ordinal { get; } = new(
        static (left, right) => left != null && right != null && left.Count == right.Count
                                && left.All(pair => right.ContainsKey(pair.Key) && right[pair.Key] == pair.Value),
        static dictionary => dictionary.Aggregate(
            0, static (hash, pair) => HashCode.Combine(hash, pair.Key.GetHashCode(StringComparison.Ordinal),
                pair.Value.GetHashCode(StringComparison.Ordinal))),
        static dictionary => new Dictionary<string, string>(dictionary, StringComparer.Ordinal));
}
