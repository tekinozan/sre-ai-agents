using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Persistence.Configurations;

internal sealed class ProviderConnectionConfiguration : IEntityTypeConfiguration<ProviderConnectionEntity>
{
    public void Configure(EntityTypeBuilder<ProviderConnectionEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("provider_connections");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.Tenant, entity.Id });
        builder.Property(entity => entity.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.DisplayName).HasMaxLength(256).IsRequired();
        builder.HasIndex(entity => new { entity.Tenant, entity.DisplayName }).IsUnique();
        builder.HasOne<ProviderConnectionRevisionEntity>()
            .WithMany()
            .HasForeignKey(entity => new { entity.Tenant, entity.ActiveRevisionId })
            .HasPrincipalKey(entity => new { entity.Tenant, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProviderConnectionRevisionConfiguration : IEntityTypeConfiguration<ProviderConnectionRevisionEntity>
{
    public void Configure(EntityTypeBuilder<ProviderConnectionRevisionEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("provider_connection_revisions");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.Tenant, entity.Id });
        builder.Property(entity => entity.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.AdapterKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.Endpoint).HasMaxLength(2048).IsRequired();
        builder.Property(entity => entity.CredentialRef).HasMaxLength(1024).IsRequired();
        builder.Property(entity => entity.ConfigurationHash).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.CreatedBy).HasMaxLength(256).IsRequired();
        builder.HasIndex(entity => new { entity.Tenant, entity.ProviderConnectionId, entity.Revision }).IsUnique();
        builder.HasOne<ProviderConnectionEntity>()
            .WithMany()
            .HasForeignKey(entity => new { entity.Tenant, entity.ProviderConnectionId })
            .HasPrincipalKey(entity => new { entity.Tenant, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProviderRevisionVerificationConfiguration : IEntityTypeConfiguration<ProviderRevisionVerificationEntity>
{
    public void Configure(EntityTypeBuilder<ProviderRevisionVerificationEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("provider_revision_verifications");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.RequestId).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.VerifiedBy).HasMaxLength(256).IsRequired();
        builder.HasIndex(entity => new { entity.Tenant, entity.ProviderConnectionRevisionId, entity.VerifiedAt });
        builder.HasOne<ProviderConnectionRevisionEntity>()
            .WithMany()
            .HasForeignKey(entity => new { entity.Tenant, entity.ProviderConnectionRevisionId })
            .HasPrincipalKey(entity => new { entity.Tenant, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ModelDeploymentVersionConfiguration : IEntityTypeConfiguration<ModelDeploymentVersionEntity>
{
    public void Configure(EntityTypeBuilder<ModelDeploymentVersionEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("model_deployment_versions");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.Tenant, entity.Id });
        builder.Property(entity => entity.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.LogicalName).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.ProviderModel).HasMaxLength(512).IsRequired();
        builder.Property(entity => entity.Currency).HasMaxLength(3).IsRequired();
        builder.Property(entity => entity.ProviderParametersJson).HasColumnType("jsonb").IsRequired();
        builder.Property(entity => entity.ConfigurationHash).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.CreatedBy).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.InputCostPerMillionTokens).HasPrecision(18, 6);
        builder.Property(entity => entity.OutputCostPerMillionTokens).HasPrecision(18, 6);
        builder.HasIndex(entity => new { entity.Tenant, entity.LogicalName, entity.Version }).IsUnique();
        builder.HasOne<ProviderConnectionRevisionEntity>()
            .WithMany()
            .HasForeignKey(entity => new { entity.Tenant, entity.ProviderConnectionRevisionId })
            .HasPrincipalKey(entity => new { entity.Tenant, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ModelAssignmentReleaseConfiguration : IEntityTypeConfiguration<ModelAssignmentReleaseEntity>
{
    public void Configure(EntityTypeBuilder<ModelAssignmentReleaseEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("model_assignment_releases");
        builder.HasKey(entity => entity.Id);
        builder.HasAlternateKey(entity => new { entity.Tenant, entity.Id });
        builder.Property(entity => entity.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Name).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.ConfigurationHash).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.CreatedBy).HasMaxLength(256).IsRequired();
        builder.HasIndex(entity => new { entity.Tenant, entity.Name, entity.Version }).IsUnique();
        builder.HasOne<ModelDeploymentVersionEntity>()
            .WithMany()
            .HasForeignKey(entity => new { entity.Tenant, entity.ClassifierDeploymentVersionId })
            .HasPrincipalKey(entity => new { entity.Tenant, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ModelDeploymentVersionEntity>()
            .WithMany()
            .HasForeignKey(entity => new { entity.Tenant, entity.CorrelatorDeploymentVersionId })
            .HasPrincipalKey(entity => new { entity.Tenant, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ModelAssignmentAgentSlotConfiguration : IEntityTypeConfiguration<ModelAssignmentAgentSlotEntity>
{
    public void Configure(EntityTypeBuilder<ModelAssignmentAgentSlotEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("model_assignment_agent_slots");
        builder.HasKey(entity => new { entity.Tenant, entity.AssignmentReleaseId, entity.AgentName });
        builder.Property(entity => entity.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.AgentName).HasMaxLength(128).IsRequired();
        builder.HasOne<ModelAssignmentReleaseEntity>()
            .WithMany()
            .HasForeignKey(entity => new { entity.Tenant, entity.AssignmentReleaseId })
            .HasPrincipalKey(entity => new { entity.Tenant, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ModelDeploymentVersionEntity>()
            .WithMany()
            .HasForeignKey(entity => new { entity.Tenant, entity.DeploymentVersionId })
            .HasPrincipalKey(entity => new { entity.Tenant, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TenantActiveModelAssignmentConfiguration : IEntityTypeConfiguration<TenantActiveModelAssignmentEntity>
{
    public void Configure(EntityTypeBuilder<TenantActiveModelAssignmentEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("tenant_active_model_assignments");
        builder.HasKey(entity => entity.Tenant);
        builder.Property(entity => entity.Tenant).HasMaxLength(128);
        builder.Property(entity => entity.ActivatedBy).HasMaxLength(256).IsRequired();
        builder.HasOne<ModelAssignmentReleaseEntity>()
            .WithMany()
            .HasForeignKey(entity => new { entity.Tenant, entity.AssignmentReleaseId })
            .HasPrincipalKey(entity => new { entity.Tenant, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ModelAssignmentEvaluationConfiguration : IEntityTypeConfiguration<ModelAssignmentEvaluationEntity>
{
    public void Configure(EntityTypeBuilder<ModelAssignmentEvaluationEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("model_assignment_evaluations");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Dataset).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Baseline).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Status).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.RequestedBy).HasMaxLength(256).IsRequired();
        builder.HasIndex(entity => new { entity.Tenant, entity.AssignmentReleaseId, entity.RequestedAt });
        builder.HasOne<ModelAssignmentReleaseEntity>()
            .WithMany()
            .HasForeignKey(entity => new { entity.Tenant, entity.AssignmentReleaseId })
            .HasPrincipalKey(entity => new { entity.Tenant, entity.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ModelControlPlaneAuditConfiguration : IEntityTypeConfiguration<ModelControlPlaneAuditEntity>
{
    public void Configure(EntityTypeBuilder<ModelControlPlaneAuditEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("model_control_plane_audit");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Actor).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Action).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.ResourceType).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.ConfigurationHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(entity => new { entity.Tenant, entity.OccurredAt });
    }
}
