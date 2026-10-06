namespace SreAiAgent.Contracts.Notifications;

/// <summary>Body of <c>POST /v1/notification-channels</c>.</summary>
/// <remarks>
/// <c>webhook_ref</c> is a credential reference, <c>env://NAME</c> or
/// <c>file://path</c>, to the incoming-webhook URL. There is deliberately no
/// field for the URL itself: anyone holding it can post into the channel.
/// <c>events</c> lists <c>investigation_completed</c>,
/// <c>investigation_failed</c> and <c>budget_threshold</c>; omitted means all.
/// </remarks>
public sealed record CreateNotificationChannelRequest
{
    public required string Kind { get; init; }

    public required string DisplayName { get; init; }

    public required string WebhookRef { get; init; }

    public IReadOnlyList<string>? Events { get; init; }
}

/// <summary>A notification channel, as returned by the API.</summary>
public sealed record NotificationChannelResponse
{
    public required Guid ChannelId { get; init; }

    public required string Kind { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>The reference, never the URL it resolves to.</summary>
    public required string WebhookRef { get; init; }

    public required IReadOnlyList<string> Events { get; init; }

    public required bool Enabled { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }
}
