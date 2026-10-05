using System.Diagnostics;
using System.Text;
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
using SreAiAgent.Application.Errors;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Contracts.Serialization;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Persistence.Configurations;

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
    private readonly IMessageProducer _producer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    protected KafkaConsumerService(
        IServiceScopeFactory scopeFactory,
        IOptions<KafkaOptions> options,
        IMessageProducer producer,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _options = options.Value;
        _producer = producer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>System clock or injected test provider.</summary>
    protected TimeProvider TimeProvider => _timeProvider;

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
        ConsumerConfig config = new()
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
        };

        KafkaClientConfiguration.ApplySecurity(config, _options);

        using IConsumer<string, byte[]> consumer = new ConsumerBuilder<string, byte[]>(config).Build();

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

    internal async Task ProcessAsync(
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
            await HandleMalformedRecordAsync(consumer, result, exception, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (envelope is null)
        {
            JsonException exception = new("The message envelope was null.");
            await HandleMalformedRecordAsync(consumer, result, exception, cancellationToken).ConfigureAwait(false);
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
            // If an earlier delivery wrote the effect and opened an attempt row but
            // failed to commit its offset, this duplicate delivery settles it before acknowledging.
            await SettleSucceededAsync(scope.ServiceProvider, result, cancellationToken).ConfigureAwait(false);
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

            // Settle any prior failure attempts before committing the offset.
            // If committing the offset fails, the broker redelivers the message;
            // redelivery is recognized as an inbox duplicate and settles the attempt before acknowledging.
            await SettleSucceededAsync(scope.ServiceProvider, result, cancellationToken).ConfigureAwait(false);
            consumer.Commit(result);
        }
        catch (DbUpdateException exception) when (IsDuplicateDelivery(exception))
        {
            // Another delivery of this message committed while this one was
            // working. The inbox key, not the earlier read, is what decides; the
            // effect rolled back with the failed transaction, so acknowledging
            // here discards a duplicate rather than losing work.
            // Settle from a fresh scope because the handler's DbContext has a failed save.
            using IServiceScope settlementScope = _scopeFactory.CreateScope();
            await SettleSucceededAsync(settlementScope.ServiceProvider, result, cancellationToken).ConfigureAwait(false);
            consumer.Commit(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await HandleFailureAsync(consumer, result, envelope, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SettleSucceededAsync(
        IServiceProvider services,
        ConsumeResult<string, byte[]> result,
        CancellationToken cancellationToken)
    {
        IDeliveryAttemptStore attempts = services.GetRequiredService<IDeliveryAttemptStore>();
        return attempts.SettleAsync(
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            ConsumerName,
            SettlementKind.Succeeded,
            cancellationToken);
    }

    /// <summary>
    /// Whether a failed save was another delivery of this same message getting
    /// there first, rather than a real error.
    /// </summary>
    /// <remarks>
    /// Only a violation of the inbox key means that. The idempotency claim and
    /// the incident's deduplication index are keyed on the alert, not on the
    /// message: a violation there is a different message for the same alert
    /// losing a race, and acknowledging it discarded the alert with no timeline
    /// entry, no recurrence count and no escalation (BUG-001). That case takes
    /// the failure path instead, and its redelivery finds the winner and folds
    /// into it.
    ///
    /// Matched on the table, named once in the inbox mapping, rather than on a
    /// constraint name that a migration may rename while this keeps compiling.
    /// </remarks>
    internal static bool IsDuplicateDelivery(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            TableName: InboxMessageConfiguration.TableName,
        };


    /// <summary>
    /// Handles execution failures thrown while processing a valid message envelope.
    /// </summary>
    /// <remarks>
    /// Possibly-transient handler failure: unlike unparseable records which are terminal on
    /// delivery 1, handler failures may be caused by transient dependency outages (database
    /// connectivity, downstream timeouts) and are retried up to MaxDeliveryAttempts.
    /// </remarks>
    private async Task HandleFailureAsync(
        IConsumer<string, byte[]> consumer,
        ConsumeResult<string, byte[]> result,
        EventEnvelope<TPayload>? envelope,
        Exception exception,
        CancellationToken cancellationToken)
    {
        Activity? activity = Activity.Current;
        activity?.SetStatus(ActivityStatusCode.Error);
        LogHandlerFailed(_logger, Topic, envelope?.EventType ?? "unknown", exception);

        // Independent failure persistence: resolve from a fresh scope so failure
        // attempt tracking commits even though the handler's transaction rolled back.
        // Reusing the rolled-back scope would roll the counter back with the effect,
        // causing poison messages to retry forever.
        using IServiceScope failureScope = _scopeFactory.CreateScope();
        IDeliveryAttemptStore attempts = failureScope.ServiceProvider
            .GetRequiredService<IDeliveryAttemptStore>();

        int attempted = await attempts
            .RecordFailureAsync(
                result.Topic,
                result.Partition.Value,
                result.Offset.Value,
                ConsumerName,
                envelope?.EventId,
                envelope?.EventType,
                exception.Message,
                cancellationToken)
            .ConfigureAwait(false);

        if (attempted < _options.MaxDeliveryAttempts)
        {
            // Not committed: redelivery is the retry, and the inbox row rolled
            // back with the failed transaction so the retry is not mistaken for a duplicate.
            await RewindAsync(consumer, result, attempted, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await PublishDeadLetterAsync(result, envelope, exception, attempted, cancellationToken).ConfigureAwait(false))
        {
            // The dead letter did not reach the broker. Committing here would
            // discard the message entirely, so leave it for redelivery.
            await RewindAsync(consumer, result, attempted, cancellationToken).ConfigureAwait(false);
            return;
        }

        await attempts
            .SettleAsync(
                result.Topic,
                result.Partition.Value,
                result.Offset.Value,
                ConsumerName,
                SettlementKind.DeadLetter,
                cancellationToken)
            .ConfigureAwait(false);

        Guid deadLetterEventId = DeadLetterIdentity.CreateDeterministicEventId(
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            ConsumerName);

        LogPoisonDeadLettered(_logger, Topic, envelope?.EventType ?? "unknown", deadLetterEventId, attempted);
        consumer.Commit(result);
    }

    /// <summary>
    /// Moves the partition back to a message that was left uncommitted, so the
    /// next poll returns it.
    /// </summary>
    /// <remarks>
    /// Leaving the offset uncommitted is not enough on its own. The consumer's
    /// fetch position has already moved past the message, so without a seek it
    /// returns only after a restart or a rebalance; and the moment a later
    /// message on the same partition succeeds, its commit covers this offset too
    /// and the failed message is skipped for good. The pause before retrying
    /// keeps a dependency outage from turning the partition into a hot loop; the
    /// attempt counter, not the pause, is what bounds it.
    ///
    /// A failed seek means the partition was revoked while this message was
    /// handled. Its new owner starts from the last committed offset, which is
    /// still this message, so there is nothing left to do here.
    /// </remarks>
    private async Task RewindAsync(
        IConsumer<string, byte[]> consumer,
        ConsumeResult<string, byte[]> result,
        int attempted,
        CancellationToken cancellationToken)
    {
        TimeSpan pause = TimeSpan.FromMilliseconds(Math.Min(250 * attempted, 5_000));
        await Task.Delay(pause, _timeProvider, cancellationToken).ConfigureAwait(false);

        try
        {
            consumer.Seek(result.TopicPartitionOffset);
        }
        catch (KafkaException exception)
        {
            LogRewindFailed(_logger, Topic, result.TopicPartitionOffset.ToString(), exception);
        }
    }

    /// <summary>
    /// Handles unparseable or malformed messages that cannot be decoded into an envelope.
    /// </summary>
    /// <remarks>
    /// Terminal on the first delivery: unlike handler failures which may be transient (such as
    /// a database outage or timeout) and are retried up to MaxDeliveryAttempts, a JSON decode
    /// failure is deterministic. Repeated deliveries cannot make invalid bytes parse, and refusing
    /// to commit would stall the partition forever behind an unreadable message.
    ///
    /// We record the attempt once for audit, publish the dead letter with the raw payload,
    /// settle the attempt counter as dead_letter, and commit immediately. If the dead-letter
    /// publication fails, we do not commit so broker redelivery will re-attempt publication.
    /// </remarks>
    private async Task HandleMalformedRecordAsync(
        IConsumer<string, byte[]> consumer,
        ConsumeResult<string, byte[]> result,
        Exception exception,
        CancellationToken cancellationToken)
    {
        Activity? activity = Activity.Current;
        activity?.SetStatus(ActivityStatusCode.Error);
        LogUnreadable(_logger, Topic, result.TopicPartitionOffset.ToString(), exception);

        using IServiceScope failureScope = _scopeFactory.CreateScope();
        IDeliveryAttemptStore attempts = failureScope.ServiceProvider
            .GetRequiredService<IDeliveryAttemptStore>();

        // Record the failure once so the attempt row exists as an audit trail
        await attempts
            .RecordFailureAsync(
                result.Topic,
                result.Partition.Value,
                result.Offset.Value,
                ConsumerName,
                eventId: null,
                eventType: null,
                exception.Message,
                cancellationToken)
            .ConfigureAwait(false);

        // Terminal error: publish the dead letter immediately without retrying
        if (!await PublishDeadLetterAsync(result, envelope: null, exception, attempts: 1, cancellationToken).ConfigureAwait(false))
        {
            // The dead letter did not reach the broker. Committing here would
            // discard the unreadable message without a trace, so leave it for redelivery.
            return;
        }

        await attempts
            .SettleAsync(
                result.Topic,
                result.Partition.Value,
                result.Offset.Value,
                ConsumerName,
                SettlementKind.DeadLetter,
                cancellationToken)
            .ConfigureAwait(false);

        Guid deadLetterEventId = DeadLetterIdentity.CreateDeterministicEventId(
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            ConsumerName);

        LogPoisonDeadLettered(_logger, Topic, "unreadable", deadLetterEventId, attempts: 1);
        consumer.Commit(result);
    }

    private async Task<bool> PublishDeadLetterAsync(
        ConsumeResult<string, byte[]> result,
        EventEnvelope<TPayload>? envelope,
        Exception exception,
        int attempts,
        CancellationToken cancellationToken)
    {
        Guid deadLetterEventId = DeadLetterIdentity.CreateDeterministicEventId(
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            ConsumerName);

        try
        {
            string payloadJson;
            string? eventType = null;
            Guid? originalEventId = null;
            string correlationId;
            Guid? causationId = null;

            if (envelope is not null)
            {
                payloadJson = JsonSerializer.Serialize(envelope, EnvelopeTypeInfo);
                eventType = envelope.EventType;
                originalEventId = envelope.EventId;
                correlationId = envelope.CorrelationId;
                causationId = envelope.EventId;
            }
            else
            {
                payloadJson = result.Message.Value is { Length: > 0 }
                    ? Encoding.UTF8.GetString(result.Message.Value)
                    : string.Empty;
                correlationId = DeadLetterIdentity.CreateDeterministicCorrelationId(
                    result.Topic,
                    result.Partition.Value,
                    result.Offset.Value,
                    ConsumerName);
            }

            DeadLetterPayload payload = new(
                OriginalTopic: result.Topic,
                SourcePartition: result.Partition.Value,
                SourceOffset: result.Offset.Value,
                ConsumerName: ConsumerName,
                OriginalEventType: eventType,
                OriginalEventId: originalEventId,
                FailureReason: exception.Message,
                ErrorCode: ApplicationErrorCodes.DependencyUnavailable,
                AttemptCount: attempts,
                PayloadJson: payloadJson);

            DateTimeOffset now = _timeProvider.GetUtcNow();

            EventEnvelope<DeadLetterPayload> deadLetterEnvelope = new(
                EventId: deadLetterEventId,
                EventType: "sre.deadletter",
                EventVersion: 1,
                OccurredAt: now,
                CorrelationId: correlationId,
                CausationId: causationId,
                Payload: payload);

            string deadLetterJson = JsonSerializer.Serialize(
                deadLetterEnvelope,
                SreAiAgentJsonContext.Default.EventEnvelopeDeadLetterPayload);

            OutboxMessage outboxMessage = new(
                Id: deadLetterEventId,
                Topic: KafkaTopics.DeadLetter,
                EventType: "sre.deadletter",
                EventId: deadLetterEventId,
                PayloadJson: deadLetterJson,
                PartitionKey: result.Message.Key ?? string.Empty,
                CorrelationId: correlationId,
                TraceParent: Activity.Current?.Id,
                OccurredAt: now,
                AttemptCount: 0);

            await _producer.PublishAsync(outboxMessage, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception publishException) when (publishException is not OperationCanceledException)
        {
            LogDeadLetterPublishFailed(_logger, result.Topic, deadLetterEventId, publishException);
            return false;
        }
    }
    [LoggerMessage(
        EventId = 7201,
        Level = LogLevel.Information,
        Message = "Subscribed to {Topic} as group {ConsumerGroup}.")]
    private static partial void LogSubscribed(ILogger logger, string topic, string consumerGroup);

    [LoggerMessage(
        EventId = 7202,
        Level = LogLevel.Error,
        Message = "A message on {Topic} at {Offset} could not be read and was dead-lettered after bounded retries.")]
    private static partial void LogUnreadable(
        ILogger logger, string topic, string offset, Exception exception);

    [LoggerMessage(
        EventId = 7203,
        Level = LogLevel.Error,
        Message = "Handling {EventType} from {Topic} failed; the message will be redelivered.")]
    private static partial void LogHandlerFailed(
        ILogger logger, string topic, string eventType, Exception exception);

    [LoggerMessage(
        EventId = 7204,
        Level = LogLevel.Warning,
        Message = "Could not rewind {Topic} to {Offset}; the partition was likely revoked and its new owner resumes from the committed offset.")]
    private static partial void LogRewindFailed(
        ILogger logger, string topic, string offset, Exception exception);

    [LoggerMessage(
        EventId = 7301,
        Level = LogLevel.Error,
        Message = "Message {EventId} of type {EventType} on {Topic} was dead-lettered after {Attempts} attempts.")]
    private static partial void LogPoisonDeadLettered(
        ILogger logger, string topic, string eventType, Guid eventId, int attempts);

    [LoggerMessage(
        EventId = 7302,
        Level = LogLevel.Error,
        Message = "Could not publish the dead letter for message {EventId} on {Topic}.")]
    private static partial void LogDeadLetterPublishFailed(
        ILogger logger, string topic, Guid eventId, Exception exception);
}
