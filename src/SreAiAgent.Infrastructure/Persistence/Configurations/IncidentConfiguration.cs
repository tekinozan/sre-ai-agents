using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SreAiAgent.Domain.Incidents;

namespace SreAiAgent.Infrastructure.Persistence.Configurations;

/// <summary>Maps the incident aggregate to <c>incidents</c>.</summary>
internal sealed class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("incidents");
        builder.HasKey(incident => incident.Id);

        // xmin is PostgreSQL's own system row version. Using it costs no extra
        // column and no application bookkeeping, and it catches the race that
        // actually happens here: a duplicate alert and a workflow transition
        // touching the same incident at once.
        builder.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

        builder.Property(incident => incident.Tenant).HasMaxLength(128).IsRequired();
        builder.Property(incident => incident.Source).HasMaxLength(64).IsRequired();
        builder.Property(incident => incident.ExternalId).HasMaxLength(256).IsRequired();
        builder.Property(incident => incident.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(incident => incident.Title).HasMaxLength(512).IsRequired();
        builder.Property(incident => incident.Description).HasMaxLength(4000);
        builder.Property(incident => incident.Service).HasMaxLength(128).IsRequired();
        builder.Property(incident => incident.Environment).HasMaxLength(64).IsRequired();
        builder.Property(incident => incident.FailureReason).HasMaxLength(1024);

        // Enums are stored as text. An integer column saves four bytes and costs
        // a decoder ring every time someone reads the table during an incident,
        // which is exactly when nobody wants one.
        builder.Property(incident => incident.Severity)
            .HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(incident => incident.Status)
            .HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(incident => incident.Labels)
            .HasColumnType("jsonb")
            .HasConversion(
                labels => JsonbConverters.Serialize(labels),
                json => JsonbConverters.DeserializeLabels(json))
            .Metadata.SetValueComparer(DictionaryComparers.Ordinal);

        // The deduplication constraint of ARCHITECTURE.md 9.3: one active
        // incident per alert identity. Unique, because two deliveries can both
        // read "nothing active" and only the database can break that tie (D43).
        // Filtered to open incidents so a resolved one stops blocking a genuine
        // recurrence, and ordered tenant-first because every query is
        // tenant-scoped.
        builder.HasIndex(incident => new { incident.Tenant, incident.Source, incident.Fingerprint })
            .HasDatabaseName("ix_incidents_active_fingerprint")
            .HasFilter("status NOT IN ('Resolved', 'Cancelled')")
            .IsUnique();

        // The cooldown half of the same lookup: the most recently alerted
        // resolved incident for an identity. The active index cannot serve it,
        // and without this the lookup scans a tenant's whole resolved history on
        // every alert.
        builder.HasIndex(incident => new { incident.Tenant, incident.Source, incident.Fingerprint, incident.LastAlertAt })
            .HasDatabaseName("ix_incidents_fingerprint_last_alert");

        builder.HasIndex(incident => new { incident.Tenant, incident.Status, incident.DetectedAt })
            .HasDatabaseName("ix_incidents_tenant_status_detected");

    }
}

/// <summary>Maps the timeline to <c>incident_events</c>.</summary>
internal sealed class IncidentTimelineEntryConfiguration : IEntityTypeConfiguration<IncidentTimelineEntry>
{
    public void Configure(EntityTypeBuilder<IncidentTimelineEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("incident_events");
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.EventType).HasMaxLength(128).IsRequired();
        builder.Property(entry => entry.Summary).HasMaxLength(1024).IsRequired();
        builder.Property(entry => entry.Actor).HasMaxLength(256);
        builder.Property(entry => entry.PayloadJson).HasColumnType("jsonb");

        // Ordered by id rather than by timestamp: ids are version 7, so they sort
        // chronologically and two entries written in the same millisecond still
        // have a stable order, which a timestamp alone does not give.
        builder.HasIndex(entry => new { entry.IncidentId, entry.Id })
            .HasDatabaseName("ix_incident_events_incident");
    }
}
