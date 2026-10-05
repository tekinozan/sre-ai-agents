using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Recommendations;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Evals.Harness;

internal sealed class InMemoryInvestigationStores :
    IIncidentRepository,
    IInvestigationStore,
    IConfigurationSnapshotStore,
    IUnitOfWork,
    IEventPublisher
{
    // The eval harness has no broker; published events are counted, not sent.
    public int PublishedCount { get; private set; }

    public void Enqueue(DomainEvent domainEvent, string correlationId, Guid? causationId = null) => PublishedCount++;

    public void Enqueue<TPayload>(string topic, string eventType, TPayload payload, string correlationId, Guid? causationId = null)
        where TPayload : class
        => PublishedCount++;

    private readonly Dictionary<IncidentId, Incident> _incidents = new();
    private readonly List<IncidentTimelineEntry> _timeline = new();
    private readonly Dictionary<ConfigurationSnapshotId, ConfigurationSnapshot> _snapshots = new();

    private readonly List<EvidenceRecord> _evidence = new();
    private readonly Dictionary<AgentRunId, AgentRun> _agentRuns = new();
    private readonly List<Finding> _findings = new();
    private readonly List<RootCauseReport> _reports = new();
    private readonly List<Recommendation> _recommendations = new();

    // IIncidentRepository
    public Task<Incident?> GetAsync(IncidentId incidentId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_incidents.GetValueOrDefault(incidentId));
    }

    public Task<Incident?> FindActiveByFingerprintAsync(
        string tenant,
        string source,
        string fingerprint,
        DateTimeOffset activeSince,
        CancellationToken cancellationToken)
    {
        // Mirrors IncidentRepository (docs/DECISIONS.md D43).
        var found = _incidents.Values
            .Where(i =>
                i.Tenant == tenant &&
                i.Source == source &&
                i.Fingerprint == fingerprint &&
                i.Status != IncidentStatus.Cancelled &&
                (i.Status != IncidentStatus.Resolved || i.LastAlertAt >= activeSince))
            .OrderBy(i => i.Status == IncidentStatus.Resolved ? 1 : 0)
            .ThenByDescending(i => i.LastAlertAt)
            .FirstOrDefault();
        return Task.FromResult(found);
    }

    public void Add(Incident incident) => _incidents[incident.Id] = incident;

    public void AddTimelineEntry(IncidentTimelineEntry entry) => _timeline.Add(entry);

    // IInvestigationStore
    public void AddEvidence(EvidenceRecord evidence) => _evidence.Add(evidence);

    public void AddAgentRun(AgentRun agentRun) => _agentRuns[agentRun.Id] = agentRun;

    public void AddFinding(Finding finding) => _findings.Add(finding);

    public void AddReport(RootCauseReport report) => _reports.Add(report);

    public void AddRecommendations(IReadOnlyList<Recommendation> recommendations) => _recommendations.AddRange(recommendations);

    public Task<AgentRun?> GetAgentRunAsync(AgentRunId agentRunId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_agentRuns.GetValueOrDefault(agentRunId));
    }

    public Task<IReadOnlyList<Finding>> GetFindingsAsync(IncidentId incidentId, CancellationToken cancellationToken)
    {
        var result = _findings.Where(f => f.IncidentId == incidentId).ToList();
        return Task.FromResult<IReadOnlyList<Finding>>(result);
    }

    public Task<IReadOnlyList<EvidenceRecord>> GetUsableEvidenceAsync(
        IncidentId incidentId,
        int limit,
        CancellationToken cancellationToken)
    {
        var result = _evidence
            .Where(e => e.IncidentId == incidentId && e.IsUsableInPrompt)
            .OrderBy(e => e.CollectedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<EvidenceRecord>>(result);
    }

    public Task<int> GetNextReportVersionAsync(IncidentId incidentId, CancellationToken cancellationToken)
    {
        var next = _reports.Count(r => r.IncidentId == incidentId) + 1;
        return Task.FromResult(next);
    }

    // IConfigurationSnapshotStore
    public void Add(ConfigurationSnapshot snapshot) => _snapshots[snapshot.Id] = snapshot;

    public Task<ConfigurationSnapshot?> GetAsync(
        ConfigurationSnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(_snapshots.GetValueOrDefault(snapshotId));
    }

    // IUnitOfWork
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        // Everything is already in memory
        return Task.FromResult(1);
    }

    public Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        return operation(cancellationToken);
    }
}
