using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Errors;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Contracts.Serialization;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Messaging;

/// <summary>
/// Drains the outbox to Kafka (ARCHITECTURE.md 9.4).
/// </summary>
/// <remarks>
/// Safe to run on every replica. Rows are claimed with FOR UPDATE SKIP LOCKED,
/// so several dispatchers share the table without blocking each other and
/// without publishing the same row twice.
///
/// Paced by a PeriodicTimer rather than a Task.Delay loop. Delay allocates a
/// timer per iteration and drifts by the time the body takes; a periodic timer
/// keeps the interval and lets a slow batch simply skip a tick.
///
/// Delivery is at-least-once by construction. A crash between publishing and
/// marking the row dispatched republishes on the next pass, which is why every
/// consumer writes an inbox row in the same transaction as its effect.
/// </remarks>
internal sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IMessageProducer producer,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(
            TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds), timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Keep draining while batches come back full: a burst of alerts
                // should not be paced out one poll interval at a time.
                while (await DrainOnceAsync(stoppingToken).ConfigureAwait(false) == _options.BatchSize)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The dispatcher must not die. Its death is silent, and the
                // symptom is investigations that never start with an incident
                // table that looks perfectly healthy.
                LogCycleFailed(logger, exception);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Claims and publishes one batch. Returns how many were claimed.</summary>
    private async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IOutboxStore store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        DateTimeOffset now = timeProvider.GetUtcNow();

        IReadOnlyList<OutboxMessage> batch = await store
            .ClaimBatchAsync(_options.BatchSize, now, cancellationToken)
            .ConfigureAwait(false);

        if (batch.Count == 0)
        {
            return 0;
        }

        List<Guid> published = [];

        foreach (OutboxMessage message in batch)
        {
            try
            {
                await producer.PublishAsync(message, cancellationToken).ConfigureAwait(false);
                published.Add(message.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await HandleFailureAsync(store, message, exception, now, cancellationToken).ConfigureAwait(false);
            }
        }

        if (published.Count > 0)
        {
            await store.MarkDispatchedAsync(published, now, cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return batch.Count;
    }

    /// <summary>
    /// Schedules another attempt, or dead-letters a message that has run out.
    /// </summary>
    /// <remarks>
    /// Backoff is exponential with jitter. Without jitter, several replicas that
    /// failed on the same broker outage retry in lockstep and hit it again
    /// together the moment it starts recovering.
    /// </remarks>
    private async Task HandleFailureAsync(
        IOutboxStore store,
        OutboxMessage message,
        Exception exception,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Error error = Error.Transient(ApplicationErrorCodes.DependencyUnavailable, exception.Message);

        if (message.AttemptCount + 1 >= _options.MaxAttempts)
        {
            LogDeadLettered(logger, message.EventType, message.Id, exception);

            await PublishDeadLetterAsync(message, error, cancellationToken).ConfigureAwait(false);
            await store.MarkDeadLetteredAsync(message.Id, error, cancellationToken).ConfigureAwait(false);
            return;
        }

        double seconds = Math.Min(Math.Pow(2, message.AttemptCount), 60);
        double jitter = Random.Shared.NextDouble() * seconds * 0.25;

        await store
            .RecordFailureAsync(message.Id, error, now.AddSeconds(seconds + jitter), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Republishes an exhausted message onto the dead-letter topic.
    /// </summary>
    /// <remarks>
    /// The original payload travels with it. A dead letter that says only "this
    /// failed" is a log line; one that carries the message is something an
    /// operator can replay once the cause is fixed.
    ///
    /// A failure to publish the dead letter is swallowed: the row is parked
    /// either way, and throwing here would abandon the rest of the batch over a
    /// message that has already been given up on.
    /// </remarks>
    private async Task PublishDeadLetterAsync(
        OutboxMessage message,
        Error error,
        CancellationToken cancellationToken)
    {
        try
        {
            DeadLetterPayload payload = new(
                message.Topic,
                SourcePartition: 0,
                SourceOffset: 0,
                ConsumerName: "outbox-dispatcher",
                OriginalEventType: message.EventType,
                OriginalEventId: message.EventId,
                FailureReason: error.Message,
                ErrorCode: error.Code,
                AttemptCount: message.AttemptCount + 1,
                PayloadJson: message.PayloadJson);

            EventEnvelope<DeadLetterPayload> envelope = new(
                Guid.CreateVersion7(),
                "sre.deadletter",
                1,
                timeProvider.GetUtcNow(),
                message.CorrelationId,
                message.EventId,
                payload);

            await producer.PublishAsync(
                new OutboxMessage(
                    Guid.CreateVersion7(),
                    KafkaTopics.DeadLetter,
                    "sre.deadletter",
                    envelope.EventId,
                    JsonSerializer.Serialize(
                        envelope, SreAiAgentJsonContext.Default.EventEnvelopeDeadLetterPayload),
                    message.PartitionKey,
                    message.CorrelationId,
                    message.TraceParent,
                    envelope.OccurredAt,
                    AttemptCount: 0),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDeadLetterPublishFailed(logger, message.Id, exception);
        }
    }

    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Error,
        Message = "The outbox dispatch cycle failed.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 7102,
        Level = LogLevel.Error,
        Message = "Outbox message {MessageId} of type {EventType} was dead-lettered.")]
    private static partial void LogDeadLettered(
        ILogger logger, string eventType, Guid messageId, Exception exception);

    [LoggerMessage(
        EventId = 7103,
        Level = LogLevel.Error,
        Message = "Could not publish the dead letter for outbox message {MessageId}.")]
    private static partial void LogDeadLetterPublishFailed(ILogger logger, Guid messageId, Exception exception);
}
