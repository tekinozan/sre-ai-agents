using SreAiAgent.Domain.Notifications;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Notifications;

/// <summary>Manages a tenant's Slack and Teams channels (gemini-budgets-notifications.md N).</summary>
public interface INotificationChannelService
{
    /// <summary>The tenant's channels.</summary>
    Task<IReadOnlyList<NotificationChannel>> ListAsync(string tenant, CancellationToken cancellationToken);

    /// <summary>Adds a channel. The webhook is given as a credential reference, never as a URL.</summary>
    Task<Result<NotificationChannel>> CreateAsync(
        string tenant,
        NotificationChannelKind kind,
        string displayName,
        string webhookRef,
        NotificationEvents events,
        string actor,
        CancellationToken cancellationToken);

    /// <summary>Removes a channel.</summary>
    Task<Result> DeleteAsync(string tenant, Guid channelId, string actor, CancellationToken cancellationToken);

    /// <summary>Sends a test message to one channel, so an operator can see the wiring work.</summary>
    Task<Result> SendTestAsync(string tenant, Guid channelId, CancellationToken cancellationToken);
}
