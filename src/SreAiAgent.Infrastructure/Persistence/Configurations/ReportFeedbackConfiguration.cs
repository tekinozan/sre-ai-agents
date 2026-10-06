using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Configurations;

internal sealed class ReportFeedbackConfiguration : IEntityTypeConfiguration<ReportFeedbackEntity>
{
    public void Configure(EntityTypeBuilder<ReportFeedbackEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("report_feedback");
        builder.HasKey(feedback => feedback.Id);
        builder.Property(feedback => feedback.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(feedback => feedback.Outcome).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(feedback => feedback.Verdict).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(feedback => feedback.ActualRootCause).HasMaxLength(ReportFeedback.MaximumTextLength);
        builder.Property(feedback => feedback.Comment).HasMaxLength(ReportFeedback.MaximumTextLength);
        builder.Property(feedback => feedback.SubmittedBy).HasMaxLength(256).IsRequired();

        // One current verdict per person per report version; a second
        // submission revises the first instead of stacking beside it.
        builder.HasIndex(feedback => new { feedback.IncidentId, feedback.ReportVersion, feedback.SubmittedBy })
            .IsUnique()
            .HasDatabaseName("ux_report_feedback_report_actor");

        // The tenant-wide list pages by first submission.
        builder.HasIndex(feedback => new { feedback.Tenant, feedback.SubmittedAt, feedback.Id })
            .HasDatabaseName("ix_report_feedback_tenant_submitted");
    }
}
