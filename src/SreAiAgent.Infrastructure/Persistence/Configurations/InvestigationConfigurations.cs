using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Recommendations;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Configurations;

/// <summary>Maps evidence to <c>evidence</c>.</summary>
internal sealed class EvidenceConfiguration : IEntityTypeConfiguration<EvidenceRecord>
{
    public void Configure(EntityTypeBuilder<EvidenceRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("evidence");
        builder.HasKey(evidence => evidence.Id);

        builder.Property(evidence => evidence.Provider).HasMaxLength(64).IsRequired();
        builder.Property(evidence => evidence.ToolName).HasMaxLength(128).IsRequired();
        builder.Property(evidence => evidence.ToolVersion).HasMaxLength(32).IsRequired();
        builder.Property(evidence => evidence.Summary).HasMaxLength(2048).IsRequired();
        builder.Property(evidence => evidence.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(evidence => evidence.RawPayloadReference).HasMaxLength(512);

        builder.Property(evidence => evidence.SourceType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(evidence => evidence.RedactionStatus).HasConversion<string>().HasMaxLength(32).IsRequired();

        // The only column allowed to be unbounded, and it is still capped by the
        // tool's MaxResultBytes before it gets here. Evidence content is the one
        // payload whose size genuinely varies by orders of magnitude between a
        // pod list and a metric range.
        builder.Property(evidence => evidence.ContentJson).HasColumnType("jsonb").IsRequired();

        builder.HasIndex(evidence => new { evidence.IncidentId, evidence.Id })
            .HasDatabaseName("ix_evidence_incident");

        builder.Ignore(evidence => evidence.IsUsableInPrompt);
    }
}

/// <summary>Maps agent runs to <c>agent_runs</c>.</summary>
internal sealed class AgentRunConfiguration : IEntityTypeConfiguration<AgentRun>
{
    public void Configure(EntityTypeBuilder<AgentRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("agent_runs");
        builder.HasKey(run => run.Id);

        builder.Property(run => run.AgentName).HasMaxLength(128).IsRequired();
        builder.Property(run => run.AgentVersion).HasMaxLength(32).IsRequired();
        builder.Property(run => run.PromptName).HasMaxLength(128).IsRequired();
        builder.Property(run => run.PromptVersion).HasMaxLength(32).IsRequired();
        builder.Property(run => run.PromptHash).HasMaxLength(64).IsRequired();
        builder.Property(run => run.ModelProvider).HasMaxLength(64).IsRequired();
        builder.Property(run => run.ModelName).HasMaxLength(128).IsRequired();
        builder.Property(run => run.ModelDeploymentVersion).HasColumnName("model_deployment_version").HasMaxLength(32).IsRequired();
        builder.Property(run => run.ErrorReason).HasMaxLength(1024);
        builder.Property(run => run.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(run => run.EstimatedCost).HasPrecision(12, 6);

        builder.HasIndex(run => new { run.IncidentId, run.Id }).HasDatabaseName("ix_agent_runs_incident");

        builder.Ignore(run => run.Duration);
    }
}

/// <summary>Maps findings to <c>findings</c>.</summary>
internal sealed class FindingConfiguration : IEntityTypeConfiguration<Finding>
{
    public void Configure(EntityTypeBuilder<Finding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("findings");
        builder.HasKey(finding => finding.Id);

        builder.Property(finding => finding.AgentName).HasMaxLength(128).IsRequired();
        builder.Property(finding => finding.AgentVersion).HasMaxLength(32).IsRequired();

        // The structured finding round-trips through the domain factories, so an
        // observation read back from an old row still cannot be verified without
        // evidence. Storing it as a flat blob and rebuilding it by hand would
        // bypass exactly the invariant the type exists to hold.
        builder.Property(finding => finding.Content)
            .HasColumnType("jsonb")
            .HasConversion(
                content => JsonbConverters.SerializeFinding(content),
                json => JsonbConverters.DeserializeFinding(json))
            .IsRequired();

        builder.HasIndex(finding => new { finding.IncidentId, finding.IsShadow })
            .HasDatabaseName("ix_findings_incident");

    }
}

/// <summary>Maps reports to <c>root_cause_reports</c>.</summary>
internal sealed class RootCauseReportConfiguration : IEntityTypeConfiguration<RootCauseReport>
{
    public void Configure(EntityTypeBuilder<RootCauseReport> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("root_cause_reports");
        builder.HasKey(report => report.Id);

        builder.Property(report => report.Summary).HasMaxLength(8192).IsRequired();
        builder.Property(report => report.Category).HasMaxLength(128);
        builder.Property(report => report.Outcome).HasConversion<string>().HasMaxLength(48).IsRequired();

        builder.Property(report => report.KeyObservations)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonbConverters.SerializeObservations(value),
                json => JsonbConverters.DeserializeObservations(json));

        builder.Property(report => report.RankedHypotheses)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonbConverters.SerializeHypotheses(value),
                json => JsonbConverters.DeserializeHypotheses(json));

        builder.Property(report => report.EvidenceGaps)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonbConverters.SerializeStrings(value),
                json => JsonbConverters.DeserializeStrings(json));

        builder.Property(report => report.CitedEvidenceIds)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonbConverters.SerializeEvidenceIds(value),
                json => JsonbConverters.DeserializeEvidenceIds(json));

        builder.Property(report => report.ContributingAgentNames)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonbConverters.SerializeStrings(value),
                json => JsonbConverters.DeserializeStrings(json));

        // UNIQUE(incident_id, report_version) from ARCHITECTURE.md 9.3. Enforced
        // by the database rather than by a read-then-write check, because two
        // concurrent retries would both read the same "next" version.
        builder.HasIndex(report => new { report.IncidentId, report.Version })
            .IsUnique()
            .HasDatabaseName("ux_root_cause_reports_incident_version");

    }
}

/// <summary>Maps recommendations to <c>recommendations</c>.</summary>
internal sealed class RecommendationConfiguration : IEntityTypeConfiguration<Recommendation>
{
    public void Configure(EntityTypeBuilder<Recommendation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("recommendations");
        builder.HasKey(recommendation => recommendation.Id);

        builder.Property(recommendation => recommendation.Title).HasMaxLength(512).IsRequired();
        builder.Property(recommendation => recommendation.Rationale).HasMaxLength(4096).IsRequired();
        builder.Property(recommendation => recommendation.Risk).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Property(recommendation => recommendation.EvidenceIds)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonbConverters.SerializeEvidenceIds(value),
                json => JsonbConverters.DeserializeEvidenceIds(json));

        builder.HasIndex(recommendation => new { recommendation.RootCauseReportId, recommendation.Rank })
            .HasDatabaseName("ix_recommendations_report");

        builder.Ignore(recommendation => recommendation.RequiresApproval);
    }
}

/// <summary>Maps approvals to <c>approval_requests</c>.</summary>
internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("approval_requests");
        builder.HasKey(request => request.Id);

        builder.Property(request => request.Title).HasMaxLength(512).IsRequired();
        builder.Property(request => request.Justification).HasMaxLength(4096).IsRequired();
        builder.Property(request => request.ProposedActionJson).HasColumnType("jsonb").IsRequired();
        builder.Property(request => request.DecidedBy).HasMaxLength(256);
        builder.Property(request => request.DecisionReason).HasMaxLength(4096);
        builder.Property(request => request.Risk).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.HasIndex(request => new { request.Status, request.ExpiresAt })
            .HasDatabaseName("ix_approval_requests_pending");

        builder.Ignore(request => request.IsOpen);
    }
}

/// <summary>Maps remediation actions to <c>remediation_actions</c>.</summary>
internal sealed class RemediationActionConfiguration : IEntityTypeConfiguration<RemediationAction>
{
    public void Configure(EntityTypeBuilder<RemediationAction> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("remediation_actions");
        builder.HasKey(action => action.Id);

        builder.Property(action => action.ToolName).HasMaxLength(128).IsRequired();
        builder.Property(action => action.ToolVersion).HasMaxLength(32).IsRequired();
        builder.Property(action => action.ArgumentsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(action => action.AuthorizedBy).HasMaxLength(256).IsRequired();
        builder.Property(action => action.IdempotencyKey).HasMaxLength(128);
        builder.Property(action => action.ResultSummary).HasMaxLength(4096);
        builder.Property(action => action.Risk).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(action => action.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        // Unique per approval: an approved action executes once. Without this a
        // retried activity could produce a second write against a real system
        // from a single human authorization.
        builder.HasIndex(action => action.ApprovalRequestId)
            .IsUnique()
            .HasDatabaseName("ux_remediation_actions_approval");

    }
}

/// <summary>Maps configuration snapshots to <c>configuration_snapshots</c>.</summary>
internal sealed class ConfigurationSnapshotConfiguration : IEntityTypeConfiguration<ConfigurationSnapshotEntity>
{
    public void Configure(EntityTypeBuilder<ConfigurationSnapshotEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("configuration_snapshots");
        builder.HasKey(snapshot => snapshot.Id);

        builder.Property(snapshot => snapshot.AgentRelease).HasMaxLength(128).IsRequired();
        builder.Property(snapshot => snapshot.PromptRelease).HasMaxLength(128).IsRequired();
        builder.Property(snapshot => snapshot.ModelAssignmentJson).HasColumnType("jsonb").IsRequired();
        builder.Property(snapshot => snapshot.ToolRelease).HasMaxLength(128).IsRequired();
        builder.Property(snapshot => snapshot.PolicyRelease).HasMaxLength(128).IsRequired();

        builder.Property(snapshot => snapshot.FlagSnapshot)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonbConverters.Serialize(value),
                json => JsonbConverters.DeserializeLabels(json))
            .Metadata.SetValueComparer(DictionaryComparers.Ordinal);
    }
}
