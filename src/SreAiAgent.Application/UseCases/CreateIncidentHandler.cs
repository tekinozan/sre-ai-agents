using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.UseCases;

/// <summary>
/// Opens an incident from a normalized alert, or folds the alert into the one
/// already tracking it.
/// </summary>
/// <remarks>
/// The whole operation is one transaction (ARCHITECTURE.md 9.4): the incident,
/// its pinned configuration snapshot, its first timeline entry and the outbox
/// rows that will start the investigation all commit together. Any split here
/// produces a state someone has to reconcile by hand, and the most likely one,
/// a committed incident whose start message was never published, is an
/// investigation that silently never happens.
///
/// Written as a plain class registered directly in DI. There is no mediator:
/// the endpoint calls this method, which is one indirection fewer to step
/// through when something goes wrong at three in the morning
/// (docs/DECISIONS.md D04).
/// </remarks>
public sealed class CreateIncidentHandler(
    IIncidentRepository incidents,
    IConfigurationSnapshotStore snapshots,
    IConfigurationPinner pinner,
    IIdempotencyStore idempotency,
    IEventPublisher publisher,
    IUnitOfWork unitOfWork,
    SreMetrics metrics,
    TimeProvider timeProvider)
{
    /// <summary>
    /// How long a claimed creation key blocks a competing creation.
    /// </summary>
    /// <remarks>
    /// Short on purpose, and bounded from both sides. It has to outlast the
    /// transaction that opens an incident, because that is the whole window the
    /// claim exists to protect: once the incident commits, the fingerprint lookup
    /// finds it and the claim is redundant.
    ///
    /// It must also expire well before an incident could plausibly close, or it
    /// blocks the recurrence it was never meant to block. At five minutes an
    /// incident that resolved quickly left its claim standing, so the next alert
    /// found no active incident, could not take the claim, and was redelivered
    /// until the claim aged out: a legitimate new incident turned into a retry
    /// loop.
    /// </remarks>
    private static readonly TimeSpan ClaimLifetime = TimeSpan.FromSeconds(30);

    /// <summary>Opens or updates an incident.</summary>
    public async Task<Result<IncidentAcceptedResponse>> HandleAsync(
        CreateIncidentRequest request,
        string tenant,
        TimeSpan deduplicationWindow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset now = timeProvider.GetUtcNow();
        string fingerprint = FingerprintOf(request);
        bool startInvestigation = request.StartInvestigation ?? true;

        Incident? existing = await incidents
            .FindActiveByFingerprintAsync(
                tenant, request.Source, fingerprint, now - deduplicationWindow, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return await RecordRecurrenceAsync(existing, request, now, deduplicationWindow, cancellationToken)
                .ConfigureAwait(false);
        }

        // A unique index decides, not a read. Two deliveries of the same alert
        // can both observe "no active incident" a microsecond apart, and only a
        // database constraint can break that tie.
        bool claimed = await idempotency
            .TryClaimAsync($"incident:{tenant}:{request.Source}:{fingerprint}", now + ClaimLifetime, cancellationToken)
            .ConfigureAwait(false);

        if (!claimed)
        {
            return await ResolveConcurrentCreationAsync(
                request, tenant, fingerprint, now, deduplicationWindow, cancellationToken).ConfigureAwait(false);
        }

        return await OpenAsync(request, tenant, fingerprint, startInvestigation, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Derives the identity of an alert when the source did not supply one.
    /// </summary>
    /// <remarks>
    /// Only the label keys the caller nominated take part. A provider sends
    /// labels that change on every restart, such as a pod name, and folding one
    /// of those into identity opens a fresh incident per restart during exactly
    /// the kind of crash loop that most needs to be one incident.
    /// </remarks>
    private static string FingerprintOf(CreateIncidentRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Fingerprint))
        {
            return request.Fingerprint;
        }

        List<KeyValuePair<string, string>> identity = [];
        foreach (string key in request.IdentityLabelKeys ?? [])
        {
            if (request.Labels is not null && request.Labels.TryGetValue(key, out string? value))
            {
                identity.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return IncidentFingerprint.Compute(
            request.Source, request.Service, request.Environment, request.Title, identity);
    }

    /// <summary>Opens a new incident and stages everything the workflow needs.</summary>
    private async Task<Result<IncidentAcceptedResponse>> OpenAsync(
        CreateIncidentRequest request,
        string tenant,
        string fingerprint,
        bool startInvestigation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Pin the assignment first: an activation gate failure must reject the
        // request rather than open an unusable incident, and the aggregate does
        // not need to exist to answer that question.
        Result<ConfigurationSnapshot> pinned = await pinner
            .PinCurrentAsync(tenant, now, cancellationToken)
            .ConfigureAwait(false);
        if (pinned.IsFailure)
        {
            return Result<IncidentAcceptedResponse>.Failure(pinned.Error);
        }

        ConfigurationSnapshot snapshot = pinned.Value;

        Incident incident = new(
            IncidentId.New(),
            new NewIncident(
                tenant,
                request.Source,
                request.ExternalId,
                fingerprint,
                request.Title,
                request.Description,
                request.Service,
                request.Environment,
                request.Severity,
                request.DetectedAt ?? now,
                request.Labels),
            now);

        incidents.Add(incident);
        snapshots.Add(snapshot);

        if (startInvestigation)
        {
            incident.StartInvestigation(snapshot.Id, now);
        }

        // The aggregate already announced the opening, and its event maps to the
        // same topic. Publishing a hand-built IncidentCreated here as well put two
        // identical messages on the topic for one incident, which every consumer
        // then had to deduplicate for no reason.
        StageTimeline(incident, now);

        if (startInvestigation)
        {
            publisher.Enqueue(
                KafkaTopics.InvestigationRequested,
                "sre.incident.investigation.requested",
                new InvestigationRequestedPayload(incident.Id, tenant, snapshot.Id, Attempt: 1),
                incident.Id.ToString());
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        metrics.IncidentStarted(tenant, incident.Environment);

        return Result<IncidentAcceptedResponse>.Success(
            new IncidentAcceptedResponse(incident.Id, incident.Status, IsDuplicate: false));
    }

    /// <summary>
    /// Records that the source reported an alert's condition as resolved.
    /// </summary>
    /// <remarks>
    /// Never opens an incident. A resolved delivery for an identity with nothing
    /// to fold into used to open one without an investigation, and because that
    /// incident then counted as active, the real alerts that followed were only
    /// counted and never investigated (BUG-001, docs/DECISIONS.md D43). When an
    /// incident is found the delivery is a timeline entry and nothing else: the
    /// platform's own lifecycle is decided by the investigation, not by the
    /// source.
    /// </remarks>
    public async Task<Result> RecordResolutionAsync(
        CreateIncidentRequest request,
        string tenant,
        TimeSpan deduplicationWindow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset now = timeProvider.GetUtcNow();
        Incident? incident = await incidents
            .FindActiveByFingerprintAsync(
                tenant, request.Source, FingerprintOf(request), now - deduplicationWindow, cancellationToken)
            .ConfigureAwait(false);

        if (incident is null)
        {
            return Result.Success();
        }

        incidents.AddTimelineEntry(IncidentTimelineEntry.Progress(
            incident.Id,
            "alert.resolved",
            $"{incident.Source} reported the alert resolved.",
            now));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>Folds a repeat alert into the incident already tracking it.</summary>
    /// <remarks>
    /// The caller gets the same successful response either way. A duplicate is
    /// not a client error: an alert source repeating itself while a condition
    /// persists is the normal case, and answering 409 would train every
    /// integration to treat a healthy platform as broken.
    ///
    /// Folding is not always only counting. An incident that was never
    /// investigated, or whose investigation failed before the alert went quiet
    /// for a whole window, would otherwise absorb every later alert without
    /// anyone looking at it; for those the recurrence starts the investigation
    /// (D43). The snapshot is pinned before the aggregate changes, for the same
    /// reason as on open: a pin failure rejects the delivery rather than leaving
    /// a counted alert that a retry would count again.
    /// </remarks>
    private async Task<Result<IncidentAcceptedResponse>> RecordRecurrenceAsync(
        Incident incident,
        CreateIncidentRequest request,
        DateTimeOffset now,
        TimeSpan deduplicationWindow,
        CancellationToken cancellationToken)
    {
        bool investigate = (request.StartInvestigation ?? true)
            && NeedsInvestigation(incident, now - deduplicationWindow);

        ConfigurationSnapshot? snapshot = null;
        if (investigate)
        {
            Result<ConfigurationSnapshot> pinned = await pinner
                .PinCurrentAsync(incident.Tenant, now, cancellationToken)
                .ConfigureAwait(false);
            if (pinned.IsFailure)
            {
                return Result<IncidentAcceptedResponse>.Failure(pinned.Error);
            }

            snapshot = pinned.Value;
        }

        bool wasFailed = incident.Status == IncidentStatus.Failed;
        incident.RecordAlertRecurrence(now);

        if (request.Severity > incident.Severity)
        {
            incident.EscalateSeverity(request.Severity, now);
        }

        if (snapshot is not null)
        {
            snapshots.Add(snapshot);
            incident.StartInvestigation(snapshot.Id, now);
        }

        StageTimeline(incident, now);

        if (snapshot is not null)
        {
            publisher.Enqueue(
                KafkaTopics.InvestigationRequested,
                "sre.incident.investigation.requested",
                new InvestigationRequestedPayload(incident.Id, incident.Tenant, snapshot.Id, Attempt: wasFailed ? 2 : 1),
                incident.Id.ToString());
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<IncidentAcceptedResponse>.Success(
            new IncidentAcceptedResponse(incident.Id, incident.Status, IsDuplicate: true));
    }

    /// <summary>
    /// Whether a recurrence should start an investigation on the incident it
    /// folds into.
    /// </summary>
    /// <remarks>
    /// Every other state is either investigating, waiting on a human, or a
    /// resolved incident inside its cooldown, where the alert repeating is the
    /// outage the report already explains. A failed incident is restarted only
    /// once its alert had gone quiet for a window, so a pipeline that fails on
    /// every attempt costs one investigation per window rather than one per
    /// alert.
    /// </remarks>
    private static bool NeedsInvestigation(Incident incident, DateTimeOffset quietSince)
        => incident.Status switch
        {
            IncidentStatus.Open => incident.ConfigurationSnapshotId is null,
            IncidentStatus.Failed => incident.LastAlertAt < quietSince,
            _ => false,
        };

    /// <summary>
    /// Handles losing the race to claim the creation key.
    /// </summary>
    /// <remarks>
    /// The winner is committing right now, so a second lookup usually finds the
    /// incident and the caller gets the duplicate answer it would have got a
    /// moment later. When it does not, the answer is a retryable failure rather
    /// than a second incident: two incidents for one alert split the evidence
    /// and the investigation across both, which is worse than asking the caller
    /// to try again.
    /// </remarks>
    private async Task<Result<IncidentAcceptedResponse>> ResolveConcurrentCreationAsync(
        CreateIncidentRequest request,
        string tenant,
        string fingerprint,
        DateTimeOffset now,
        TimeSpan deduplicationWindow,
        CancellationToken cancellationToken)
    {
        Incident? winner = await incidents
            .FindActiveByFingerprintAsync(
                tenant, request.Source, fingerprint, now - deduplicationWindow, cancellationToken)
            .ConfigureAwait(false);

        return winner is not null
            ? await RecordRecurrenceAsync(winner, request, now, deduplicationWindow, cancellationToken).ConfigureAwait(false)
            : Result<IncidentAcceptedResponse>.Failure(Error.Transient(
                ApplicationErrorCodes.IncidentInvalidState,
                "Another request is already opening this incident. Retry shortly."));
    }

    /// <summary>
    /// Turns the events the aggregate raised into timeline entries and outbox
    /// rows.
    /// </summary>
    /// <remarks>
    /// Drained here rather than inside the aggregate. A domain object that
    /// published its own events would need the outbox, a clock and a correlation
    /// id, and would stop being a plain object that can be unit tested without
    /// any of them.
    /// </remarks>
    private void StageTimeline(Incident incident, DateTimeOffset now)
    {
        foreach (DomainEvent domainEvent in incident.DequeueEvents())
        {
            if (domainEvent is not IncidentDomainEvent incidentEvent)
            {
                continue;
            }

            incidents.AddTimelineEntry(
                IncidentTimelineEntry.FromDomainEvent(incidentEvent, SummaryFor(incidentEvent, incident)));

            publisher.Enqueue(incidentEvent, incident.Id.ToString());
        }
    }

    /// <summary>
    /// Operator-facing text for the timeline.
    /// </summary>
    /// <remarks>
    /// Deliberately built from fields the platform already holds rather than
    /// from anything a provider sent. The timeline is returned by
    /// <c>GET /v1/incidents/{id}/timeline</c>, and echoing provider text into it
    /// would put unredacted third-party content on a response.
    /// </remarks>
    private static string SummaryFor(IncidentDomainEvent domainEvent, Incident incident) => domainEvent switch
    {
        IncidentOpened => $"Incident opened from {incident.Source} for {incident.Service} in {incident.Environment}.",
        IncidentAlertRecurred => $"The same alert fired again. Alert count is now {incident.AlertCount}.",
        InvestigationStarted => "Investigation started.",
        _ => domainEvent.EventType,
    };
}
