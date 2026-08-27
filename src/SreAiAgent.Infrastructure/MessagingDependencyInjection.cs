using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SreAiAgent.Infrastructure.Alerts;
using SreAiAgent.Infrastructure.Messaging;
using SreAiAgent.Infrastructure.Messaging.Consumers;

namespace SreAiAgent.Infrastructure;

/// <summary>
/// Registers Kafka publication, consumption and alert normalization
/// (ARCHITECTURE.md 10).
/// </summary>
/// <remarks>
/// Split into three methods rather than one, because the three hosts need
/// different subsets. Every host stages messages, only some drain the outbox,
/// and exactly one consumes investigation requests. A single AddSreMessaging
/// would put a Kafka consumer group in the API process, where it would take a
/// partition assignment and then not process it.
/// </remarks>
public static class MessagingDependencyInjection
{
    /// <summary>
    /// Registers the producer and the outbox dispatcher.
    /// </summary>
    /// <remarks>
    /// Safe on every replica: rows are claimed with FOR UPDATE SKIP LOCKED, so
    /// dispatchers share the table without blocking each other or publishing the
    /// same row twice. Running it in more than one deployment is a deliberate
    /// availability choice rather than an oversight; a single dispatcher is a
    /// single point at which every investigation stops starting.
    /// </remarks>
    public static IServiceCollection AddSreOutboxDispatcher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Singleton: a Kafka producer owns connections, batching and an internal
        // queue, and creating one per scope would give up batching entirely and
        // leak sockets under load.
        services.TryAddSingleton<IMessageProducer, KafkaMessageProducer>();
        services.AddHostedService<OutboxDispatcher>();

        return services;
    }

    /// <summary>Registers the provider adapters and the normalizer in front of them.</summary>
    public static IServiceCollection AddSreAlertIngestion(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Listed individually. An adapter decides what counts as the same alert,
        // and which ones a deployment accepts should be visible in a diff rather
        // than inferred from a naming convention.
        services.AddSingleton<IAlertSourceAdapter, AlertmanagerAlertAdapter>();
        services.AddSingleton<IAlertSourceAdapter, DatadogAlertAdapter>();
        services.AddSingleton<IAlertSourceAdapter, GrafanaAlertAdapter>();
        services.AddSingleton<IAlertSourceAdapter, WebhookAlertAdapter>();

        services.TryAddSingleton(static provider =>
            new AlertNormalizer(provider.GetRequiredService<IEnumerable<IAlertSourceAdapter>>()));

        return services;
    }

    /// <summary>Registers the consumer that turns an accepted alert into an incident.</summary>
    public static IServiceCollection AddSreAlertConsumer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<AlertReceivedConsumer>();

        return services;
    }

    /// <summary>Registers the consumer that turns a request into a running investigation.</summary>
    public static IServiceCollection AddSreInvestigationConsumer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<InvestigationRequestedConsumer>();

        return services;
    }
}
