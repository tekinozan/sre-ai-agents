using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Notifications;

/// <summary>Where a notification is delivered.</summary>
public enum NotificationChannelKind
{
    Slack = 0,
    Teams = 1,
}

/// <summary>What a channel is told about.</summary>
[Flags]
public enum NotificationEvents
{
    None = 0,

    /// <summary>An investigation finished with a report.</summary>
    InvestigationCompleted = 1 << 0,

    /// <summary>An investigation could not finish.</summary>
    InvestigationFailed = 1 << 1,

    /// <summary>The tenant's model spend crossed 80% or 100% of its budget.</summary>
    BudgetThreshold = 1 << 2,

    All = InvestigationCompleted | InvestigationFailed | BudgetThreshold,
}

/// <summary>
/// A tenant's Slack or Teams destination (gemini-budgets-notifications.md N, D48).
/// </summary>
/// <remarks>
/// The webhook URL is never held here. Anyone with an incoming-webhook URL can
/// post into that channel, so it is a secret and is stored the way model
/// provider keys are: as a reference the credential resolver reads at send
/// time. A literal URL is refused at creation.
/// </remarks>
public sealed record NotificationChannel
{
    private const int MaximumNameLength = 128;

    private NotificationChannel(
        Guid id,
        string tenant,
        NotificationChannelKind kind,
        string displayName,
        string webhookRef,
        NotificationEvents events,
        bool enabled,
        DateTimeOffset createdAt,
        string createdBy)
    {
        Id = id;
        Tenant = tenant;
        Kind = kind;
        DisplayName = displayName;
        WebhookRef = webhookRef;
        Events = events;
        Enabled = enabled;
        CreatedAt = createdAt;
        CreatedBy = createdBy;
    }

    public Guid Id { get; }
    public string Tenant { get; }
    public NotificationChannelKind Kind { get; }
    public string DisplayName { get; }

    /// <summary>A credential reference (<c>env://</c> or <c>file://</c>) to the webhook URL.</summary>
    public string WebhookRef { get; }

    public NotificationEvents Events { get; }
    public bool Enabled { get; }
    public DateTimeOffset CreatedAt { get; }
    public string CreatedBy { get; }

    /// <summary>Whether this channel should hear about <paramref name="notification"/>.</summary>
    public bool Wants(NotificationEvents notification) => Enabled && (Events & notification) == notification;

    /// <summary>Validates and creates a channel.</summary>
    /// <exception cref="DomainException">A field is out of range.</exception>
    public static NotificationChannel Create(
        string tenant,
        NotificationChannelKind kind,
        string displayName,
        string webhookRef,
        NotificationEvents events,
        DateTimeOffset createdAt,
        string createdBy)
    {
        Guard.AgainstNullOrWhiteSpace(tenant, DomainErrorCodes.NotificationChannelInvalid);
        Guard.AgainstNullOrWhiteSpace(createdBy, DomainErrorCodes.NotificationChannelInvalid);
        Guard.AgainstNullOrWhiteSpace(displayName, DomainErrorCodes.NotificationChannelInvalid);
        Guard.AgainstNullOrWhiteSpace(webhookRef, DomainErrorCodes.NotificationChannelInvalid);
        if (displayName.Length > MaximumNameLength)
        {
            throw new DomainException(DomainErrorCodes.NotificationChannelInvalid, "The channel name is too long.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new DomainException(DomainErrorCodes.NotificationChannelInvalid, "The channel kind is not known.");
        }

        if (events == NotificationEvents.None || (events & ~NotificationEvents.All) != NotificationEvents.None)
        {
            throw new DomainException(DomainErrorCodes.NotificationChannelInvalid, "A channel must subscribe to at least one known event.");
        }

        return new NotificationChannel(
            Guid.CreateVersion7(), tenant, kind, displayName.Trim(), webhookRef, events, true, createdAt.ToUniversalTime(), createdBy);
    }

    /// <summary>Recreates a channel read from storage.</summary>
    public static NotificationChannel Restore(
        Guid id,
        string tenant,
        NotificationChannelKind kind,
        string displayName,
        string webhookRef,
        NotificationEvents events,
        bool enabled,
        DateTimeOffset createdAt,
        string createdBy)
        => new(id, tenant, kind, displayName, webhookRef, events, enabled, createdAt, createdBy);
}
