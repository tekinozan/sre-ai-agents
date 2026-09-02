using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Workflow.Tests;

/// <summary>
/// An <see cref="IInvestigationActivities"/> that records what the sequence did
/// to it.
/// </summary>
/// <remarks>
/// Hand-written rather than mocked. What these tests assert is the order of a
/// sequence and the shape of what it eventually persists, and a mocking
/// framework's received-call assertions express that far less clearly than a
/// list of strings and a captured input.
/// </remarks>
internal sealed class RecordingActivities : IInvestigationActivities
{
    public static readonly IncidentId Incident = IncidentId.New();
    public static readonly ConfigurationSnapshotId Snapshot = ConfigurationSnapshotId.New();

    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls => _calls;

    public IReadOnlyList<SelectedAgent> Agents { get; set; } =
    [
        new SelectedAgent("database-expert", "1.0.0", IsShadow: false),
    ];

    /// <summary>Lets a test control how long each agent takes and what it returns.</summary>
    public Func<RunAgentInput, Task<AgentRunResult>>? OnRunAgent { get; set; }

    public Error? LoadIncidentError { get; set; }

    public PersistReportInput? PersistedReport { get; private set; }

    public CompleteInvestigationInput? Completed { get; private set; }

    public FailInvestigationInput? Failed { get; private set; }

    public bool ApprovalRequired { get; set; }

    public Task<Result<IncidentSnapshot>> LoadIncidentAsync(
        IncidentId incidentId,
        CancellationToken cancellationToken)
    {
        _calls.Add("load");

        return Task.FromResult(LoadIncidentError is null
            ? Result<IncidentSnapshot>.Success(new IncidentSnapshot(
                incidentId,
                "acme",
                "alertmanager",
                "Checkout latency above threshold",
                null,
                "checkout",
                "production",
                IncidentSeverity.High,
                IncidentStatus.Investigating,
                DateTimeOffset.UnixEpoch,
                new Dictionary<string, string>(StringComparer.Ordinal)))
            : Result<IncidentSnapshot>.Failure(LoadIncidentError));
    }

    public Task<Result<ResolvedConfiguration>> ResolveConfigurationAsync(
        ConfigurationSnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        _calls.Add("resolve");

        ConfigurationSnapshot snapshot = new(
            snapshotId,
            "agents-1",
            "prompts-1",
            SreAiAgent.TestKit.PinnedModelFixtures.Scripted(),
            "tools-1",
            "policies-1",
            new Dictionary<string, string>(StringComparer.Ordinal),
            DateTimeOffset.UnixEpoch);

        return Task.FromResult(Result<ResolvedConfiguration>.Success(
            new ResolvedConfiguration(snapshot, "prompts-1", SreAiAgent.TestKit.PinnedModelFixtures.Scripted(), "tools-1", "policies-1")));
    }

    public Task<Result<IncidentClassification>> ClassifyIncidentAsync(
        ClassifyIncidentInput input,
        CancellationToken cancellationToken)
    {
        _calls.Add("classify");

        return Task.FromResult(Result<IncidentClassification>.Success(new IncidentClassification(
            ["database"], ClassificationMethod.Deterministic, Confidence.Certain, "Matched a label rule.")));
    }

    public Task<Result<AgentSelection>> SelectAgentsAsync(
        SelectAgentsInput input,
        CancellationToken cancellationToken)
    {
        _calls.Add("select");

        return Task.FromResult(Result<AgentSelection>.Success(new AgentSelection(Agents, [])));
    }

    public async Task<AgentRunResult> RunAgentAsync(RunAgentInput input, CancellationToken cancellationToken)
    {
        if (OnRunAgent is not null)
        {
            AgentRunResult controlled = await OnRunAgent(input);
            lock (_calls)
            {
                _calls.Add("agent:" + input.Agent.AgentName);
            }

            return controlled;
        }

        lock (_calls)
        {
            _calls.Add("agent:" + input.Agent.AgentName);
        }

        return Succeeded(input.Agent);
    }

    public Task<Result<CorrelationResult>> CorrelateFindingsAsync(
        CorrelateFindingsInput input,
        CancellationToken cancellationToken)
    {
        _calls.Add("correlate");

        return Task.FromResult(Result<CorrelationResult>.Success(new CorrelationResult(
            "Connection pool exhaustion.",
            "database",
            Confidence.From(0.7m),
            [],
            [],
            [])));
    }

    public Task<Result<RecommendationSet>> BuildRecommendationsAsync(
        BuildRecommendationsInput input,
        CancellationToken cancellationToken)
    {
        _calls.Add("recommend");

        return Task.FromResult(Result<RecommendationSet>.Success(new RecommendationSet([])));
    }

    public Task<Result<ApprovalDecision>> DecideApprovalAsync(
        DecideApprovalInput input,
        CancellationToken cancellationToken)
    {
        _calls.Add("approval");

        return Task.FromResult(Result<ApprovalDecision>.Success(
            new ApprovalDecision(ApprovalRequired, null, "V1 tools are read-only.")));
    }

    public Task<Result<PersistedReport>> PersistReportAsync(
        PersistReportInput input,
        CancellationToken cancellationToken)
    {
        _calls.Add("persist");
        PersistedReport = input;

        return Task.FromResult(Result<PersistedReport>.Success(new PersistedReport(RootCauseReportId.New(), 1)));
    }

    public Task<Result> CompleteInvestigationAsync(
        CompleteInvestigationInput input,
        CancellationToken cancellationToken)
    {
        _calls.Add("complete");
        Completed = input;

        return Task.FromResult(Result.Success());
    }

    public Task<Result> FailInvestigationAsync(
        FailInvestigationInput input,
        CancellationToken cancellationToken)
    {
        _calls.Add("fail");
        Failed = input;

        return Task.FromResult(Result.Success());
    }

    public static AgentRunResult Succeeded(SelectedAgent agent)
        => new(
            agent.AgentName,
            agent.AgentVersion,
            AgentRunId.New(),
            AgentRunStatus.Succeeded,
            agent.IsShadow,
            AgentFinding.Empty("Nothing conclusive."),
            [],
            null,
            null);

    public static AgentRunResult FailedWith(SelectedAgent agent, AgentRunStatus status)
        => new(
            agent.AgentName,
            agent.AgentVersion,
            AgentRunId.New(),
            status,
            agent.IsShadow,
            null,
            [],
            ApplicationErrorCodes.ToolTimeout,
            "The tool did not answer in time.");
}
