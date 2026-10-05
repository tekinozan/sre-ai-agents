using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SreAiAgent.Application.Abstractions.Notifications;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Notifications;

namespace SreAiAgent.Infrastructure;

/// <summary>Slack and Teams notifications (gemini-budgets-notifications.md N).</summary>
public static class NotificationDependencyInjection
{
    /// <summary>
    /// Registers channel management and the sender, which the API needs to
    /// manage channels and send a test message.
    /// </summary>
    public static IServiceCollection AddSreNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        int timeoutSeconds = configuration.GetValue<int?>($"{NotificationOptions.SectionName}:TimeoutSeconds") ?? 10;

        // No resilience handler: a webhook that fails is retried by redelivery
        // of the event, with backoff and a dead letter (D42). Retrying inside the
        // call as well would multiply the attempts against a receiver that is
        // already struggling.
        services.AddHttpClient(NotificationSender.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds));

        services.TryAddScoped<WebhookEndpointPolicy>();
        services.TryAddScoped<NotificationSender>();
        services.TryAddScoped<NotificationDispatcher>();
        services.TryAddScoped<INotificationChannelService, NotificationChannelService>();

        return services;
    }

    /// <summary>
    /// Registers the consumer and the budget monitor that actually send, when
    /// <c>Notifications:Enabled</c> is on. Belongs on one kind of host: the
    /// investigation worker.
    /// </summary>
    public static IServiceCollection AddSreNotificationDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSreNotifications(configuration);
        if (configuration.GetValue<bool?>($"{NotificationOptions.SectionName}:Enabled") ?? true)
        {
            services.AddHostedService<IncidentNotificationConsumer>();
            services.AddHostedService<BudgetThresholdMonitor>();
        }

        return services;
    }
}
