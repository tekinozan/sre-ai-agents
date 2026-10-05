using System.Security.Claims;
using SreAiAgent.Api.Http;
using SreAiAgent.Api.Security;
using SreAiAgent.Application.Abstractions.Notifications;
using SreAiAgent.Contracts.Notifications;
using SreAiAgent.Domain.Notifications;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Api.Endpoints;

/// <summary>
/// The tenant's Slack and Teams channels (gemini-budgets-notifications.md N, D48).
/// </summary>
public static class NotificationEndpoints
{
    /// <summary>Maps the notification channel endpoints.</summary>
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder channels = builder.MapGroup("/v1/notification-channels").WithTags("Notifications");

        channels.MapGet("/", ListAsync)
            .RequireAuthorization(SrePolicies.ManageNotifications)
            .WithSummary("Lists the tenant's notification channels.");

        channels.MapPost("/", CreateAsync)
            .RequireAuthorization(SrePolicies.ManageNotifications)
            .WithSummary("Adds a Slack or Teams channel.");

        channels.MapDelete("/{channelId:guid}", DeleteAsync)
            .RequireAuthorization(SrePolicies.ManageNotifications)
            .WithSummary("Removes a channel.");

        channels.MapPost("/{channelId:guid}/test", TestAsync)
            .RequireAuthorization(SrePolicies.ManageNotifications)
            .WithSummary("Sends a test message to a channel.");

        return builder;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        INotificationChannelService service,
        CancellationToken cancellationToken)
        => Results.Json((await service.ListAsync(principal.Tenant(), cancellationToken).ConfigureAwait(false))
            .Select(ToResponse)
            .ToArray());

    private static async Task<IResult> CreateAsync(
        CreateNotificationChannelRequest request,
        ClaimsPrincipal principal,
        INotificationChannelService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.TryParse(request.Kind, ignoreCase: true, out NotificationChannelKind kind) || !Enum.IsDefined(kind))
        {
            return Invalid("The channel kind must be 'slack' or 'teams'.");
        }

        NotificationEvents events = NotificationEvents.None;
        foreach (string token in request.Events ?? [])
        {
            if (!Enum.TryParse(token.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out NotificationEvents one)
                || one is NotificationEvents.None or NotificationEvents.All
                || !Enum.IsDefined(one))
            {
                return Invalid($"Unknown notification event '{token}'.");
            }

            events |= one;
        }

        Result<NotificationChannel> result = await service
            .CreateAsync(
                principal.Tenant(),
                kind,
                request.DisplayName,
                request.WebhookRef,
                events == NotificationEvents.None ? NotificationEvents.All : events,
                principal.Actor(),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Json(ToResponse(result.Value), statusCode: StatusCodes.Status201Created)
            : ResultExtensions.Problem(result.Error);
    }

    private static async Task<IResult> DeleteAsync(
        Guid channelId,
        ClaimsPrincipal principal,
        INotificationChannelService service,
        CancellationToken cancellationToken)
        => (await service.DeleteAsync(principal.Tenant(), channelId, principal.Actor(), cancellationToken).ConfigureAwait(false))
            .ToHttpResult();

    private static async Task<IResult> TestAsync(
        Guid channelId,
        ClaimsPrincipal principal,
        INotificationChannelService service,
        CancellationToken cancellationToken)
        => (await service.SendTestAsync(principal.Tenant(), channelId, cancellationToken).ConfigureAwait(false))
            .ToHttpResult(StatusCodes.Status202Accepted);

    private static IResult Invalid(string message)
        => ResultExtensions.Problem(Error.Permanent(DomainErrorCodes.NotificationChannelInvalid, message));

    private static NotificationChannelResponse ToResponse(NotificationChannel channel) => new()
    {
        ChannelId = channel.Id,
        Kind = channel.Kind == NotificationChannelKind.Slack ? "slack" : "teams",
        DisplayName = channel.DisplayName,
        WebhookRef = channel.WebhookRef,
        Events = new[]
            {
                (NotificationEvents.InvestigationCompleted, "investigation_completed"),
                (NotificationEvents.InvestigationFailed, "investigation_failed"),
                (NotificationEvents.BudgetThreshold, "budget_threshold"),
            }
            .Where(pair => (channel.Events & pair.Item1) == pair.Item1)
            .Select(static pair => pair.Item2)
            .ToArray(),
        Enabled = channel.Enabled,
        CreatedAt = channel.CreatedAt,
        CreatedBy = channel.CreatedBy,
    };
}
