using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.UseCases;
using SreAiAgent.Contracts.Alerts;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Contracts.Serialization;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Alerts;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Messaging.Consumers;

/// <summary>
/// Opens an incident for each accepted alert (ARCHITECTURE.md 19.1).
/// </summary>
/// <remarks>
/// Ingestion is separated from the API deployment because the two fail
/// differently. A provider retrying a webhook storm should not compete for
/// threads with an operator reading a report, and an alert backlog should queue
/// in a topic rather than in a request pipeline where it turns into timeouts.
///
/// The work itself goes through the same CreateIncidentHandler the HTTP path
/// uses. Two code paths that both create incidents would eventually disagree
/// about what counts as a duplicate, and deduplication is the one rule that has
/// to hold across every entry point.
/// </remarks>
internal sealed partial class AlertReceivedConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<AlertReceivedConsumer> logger)
    : KafkaConsumerService<CanonicalAlert>(scopeFactory, kafkaOptions, logger)
{
    private readonly DatabaseOptions _database = databaseOptions.Value;

    /// <inheritdoc />
    protected override string Topic => KafkaTopics.AlertReceived;

    /// <inheritdoc />
    protected override string ConsumerName => "alert-worker";

    /// <inheritdoc />
    protected override JsonTypeInfo<EventEnvelope<CanonicalAlert>> EnvelopeTypeInfo
        => SreAiAgentJsonContext.Default.EventEnvelopeCanonicalAlert;

    /// <inheritdoc />
    protected override async Task HandleAsync(
        EventEnvelope<CanonicalAlert> envelope,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(services);

        CreateIncidentHandler handler = services.GetRequiredService<CreateIncidentHandler>();
        CreateIncidentRequest request = AlertNormalizer.ToRequest(envelope.Payload);

        Result<IncidentAcceptedResponse> result = await handler.HandleAsync(
            request,
            TenantFor(envelope.Payload),
            TimeSpan.FromMinutes(_database.IncidentDeduplicationWindowMinutes),
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure && result.Error.IsRetryable)
        {
            // Leaves the offset uncommitted so the broker redelivers. The usual
            // cause is another delivery of the same alert committing right now,
            // and a moment later the retry folds into the incident it opened.
            throw new InvalidOperationException(result.Error.Message);
        }

        if (result.IsFailure)
        {
            LogRejected(logger, envelope.Payload.Source, result.Error.Code.Value, result.Error.Message);
        }
    }

    /// <summary>
    /// The tenant an alert belongs to.
    /// </summary>
    /// <remarks>
    /// Read from a label, because an alert arrives from a monitoring system
    /// rather than from an authenticated principal and there is no token to take
    /// it from. Anything unlabelled lands in the default tenant instead of being
    /// dropped: losing an alert is worse than filing it in the wrong place, and a
    /// misfiled incident is visible to whoever reads it.
    /// </remarks>
    private static string TenantFor(CanonicalAlert alert)
        => alert.Labels.TryGetValue("tenant", out string? tenant) && !string.IsNullOrWhiteSpace(tenant)
            ? tenant
            : "default";

    [LoggerMessage(
        EventId = 7401,
        Level = LogLevel.Warning,
        Message = "An alert from {Source} was rejected: {ErrorCode} {Reason}")]
    private static partial void LogRejected(ILogger logger, string source, string errorCode, string reason);
}
