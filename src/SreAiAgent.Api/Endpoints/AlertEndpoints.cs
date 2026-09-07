using System.Text.Json;
using SreAiAgent.Api.Http;
using SreAiAgent.Api.Security;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Contracts.Alerts;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Alerts;

namespace SreAiAgent.Api.Endpoints;

/// <summary>
/// Alert ingress for the provider adapters of ARCHITECTURE.md 10.1.
/// </summary>
/// <remarks>
/// The endpoint normalizes and stages; it does not open the incident. Section
/// 19.1 separates ingestion from the rest so a provider retrying a webhook storm
/// cannot compete with an operator reading a report, and the alert worker is
/// where that work belongs. What this does is turn an untrusted provider payload
/// into a canonical alert at the edge, which is the only point where anyone
/// should still be looking at provider-shaped JSON.
///
/// Answering 202 rather than 201 is honest about that: nothing exists yet except
/// a message that will be handled.
/// </remarks>
public static class AlertEndpoints
{
    /// <summary>Maps the alert ingress endpoint.</summary>
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.MapPost("/v1/alerts/{source}", IngestAsync)
            .RequireAuthorization(SrePolicies.WriteIncidents)
            .WithTags("Alerts")
            .WithSummary("Accepts a provider alert payload and queues it for ingestion.");

        return builder;
    }

    private static async Task<IResult> IngestAsync(
        string source,
        JsonElement payload,
        AlertNormalizer normalizer,
        IEventPublisher publisher,
        IUnitOfWork unitOfWork,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        Result<CreateIncidentRequest> normalized = normalizer.Normalize(source, payload);

        if (normalized.IsFailure)
        {
            return ResultExtensions.Problem(normalized.Error);
        }

        Result<CreateIncidentRequest> bound = AlertNormalizer.BindTenant(normalized.Value, context.User.Tenant());

        if (bound.IsFailure)
        {
            return ResultExtensions.Problem(bound.Error);
        }

        CreateIncidentRequest request = bound.Value;

        publisher.Enqueue(
            KafkaTopics.AlertReceived,
            "sre.alert.received",
            ToCanonical(request),
            CorrelationId.From(context));

        // Staged in the outbox rather than produced directly, so an alert that
        // was accepted cannot be lost if this process dies before the broker
        // acknowledges it (ARCHITECTURE.md 9.4).
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Accepted();
    }

    /// <summary>
    /// Converts back to the canonical shape for the wire.
    /// </summary>
    /// <remarks>
    /// The adapters produce a CanonicalAlert and the normalizer maps it to the
    /// request the use case takes; this reverses the second half so the message
    /// carries the canonical form. Keeping the wire shape canonical rather than
    /// request-shaped means the topic stays readable by anything that understands
    /// alerts, not only by this platform's use case.
    /// </remarks>
    private static CanonicalAlert ToCanonical(CreateIncidentRequest request)
        => new(
            request.Source,
            request.ExternalId,
            request.Fingerprint,
            request.Title,
            request.Description,
            request.Service,
            request.Environment,
            request.Severity,
            request.DetectedAt,
            request.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal),
            request.Annotations ?? new Dictionary<string, string>(StringComparer.Ordinal),
            RawPayloadReference: null)
        {
            IdentityLabelKeys = request.IdentityLabelKeys ?? [],
            IsResolved = request.StartInvestigation == false,
        };
}
