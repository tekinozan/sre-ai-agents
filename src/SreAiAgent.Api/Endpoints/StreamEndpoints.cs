using System.Security.Claims;
using System.Text.Json;
using SreAiAgent.Api.Security;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Contracts.Serialization;
using SreAiAgent.Contracts.Streaming;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Api.Endpoints;

/// <summary>
/// The investigation progress stream of ARCHITECTURE.md 12.1.
/// </summary>
/// <remarks>
/// The stream tails the incident timeline in PostgreSQL rather than subscribing
/// to anything in process. The events are written by the investigation worker
/// and this endpoint is served by the API, which are separate deployments
/// (ARCHITECTURE.md 19.1), so an in-memory broadcast would work only while both
/// happened to be the same process and would fail quietly the moment they were
/// not.
///
/// Polling on a timer rather than LISTEN/NOTIFY. Notify would remove the latency
/// floor but costs a dedicated connection per subscriber, and an incident stream
/// is watched by a handful of operators for a few minutes, not by thousands of
/// clients continuously.
///
/// What is published is what the platform did, never what a model thought. The
/// event type has nowhere to put a prompt, a token count or a reasoning trace.
/// </remarks>
public static class StreamEndpoints
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private const int BatchSize = 50;

    /// <summary>Maps the server-sent event endpoint.</summary>
    public static IEndpointRouteBuilder MapStreamEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.MapGet("/v1/incidents/{incidentId:guid}/events", StreamAsync)
            .RequireAuthorization(SrePolicies.ReadIncidents)
            .WithTags("Incidents");

        return builder;
    }

    private static async Task StreamAsync(
        HttpContext context,
        Guid incidentId,
        ClaimsPrincipal principal,
        IIncidentQueries queries,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        string tenant = principal.Tenant();
        IncidentId incident = IncidentId.From(incidentId);

        IncidentStatus? status = await queries
            .GetStatusAsync(tenant, incident, cancellationToken)
            .ConfigureAwait(false);

        if (status is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        // Proxies that buffer a response defeat the entire point of streaming it.
        context.Response.Headers["X-Accel-Buffering"] = "no";

        int delivered = ResumeFrom(context.Request.Headers["Last-Event-ID"]);

        using PeriodicTimer timer = new(PollInterval, timeProvider);

        do
        {
            IReadOnlyList<IncidentTimelineEntryResponse> entries = await queries
                .GetStreamEventsAsync(tenant, incident, delivered, BatchSize, cancellationToken)
                .ConfigureAwait(false);

            foreach (IncidentTimelineEntryResponse entry in entries)
            {
                delivered++;
                await WriteAsync(context, entry, incident, delivered, cancellationToken).ConfigureAwait(false);
            }

            if (entries.Count > 0)
            {
                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            status = await queries.GetStatusAsync(tenant, incident, cancellationToken).ConfigureAwait(false);

            // Closed and fully drained. Closing while entries remain would drop
            // the completion event the client is waiting for.
            if (status is null || (IncidentStatePolicy.IsClosed(status.Value) && entries.Count == 0))
            {
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task WriteAsync(
        HttpContext context,
        IncidentTimelineEntryResponse entry,
        IncidentId incidentId,
        int sequence,
        CancellationToken cancellationToken)
    {
        InvestigationStreamEvent payload = new(
            StreamEventTypeFor(entry.EventType),
            incidentId,
            entry.OccurredAt,
            sequence,
            Message: entry.Summary);

        string json = JsonSerializer.Serialize(payload, SreAiAgentJsonContext.Default.InvestigationStreamEvent);

        await context.Response
            .WriteAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"id: {sequence}\nevent: {payload.EventType}\ndata: {json}\n\n"),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a stored timeline event type onto the published stream vocabulary.
    /// </summary>
    /// <remarks>
    /// An allowlist, not a pass-through. The set of names a client may observe is
    /// part of the contract in section 12.1, and forwarding whatever the timeline
    /// happens to contain would turn every new internal event into an
    /// unannounced API change.
    /// </remarks>
    private static string StreamEventTypeFor(string eventType) => eventType switch
    {
        "sre.incident.investigation.started" => InvestigationStreamEventTypes.IncidentStatus,
        "sre.agent.started" => InvestigationStreamEventTypes.AgentStarted,
        "sre.agent.completed" => InvestigationStreamEventTypes.AgentCompleted,
        "sre.tool.started" => InvestigationStreamEventTypes.ToolStarted,
        "sre.tool.completed" => InvestigationStreamEventTypes.ToolCompleted,
        "sre.correlation.completed" => InvestigationStreamEventTypes.CorrelationCompleted,
        "sre.incident.approval.required" => InvestigationStreamEventTypes.ApprovalRequired,
        "sre.incident.resolved" or "sre.incident.failed" or "sre.incident.cancelled"
            => InvestigationStreamEventTypes.IncidentCompleted,
        _ => InvestigationStreamEventTypes.IncidentStatus,
    };

    private static int ResumeFrom(string? lastEventId)
        => int.TryParse(lastEventId, CultureInfo.InvariantCulture, out int delivered) && delivered > 0
            ? delivered
            : 0;
}
