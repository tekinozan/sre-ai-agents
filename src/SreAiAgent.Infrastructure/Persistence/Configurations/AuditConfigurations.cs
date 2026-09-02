using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Configurations;

/// <summary>Maps the tool audit trail to <c>tool_calls</c>.</summary>
internal sealed class ToolCallConfiguration : IEntityTypeConfiguration<ToolCallEntity>
{
    public void Configure(EntityTypeBuilder<ToolCallEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tool_calls");
        builder.HasKey(call => call.Id);

        builder.Property(call => call.ToolName).HasMaxLength(128).IsRequired();
        builder.Property(call => call.ToolVersion).HasMaxLength(32).IsRequired();
        builder.Property(call => call.AgentName).HasMaxLength(128).IsRequired();
        builder.Property(call => call.Outcome).HasMaxLength(32).IsRequired();
        builder.Property(call => call.ErrorCode).HasMaxLength(64);
        builder.Property(call => call.ArgumentsJson).HasColumnType("jsonb").IsRequired();

        // Every question asked of this table starts with an incident: what did the
        // platform do about incident X. Ordering by id inside that is chronological
        // because identifiers are version 7.
        builder.HasIndex(call => new { call.IncidentId, call.Id })
            .HasDatabaseName("ix_tool_calls_incident");

        // Denials are what a platform review looks for, and they are a small
        // fraction of the rows. A partial index keeps that lookup cheap without
        // carrying the successful calls.
        builder.HasIndex(call => new { call.ToolName, call.ExecutedAt })
            .HasDatabaseName("ix_tool_calls_denied")
            .HasFilter("outcome = 'denied'");
    }
}

/// <summary>Maps the model audit trail to <c>llm_calls</c>.</summary>
internal sealed class LlmCallConfiguration : IEntityTypeConfiguration<LlmCallEntity>
{
    public void Configure(EntityTypeBuilder<LlmCallEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("llm_calls");
        builder.HasKey(call => call.Id);

        builder.Property(call => call.AgentName).HasMaxLength(128).IsRequired();
        builder.Property(call => call.PromptName).HasMaxLength(128).IsRequired();
        builder.Property(call => call.PromptVersion).HasMaxLength(32).IsRequired();
        builder.Property(call => call.PromptHash).HasMaxLength(64).IsRequired();
        builder.Property(call => call.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(call => call.AssignmentHash).HasMaxLength(64).IsRequired();
        builder.Property(call => call.DeploymentHash).HasMaxLength(64).IsRequired();
        builder.Property(call => call.ProviderRevisionHash).HasMaxLength(64).IsRequired();
        builder.Property(call => call.RequestedProvider).HasMaxLength(32).IsRequired();
        builder.Property(call => call.RequestedModel).HasMaxLength(512).IsRequired();
        builder.Property(call => call.ObservedProvider).HasMaxLength(64).IsRequired();
        builder.Property(call => call.ObservedModel).HasMaxLength(512).IsRequired();
        builder.Property(call => call.ProviderRequestId).HasMaxLength(256).IsRequired();
        builder.Property(call => call.FinishReason).HasMaxLength(64).IsRequired();
        builder.Property(call => call.Outcome).HasMaxLength(32).IsRequired();
        builder.Property(call => call.ErrorCode).HasMaxLength(64);

        // Cost is money. Stored as an exact decimal rather than a float, because a
        // per-tenant total accumulated from binary fractions drifts.
        builder.Property(call => call.EstimatedCost).HasPrecision(12, 6);

        builder.HasIndex(call => new { call.IncidentId, call.Id })
            .HasDatabaseName("ix_llm_calls_incident");

        // Attribution for a quality regression: which prompt release produced a
        // given result. Section 8.2 is the reason the hash is on the row at all.
        builder.HasIndex(call => new { call.PromptName, call.PromptVersion })
            .HasDatabaseName("ix_llm_calls_prompt");
    }
}
