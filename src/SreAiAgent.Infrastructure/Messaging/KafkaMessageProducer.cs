using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Messaging;

/// <summary>Publishes an outbox message to Kafka.</summary>
/// <remarks>
/// Only the dispatcher uses this. Nothing else in the platform is allowed to
/// produce directly: a message sent outside the outbox is either a notification
/// for a transaction that later rolled back, or one that vanished with the
/// process before its state change committed (ARCHITECTURE.md 9.4).
///
/// The producer is idempotent and waits for all in-sync replicas. Losing an
/// investigation request to a leader failover is not an acceptable trade for the
/// throughput this platform does not need: an incident every few minutes is not
/// a workload that justifies weakening delivery.
/// </remarks>
internal sealed class KafkaMessageProducer : IMessageProducer, IDisposable
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly ILogger<KafkaMessageProducer> _logger;

    public KafkaMessageProducer(IOptions<KafkaOptions> options, ILogger<KafkaMessageProducer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;

        _producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,

            // Exactly-once semantics on the producer side. Without this a retry
            // after a timeout can duplicate a message, and consumers already
            // have to be idempotent for redelivery; there is no reason to give
            // them more of it than the broker forces.
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageSendMaxRetries = 5,
            RetryBackoffMs = 200,

            // Bounded. An unbounded queue turns a broker outage into an
            // out-of-memory kill, which loses the messages it was holding.
            QueueBufferingMaxMessages = 10_000,
            LingerMs = 5,
            CompressionType = CompressionType.Snappy,
        }).Build();
    }

    /// <inheritdoc />
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Headers headers = new()
        {
            { "event-id", message.EventId.ToByteArray() },
            { "event-type", Encoding.UTF8.GetBytes(message.EventType) },
            { "correlation-id", Encoding.UTF8.GetBytes(message.CorrelationId) },
        };

        TraceContextPropagation.Inject(headers, message.TraceParent);

        await _producer.ProduceAsync(
            message.Topic,
            new Message<string, byte[]>
            {
                Key = message.PartitionKey,
                Value = Encoding.UTF8.GetBytes(message.PayloadJson),
                Headers = headers,
                Timestamp = new Timestamp(message.OccurredAt.UtcDateTime),
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Flush before disposing, or messages already accepted into the local
        // queue are dropped on shutdown after their rows were marked dispatched.
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}
