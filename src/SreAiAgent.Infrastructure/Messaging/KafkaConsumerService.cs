using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Messaging;

/// <summary>
/// Consumes one topic with at-least-once delivery and idempotent handling
/// (ARCHITECTURE.md 10.2).
/// </summary>
/// <remarks>
/// Consumption and handling run on the same logical flow, on a dedicated
/// long-running thread. Confluent's consumer is not thread-safe, and the offset
/// has to be committed only after the effect and its inbox row have been
/// written; handing messages to a separate processing loop would mean either
/// synchronising every consumer call or committing offsets from a thread that
/// does not own the consumer. Ordering within a partition also survives, which
/// matters because an incident's messages share a partition key.
///
/// The order is: extract trace context, deduplicate through the inbox, handle,
/// commit. A crash anywhere before the commit redelivers, and the inbox row
/// written in the same transaction as the effect is what makes that redelivery
/// harmless.
/// </remarks>
/// <typeparam name="TPayload">The payload carried by the envelope.</typeparam>
internal abstract partial class KafkaConsumerService<TPayload> : BackgroundService
    where TPayload : class
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KafkaOptions _options;
    private readonly ILogger _logger;

    protected KafkaConsumerService(
        IServiceScopeFactory scopeFactory,
        IOptions<KafkaOptions> options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The topic to subscribe to.</summary>
    protected abstract string Topic { get; }

    /// <summary>
    /// Identifies this consumer in the inbox.
    /// </summary>
    /// <remarks>
    /// Part of the deduplication key together with the event id, so two different
    /// consumers of the same message each get to process it exactly once rather
    /// than the second one finding the first one's row and skipping its own work.
    /// </remarks>
    protected abstract string ConsumerName { get; }

    /// <summary>Generated metadata for the envelope this consumer reads.</summary>
    protected abstract JsonTypeInfo<EventEnvelope<TPayload>> EnvelopeTypeInfo { get; }

    /// <summary>Applies the effect of one message.</summary>
    protected abstract Task HandleAsync(
        EventEnvelope<TPayload> envelope,
        IServiceProvider services,
        CancellationToken cancellationToken);

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Factory
            .StartNew(
                () => RunAsync(stoppingToken),
                stoppingToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        using IConsumer<string, byte[]> consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroup,

            // Never automatic. The offset is the platform's statement that a
            // message has been dealt with, and auto-commit makes that statement
            // on a timer regardless of whether the work succeeded.
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,

            // A new consumer group reads the backlog rather than skipping it: an
            // incident that arrived while the worker was being deployed still has
            // to be investigated.
            AutoOffsetReset = AutoOffsetReset.Earliest,
            MaxPollIntervalMs = 300_000,
            SessionTimeoutMs = 45_000,
        }).Build();

        consumer.Subscribe(Topic);
        LogSubscribed(_logger, Topic, _options.ConsumerGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? result = consumer.Consume(TimeSpan.FromMilliseconds(500));

                if (result?.Message is null)
                {
                    continue;
                }

                await ProcessAsync(consumer, result, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown: stop accepting, leave uncommitted work to be
            // redelivered (ARCHITECTURE.md 19.2).
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task ProcessAsync(
        IConsumer<string, byte[]> consumer,
        ConsumeResult<string, byte[]> result,
        CancellationToken cancellationToken)
    {
        // Parented to the producer's context so the whole path, from the HTTP
        // request that opened the incident to the worker that investigates it,
        // is one trace (ARCHITECTURE.md 16.1).
        ActivityContext parent = TraceContextPropagation.Extract(result.Message.Headers);

        using Activity? activity = SreActivitySources.Messaging.StartActivity(
            $"{Topic} receive", ActivityKind.Consumer, parent);

        EventEnvelope<TPayload>? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize(result.Message.Value, EnvelopeTypeInfo);
        }
        catch (JsonException exception)
        {
            // Unparseable. Retrying cannot help, and refusing to commit would
            // stall the partition behind a message that will never be readable.
            LogUnreadable(_logger, Topic, result.TopicPartitionOffset.ToString(), exception);
            consumer.Commit(result);
            return;
        }

        if (envelope is null)
        {
            consumer.Commit(result);
            return;
        }

        activity
            .SetSafeTag(TraceAttributes.CorrelationId, envelope.CorrelationId)
            .SetSafeTag(TraceAttributes.MessagingTopic, Topic)
            .SetSafeTag(TraceAttributes.MessagingEventType, envelope.EventType);

        using IServiceScope scope = _scopeFactory.CreateScope();

        IInboxStore inbox = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        bool isNew = await inbox
            .TryBeginAsync(envelope.EventId, envelope.EventType, ConsumerName, cancellationToken)
            .ConfigureAwait(false);

        if (!isNew)
        {
            // Already handled. Delivery is at-least-once, so this is an expected
            // outcome rather than an error worth logging at every occurrence.
            consumer.Commit(result);
            return;
        }

        try
        {
            await HandleAsync(envelope, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);

            // One save, so the inbox row and the effect land in the same
            // transaction. Committing the offset only afterwards is what makes a
            // crash in between a redelivery rather than a lost message.
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            consumer.Commit(result);
        }
        catch (DbUpdateException exception) when (IsDuplicateDelivery(exception))
        {
            // Another delivery of this message committed while this one was
            // working. The primary key, not the earlier read, is what decides;
            // the effect rolled back with the failed transaction, so acknowledging
            // here discards a duplicate rather than losing work.
            consumer.Commit(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberately not committed. The message is redelivered, and the
            // inbox row rolled back with the failed transaction so the retry is
            // not mistaken for a duplicate.
            activity?.SetStatus(ActivityStatusCode.Error);
            LogHandlerFailed(_logger, Topic, envelope.EventType, exception);
        }
    }

    /// <summary>
    /// Whether a failed save was a losing race with another delivery rather than
    /// a real error.
    /// </summary>
    /// <remarks>
    /// Any unique violation qualifies: the inbox row, the idempotency claim and
    /// the incident's own deduplication index all express the same fact, that
    /// another delivery of this message got there first. All of them rolled the
    /// transaction back, so nothing partial was written and acknowledging is
    /// safe.
    ///
    /// Matched on the SQL state rather than on constraint names, which are a
    /// schema detail a migration may rename while this keeps compiling.
    /// </remarks>
    private static bool IsDuplicateDelivery(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    [LoggerMessage(
        EventId = 7201,
        Level = LogLevel.Information,
        Message = "Subscribed to {Topic} as group {ConsumerGroup}.")]
    private static partial void LogSubscribed(ILogger logger, string topic, string consumerGroup);

    [LoggerMessage(
        EventId = 7202,
        Level = LogLevel.Error,
        Message = "A message on {Topic} at {Offset} could not be read and was skipped.")]
    private static partial void LogUnreadable(
        ILogger logger, string topic, string offset, Exception exception);

    [LoggerMessage(
        EventId = 7203,
        Level = LogLevel.Error,
        Message = "Handling {EventType} from {Topic} failed; the message will be redelivered.")]
    private static partial void LogHandlerFailed(
        ILogger logger, string topic, string eventType, Exception exception);
}
