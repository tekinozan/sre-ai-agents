using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Contracts.Serialization;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Messaging.Consumers;

/// <summary>
/// Starts an investigation for each request published by the outbox.
/// </summary>
/// <remarks>
/// The message is a trigger, not the work. This consumer does nothing except ask
/// the workflow engine to begin, which keeps the Kafka consumer loop free while
/// an investigation runs for minutes: holding a partition open for the length of
/// an LLM fan-out would stall every other incident behind it and eventually trip
/// the broker's poll interval.
///
/// Starting is idempotent by workflow id, so a redelivery joins the run that is
/// already in progress rather than launching a competing one. That is the second
/// line of defence; the inbox row written by the base class is the first.
/// </remarks>
internal sealed partial class InvestigationRequestedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<WorkflowOptions> workflowOptions,
    TimeProvider timeProvider,
    ILogger<InvestigationRequestedConsumer> logger)
    : KafkaConsumerService<InvestigationRequestedPayload>(scopeFactory, kafkaOptions, logger)
{
    private readonly WorkflowOptions _workflow = workflowOptions.Value;

    /// <inheritdoc />
    protected override string Topic => KafkaTopics.InvestigationRequested;

    /// <inheritdoc />
    protected override string ConsumerName => "investigation-worker";

    /// <inheritdoc />
    protected override JsonTypeInfo<EventEnvelope<InvestigationRequestedPayload>> EnvelopeTypeInfo
        => SreAiAgentJsonContext.Default.EventEnvelopeInvestigationRequestedPayload;

    /// <inheritdoc />
    protected override async Task HandleAsync(
        EventEnvelope<InvestigationRequestedPayload> envelope,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(services);

        InvestigationRequestedPayload payload = envelope.Payload;
        IInvestigationWorkflow workflow = services.GetRequiredService<IInvestigationWorkflow>();

        Result started = await workflow.StartAsync(
            new InvestigationWorkflowInput(
                payload.IncidentId,
                payload.Tenant,
                payload.ConfigurationSnapshotId,
                payload.Attempt,

                // The deadline is absolute and computed once here, so every
                // activity inside the run measures against the same instant
                // rather than each recomputing a relative window
                // (ARCHITECTURE.md 11.3).
                timeProvider.GetUtcNow().AddSeconds(_workflow.DeadlineSeconds),
                envelope.CorrelationId),
            cancellationToken).ConfigureAwait(false);

        if (started.IsFailure && started.Error.IsRetryable)
        {
            // Throwing leaves the offset uncommitted, so the broker redelivers.
            // A permanent failure is not rethrown: retrying it would stall the
            // partition forever on a message that cannot succeed.
            throw new InvalidOperationException(started.Error.Message);
        }

        if (started.IsFailure)
        {
            LogNotStarted(logger, payload.IncidentId.ToString(), started.Error.Code.Value, started.Error.Message);
        }
    }

    [LoggerMessage(
        EventId = 7301,
        Level = LogLevel.Warning,
        Message = "The investigation for incident {IncidentId} was not started: {ErrorCode} {Reason}")]
    private static partial void LogNotStarted(
        ILogger logger, string incidentId, string errorCode, string reason);
}
