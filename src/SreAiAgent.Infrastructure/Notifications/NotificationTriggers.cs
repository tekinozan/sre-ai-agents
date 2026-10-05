using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Contracts.Serialization;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Notifications;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Messaging;
using SreAiAgent.Infrastructure.Persistence;

namespace SreAiAgent.Infrastructure.Notifications;

/// <summary>
/// Tells a tenant's channels when an investigation finishes or fails.
/// </summary>
/// <remarks>
/// Reads the completion event every investigation already publishes, under a
/// consumer name of its own so its inbox rows never collide with another
/// consumer's. A channel that does not accept the message fails the handler, and
/// D42's rewind and dead-letter path retries it; channels already told are
/// skipped by their delivery claims.
/// </remarks>
internal sealed class IncidentNotificationConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> kafkaOptions,
    IMessageProducer producer,
    TimeProvider timeProvider,
    ILogger<IncidentNotificationConsumer> logger)
    : KafkaConsumerService<IncidentCompletedPayload>(scopeFactory, kafkaOptions, producer, timeProvider, logger)
{
    private const int SummaryLimit = 600;

    protected override string Topic => KafkaTopics.IncidentCompleted;

    protected override string ConsumerName => "notifier";

    protected override JsonTypeInfo<EventEnvelope<IncidentCompletedPayload>> EnvelopeTypeInfo
        => SreAiAgentJsonContext.Default.EventEnvelopeIncidentCompletedPayload;

    protected override async Task HandleAsync(
        EventEnvelope<IncidentCompletedPayload> envelope,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(services);

        IncidentCompletedPayload payload = envelope.Payload;
        if (payload.Status is not (IncidentStatus.Resolved or IncidentStatus.Failed))
        {
            return;
        }

        SreDbContext context = services.GetRequiredService<SreDbContext>();
        Incident? incident = await context.Incidents
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == payload.IncidentId, cancellationToken)
            .ConfigureAwait(false);
        if (incident is null)
        {
            return;
        }

        string? summary = payload.Status == IncidentStatus.Resolved
            ? await context.RootCauseReports
                .AsNoTracking()
                .Where(report => report.IncidentId == payload.IncidentId)
                .OrderByDescending(report => report.Version)
                .Select(report => report.Summary)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            : incident.FailureReason;

        Result dispatched = await services.GetRequiredService<NotificationDispatcher>()
            .DispatchAsync(incident.Tenant, Describe(envelope.EventId, payload, incident, summary), cancellationToken)
            .ConfigureAwait(false);
        if (dispatched.IsFailure)
        {
            throw new InvalidOperationException(dispatched.Error.Message);
        }
    }

    internal static NotificationMessage Describe(
        Guid eventId,
        IncidentCompletedPayload payload,
        Incident incident,
        string? summary)
    {
        bool resolved = payload.Status == IncidentStatus.Resolved;
        List<KeyValuePair<string, string>> facts =
        [
            new("Service", incident.Service),
            new("Environment", incident.Environment),
            new("Severity", incident.Severity.ToString()),
            new("Outcome", resolved ? payload.Outcome.ToString() : "Failed"),
            new("Incident", incident.Id.ToString()),
        ];
        if (!resolved && payload.ErrorCode is { } code)
        {
            facts.Add(new("Error", code.Value));
        }

        return new NotificationMessage(
            resolved ? NotificationEvents.InvestigationCompleted : NotificationEvents.InvestigationFailed,
            $"incident:{eventId:N}",
            !resolved ? NotificationTone.Critical
                : payload.Outcome == InvestigationOutcome.Complete ? NotificationTone.Good : NotificationTone.Warning,
            (resolved ? "Investigation complete: " : "Investigation failed: ") + incident.Title,
            facts,
            Truncate(summary));
    }

    private static string? Truncate(string? value)
        => value is null || value.Length <= SummaryLimit ? value : string.Concat(value.AsSpan(0, SummaryLimit - 1), "…");
}

/// <summary>
/// Tells a tenant's channels when its model spend crosses 80% and 100% of its
/// budget (D47, D48).
/// </summary>
/// <remarks>
/// Polls rather than reacting to each model call: the gateway's hot path stays a
/// read, and a minute's delay on a budget warning costs nothing. Every replica
/// may run it; the delivery key names the tenant, the period and the threshold,
/// so each channel hears each crossing once per period.
/// </remarks>
internal sealed partial class BudgetThresholdMonitor(
    IServiceScopeFactory scopes,
    IOptions<NotificationOptions> options,
    TimeProvider timeProvider,
    ILogger<BudgetThresholdMonitor> logger) : BackgroundService
{
    private static readonly decimal[] Thresholds = [1.0m, 0.8m];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(options.Value.BudgetCheckIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCheckFailed(logger, exception);
            }

            await Task.Delay(interval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        SreDbContext context = scope.ServiceProvider.GetRequiredService<SreDbContext>();
        ITenantBudgetService budgets = scope.ServiceProvider.GetRequiredService<ITenantBudgetService>();
        NotificationDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();

        List<string> tenants = await context.TenantBudgets
            .AsNoTracking()
            .Select(budget => budget.Tenant)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (string tenant in tenants)
        {
            TenantBudgetStatus? status = await budgets.GetAsync(tenant, cancellationToken).ConfigureAwait(false);
            if (status is null)
            {
                continue;
            }

            decimal crossed = Thresholds.FirstOrDefault(threshold => status.Spent >= status.Budget.Limit * threshold);
            if (crossed == 0m)
            {
                continue;
            }

            Result sent = await dispatcher.DispatchAsync(tenant, Describe(status, crossed), cancellationToken)
                .ConfigureAwait(false);
            if (sent.IsFailure)
            {
                LogThresholdNotSent(logger, tenant);
            }
        }
    }

    internal static NotificationMessage Describe(TenantBudgetStatus status, decimal threshold)
    {
        string percent = (threshold * 100m).ToString("0", CultureInfo.InvariantCulture);
        string Money(decimal value) => $"{value.ToString("0.00", CultureInfo.InvariantCulture)} {status.Budget.Currency}";
        return new NotificationMessage(
            NotificationEvents.BudgetThreshold,
            $"budget:{status.Budget.Tenant}:{status.PeriodStartedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}:{percent}",
            threshold >= 1.0m ? NotificationTone.Critical : NotificationTone.Warning,
            threshold >= 1.0m
                ? "Model budget spent: investigations can no longer call a model"
                : $"Model budget {percent}% used",
            [
                new("Spent", Money(status.Spent)),
                new("Limit", Money(status.Budget.Limit)),
                new("Period", status.Budget.Period.ToString()),
                new("Since", status.PeriodStartedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ],
            null);
    }

    [LoggerMessage(EventId = 8201, Level = LogLevel.Warning, Message = "The budget threshold check failed.")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 8202, Level = LogLevel.Warning,
        Message = "A budget threshold notification for tenant {Tenant} was not delivered; it is retried on the next check.")]
    private static partial void LogThresholdNotSent(ILogger logger, string tenant);
}
