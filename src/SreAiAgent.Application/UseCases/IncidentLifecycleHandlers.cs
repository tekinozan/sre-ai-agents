using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.UseCases;

// The operator-driven half of the incident lifecycle: starting an investigation
// that was not started at creation, retrying one that failed, and cancelling one
// that is running.
//
// All three go through the outbox rather than calling the workflow engine
// directly, with one exception: cancellation, which has to reach a workflow that
// is already running and cannot wait for a message to be dispatched.

/// <summary>Starts an investigation for an incident that does not have one.</summary>
public sealed class StartInvestigationHandler(
    IIncidentRepository incidents,
    IConfigurationSnapshotStore snapshots,
    IConfigurationPinner pinner,
    IEventPublisher publisher,
    IUnitOfWork unitOfWork,
    IInvestigationWorkflow workflow,
    TimeProvider timeProvider)
{
    /// <summary>Requests an investigation.</summary>
    public async Task<Result> HandleAsync(
        string tenant,
        IncidentId incidentId,
        CancellationToken cancellationToken)
    {
        Incident? incident = await incidents.GetAsync(incidentId, cancellationToken).ConfigureAwait(false);

        if (incident is null || !string.Equals(incident.Tenant, tenant, StringComparison.Ordinal))
        {
            return Result.Failure(Error.Permanent(ApplicationErrorCodes.IncidentNotFound, "No such incident."));
        }

        if (await workflow.IsRunningAsync(incidentId, cancellationToken).ConfigureAwait(false))
        {
            // Two investigations of one incident would query the same backends,
            // spend the same budget twice and produce two reports that disagree.
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.WorkflowAlreadyRunning,
                "An investigation is already running for this incident."));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        Result<ConfigurationSnapshot> pinned = await pinner
            .PinCurrentAsync(incident.Tenant, now, cancellationToken)
            .ConfigureAwait(false);
        if (pinned.IsFailure)
        {
            return Result.Failure(pinned.Error);
        }

        ConfigurationSnapshot snapshot = pinned.Value;
        snapshots.Add(snapshot);

        incident.StartInvestigation(snapshot.Id, now);

        LifecycleStaging.Stage(incident, incidents, publisher);

        publisher.Enqueue(
            KafkaTopics.InvestigationRequested,
            "sre.incident.investigation.requested",
            new InvestigationRequestedPayload(incident.Id, incident.Tenant, snapshot.Id, Attempt: 1),
            incident.Id.ToString());

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}

/// <summary>Retries an investigation that failed.</summary>
/// <remarks>
/// The original snapshot is reused rather than repinned. A retry is meant to be
/// comparable with the run it replaces, and repinning would silently make it a
/// different experiment: a report that improved after a retry would leave nobody
/// able to say whether the fix was the retry or the release that landed in
/// between. Picking up new configuration is what StartInvestigation is for.
/// </remarks>
public sealed class RetryInvestigationHandler(
    IIncidentRepository incidents,
    IConfigurationSnapshotStore snapshots,
    IConfigurationPinner pinner,
    IEventPublisher publisher,
    IUnitOfWork unitOfWork,
    IInvestigationWorkflow workflow,
    TimeProvider timeProvider)
{
    /// <summary>Requests another attempt.</summary>
    public async Task<Result> HandleAsync(
        string tenant,
        IncidentId incidentId,
        string reason,
        CancellationToken cancellationToken)
    {
        Incident? incident = await incidents.GetAsync(incidentId, cancellationToken).ConfigureAwait(false);

        if (incident is null || !string.Equals(incident.Tenant, tenant, StringComparison.Ordinal))
        {
            return Result.Failure(Error.Permanent(ApplicationErrorCodes.IncidentNotFound, "No such incident."));
        }

        if (await workflow.IsRunningAsync(incidentId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.WorkflowAlreadyRunning,
                "An investigation is already running for this incident."));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        ConfigurationSnapshotId snapshotId;

        if (incident.ConfigurationSnapshotId is { } pinned)
        {
            snapshotId = pinned;
        }
        else
        {
            Result<ConfigurationSnapshot> pinnedSnapshot = await pinner
                .PinCurrentAsync(incident.Tenant, now, cancellationToken)
                .ConfigureAwait(false);
            if (pinnedSnapshot.IsFailure)
            {
                return Result.Failure(pinnedSnapshot.Error);
            }

            snapshots.Add(pinnedSnapshot.Value);
            snapshotId = pinnedSnapshot.Value.Id;
        }

        if (IncidentStatePolicy.IsClosed(incident.Status))
        {
            incident.Reopen("operator", reason, now);
        }

        incident.StartInvestigation(snapshotId, now);

        LifecycleStaging.Stage(incident, incidents, publisher);

        publisher.Enqueue(
            KafkaTopics.InvestigationRequested,
            "sre.incident.investigation.requested",
            new InvestigationRequestedPayload(incident.Id, incident.Tenant, snapshotId, Attempt: 2),
            incident.Id.ToString());

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}

/// <summary>Cancels a running investigation.</summary>
public sealed class CancelInvestigationHandler(
    IIncidentRepository incidents,
    IEventPublisher publisher,
    IUnitOfWork unitOfWork,
    IInvestigationWorkflow workflow,
    TimeProvider timeProvider)
{
    /// <summary>Stops the investigation and closes the incident as cancelled.</summary>
    /// <remarks>
    /// The engine is told first, and the failure to tell it is fatal to the
    /// operation. Marking the incident cancelled while agents keep running would
    /// leave a closed incident that is still spending budget and still writing
    /// evidence, which is the one outcome an operator pressing cancel is trying
    /// to avoid.
    /// </remarks>
    public async Task<Result> HandleAsync(
        string tenant,
        IncidentId incidentId,
        string actor,
        string reason,
        CancellationToken cancellationToken)
    {
        Incident? incident = await incidents.GetAsync(incidentId, cancellationToken).ConfigureAwait(false);

        if (incident is null || !string.Equals(incident.Tenant, tenant, StringComparison.Ordinal))
        {
            return Result.Failure(Error.Permanent(ApplicationErrorCodes.IncidentNotFound, "No such incident."));
        }

        Result cancelled = await workflow.CancelAsync(incidentId, reason, cancellationToken).ConfigureAwait(false);

        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        incident.Cancel(actor, reason, timeProvider.GetUtcNow());

        LifecycleStaging.Stage(incident, incidents, publisher);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}

/// <summary>
/// Turns the events an aggregate raised into timeline entries and outbox rows.
/// </summary>
/// <remarks>
/// Shared by the lifecycle handlers because every one of them has the same
/// obligation: whatever the aggregate announced has to reach both the timeline
/// an operator reads and the topic other systems subscribe to, in the same
/// transaction as the change itself.
/// </remarks>
internal static class LifecycleStaging
{
    public static void Stage(Incident incident, IIncidentRepository incidents, IEventPublisher publisher)
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

    private static string SummaryFor(IncidentDomainEvent domainEvent, Incident incident) => domainEvent switch
    {
        InvestigationStarted => "Investigation started.",
        IncidentCancelled cancelled => $"Cancelled by {cancelled.Actor}.",
        IncidentReopened reopened => $"Reopened by {reopened.Actor}.",
        IncidentResolved => "Incident resolved.",
        IncidentFailed failed => $"Investigation failed with {failed.ErrorCode}.",
        _ => domainEvent.EventType,
    };
}
