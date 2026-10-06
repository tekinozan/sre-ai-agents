using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Notifications;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.Infrastructure.Notifications;

/// <summary>
/// Decides which webhook URLs may be called (D48).
/// </summary>
/// <remarks>
/// The URL comes from a secret an operator configured, not from a request, but
/// it is still an outbound call this platform makes on a tenant's behalf. In
/// production it must be HTTPS to Slack's or Teams' own hosts, or a host the
/// installation allowed explicitly; never an IP address, which is how a
/// webhook would be pointed at a metadata or internal service.
/// </remarks>
internal sealed class WebhookEndpointPolicy(IHostEnvironment environment, IOptions<NotificationOptions> options)
{
    private readonly bool _production = environment.IsProduction();
    private readonly HashSet<string> _extraHosts = new(options.Value.AllowedWebhookHosts, StringComparer.OrdinalIgnoreCase);

    public Result<Uri> Validate(NotificationChannelKind kind, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return Refused("The webhook reference does not resolve to an HTTP URL.");
        }

        if (!_production)
        {
            return Result<Uri>.Success(uri);
        }

        bool allowed = uri.Scheme == Uri.UriSchemeHttps
            && !IPAddress.TryParse(uri.Host, out _)
            && (_extraHosts.Contains(uri.Host) || kind switch
            {
                NotificationChannelKind.Slack => string.Equals(uri.Host, "hooks.slack.com", StringComparison.OrdinalIgnoreCase),
                NotificationChannelKind.Teams => uri.Host.EndsWith(".webhook.office.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".logic.azure.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".environment.api.powerplatform.com", StringComparison.OrdinalIgnoreCase),
                _ => false,
            });
        return allowed ? Result<Uri>.Success(uri) : Refused("The webhook host is not allowed in production.");
    }

    private static Result<Uri> Refused(string message)
        => Result<Uri>.Failure(Error.Permanent(DomainErrorCodes.NotificationChannelInvalid, message));
}

/// <summary>Posts one message to one channel.</summary>
internal sealed partial class NotificationSender(
    IHttpClientFactory clients,
    ICredentialResolver credentials,
    WebhookEndpointPolicy policy,
    ILogger<NotificationSender> logger)
{
    public const string HttpClientName = "sre-notifications";

    public async Task<Result> SendAsync(
        NotificationChannel channel,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        Result<CredentialValue> url = await credentials
            .ResolveAsync(channel.WebhookRef, cancellationToken)
            .ConfigureAwait(false);
        if (url.IsFailure)
        {
            return Result.Failure(url.Error);
        }

        Result<Uri> target = policy.Validate(channel.Kind, url.Value.Value);
        if (target.IsFailure)
        {
            return Result.Failure(target.Error);
        }

        using HttpRequestMessage request = new(HttpMethod.Post, target.Value)
        {
            Content = new StringContent(
                NotificationFormats.Serialize(channel.Kind, message), Encoding.UTF8, "application/json"),
        };

        try
        {
            using HttpResponseMessage response = await clients
                .CreateClient(HttpClientName)
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return Result.Success();
            }

            // The URL is a secret, so only the channel and the status are logged.
            LogRejected(logger, channel.Id, channel.Kind.ToString(), (int)response.StatusCode);
            return Failed();
        }
        catch (HttpRequestException exception)
        {
            LogUnreachable(logger, channel.Id, channel.Kind.ToString(), exception.HttpRequestError.ToString());
            return Failed();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, channel.Id, channel.Kind.ToString(), "timeout");
            return Failed();
        }
    }

    private static Result Failed()
        => Result.Failure(Error.Transient(
            ApplicationErrorCodes.NotificationDeliveryFailed, "The notification channel did not accept the message."));

    [LoggerMessage(EventId = 8101, Level = LogLevel.Warning,
        Message = "Notification channel {ChannelId} ({Kind}) answered {StatusCode}.")]
    private static partial void LogRejected(ILogger logger, Guid channelId, string kind, int statusCode);

    [LoggerMessage(EventId = 8102, Level = LogLevel.Warning,
        Message = "Notification channel {ChannelId} ({Kind}) could not be reached: {Reason}.")]
    private static partial void LogUnreachable(ILogger logger, Guid channelId, string kind, string reason);
}

/// <summary>
/// Sends a message to every channel of a tenant that wants it, once per channel.
/// </summary>
/// <remarks>
/// Each channel is claimed before it is sent to, by inserting
/// <c>(delivery_key, channel_id)</c>; a redelivery, a retry or a second replica
/// that loses the insert skips that channel. A failed send releases its claim so
/// the retry tries it again, and the dispatch as a whole fails so the caller
/// retries. The claim is written in a scope of its own: committing it through
/// the consumer's context would also commit the consumer's inbox row before the
/// work was done (D25).
///
/// At most once per channel, not at least once: a crash between the claim and
/// the send loses that one notification. A duplicate page was judged worse than
/// a missing chat message about a report that is also in the API.
/// </remarks>
internal sealed class NotificationDispatcher(
    IServiceScopeFactory scopes,
    NotificationSender sender,
    TimeProvider timeProvider)
{
    public async Task<Result> DispatchAsync(string tenant, NotificationMessage message, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        SreDbContext context = scope.ServiceProvider.GetRequiredService<SreDbContext>();

        List<NotificationChannel> channels = (await context.NotificationChannels
                .AsNoTracking()
                .Where(channel => channel.Tenant == tenant && channel.Enabled)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(NotificationChannelService.ToDomain)
            .Where(channel => channel.Wants(message.Event))
            .ToList();

        bool anyFailed = false;
        foreach (NotificationChannel channel in channels)
        {
            if (!await TryClaimAsync(context, tenant, message.DeliveryKey, channel.Id, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            Result sent = await sender.SendAsync(channel, message, cancellationToken).ConfigureAwait(false);
            if (sent.IsFailure)
            {
                anyFailed = true;
                await context.NotificationDeliveries
                    .Where(delivery => delivery.DeliveryKey == message.DeliveryKey && delivery.ChannelId == channel.Id)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return anyFailed
            ? Result.Failure(Error.Transient(
                ApplicationErrorCodes.NotificationDeliveryFailed, "At least one notification channel did not accept the message."))
            : Result.Success();
    }

    private async Task<bool> TryClaimAsync(
        SreDbContext context,
        string tenant,
        string deliveryKey,
        Guid channelId,
        CancellationToken cancellationToken)
    {
        NotificationDeliveryEntity claim = new()
        {
            DeliveryKey = deliveryKey,
            ChannelId = channelId,
            Tenant = tenant,
            ClaimedAt = timeProvider.GetUtcNow(),
        };
        context.NotificationDeliveries.Add(claim);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            context.Entry(claim).State = EntityState.Detached;
            return false;
        }
    }
}
