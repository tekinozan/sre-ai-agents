using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Workflow.Temporal;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace SreAiAgent.Workflow.Tests;

/// <summary>
/// Covers the payloads Temporal writes into workflow history.
/// </summary>
/// <remarks>
/// Run through Temporal's own converter rather than through JsonSerializer, so
/// what is verified is the path the engine actually takes. A payload that cannot
/// round-trip does not fail at deploy time; it fails on the first activity
/// result of the first real investigation, after the work has already been paid
/// for, and the history it failed to write is the thing that would have let the
/// run resume.
/// </remarks>
public sealed class WorkflowPayloadTests
{
    private readonly DefaultPayloadConverter _converter = new(WorkflowPayloadJson.Options);

    [Fact]
    public void TheWorkflowArgumentRoundTrips()
    {
        InvestigationWorkflowInput input = new(
            IncidentId.New(),
            "acme",
            ConfigurationSnapshotId.New(),
            Attempt: 2,
            DateTimeOffset.UnixEpoch.AddDays(1),
            "corr-42");

        RoundTrip(input).ShouldBe(input);
    }

    [Fact]
    public void ASuccessfulActivityResultKeepsItsValue()
    {
        Result<IncidentSnapshot> result = Result<IncidentSnapshot>.Success(new IncidentSnapshot(
            IncidentId.New(),
            "acme",
            "alertmanager",
            "Checkout latency above threshold",
            null,
            "checkout",
            "production",
            IncidentSeverity.High,
            IncidentStatus.Investigating,
            DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["component"] = "database" }));

        Result<IncidentSnapshot> restored = RoundTrip(result);

        restored.IsSuccess.ShouldBeTrue();

        // Compared field by field rather than with record equality: the
        // synthesized Equals compares the Labels dictionary by reference, so two
        // structurally identical snapshots are never equal and the assertion
        // would fail whether or not serialization worked.
        IncidentSnapshot snapshot = restored.Value;
        snapshot.IncidentId.ShouldBe(result.Value.IncidentId);
        snapshot.Tenant.ShouldBe("acme");
        snapshot.Severity.ShouldBe(IncidentSeverity.High);
        snapshot.Status.ShouldBe(IncidentStatus.Investigating);
        snapshot.DetectedAt.ShouldBe(DateTimeOffset.UnixEpoch);
        snapshot.Labels["component"].ShouldBe("database");
    }

    [Fact]
    public void AFailedActivityResultKeepsItsErrorAndRetryability()
    {
        Result<IncidentSnapshot> result = Result<IncidentSnapshot>.Failure(
            Error.Transient(ApplicationErrorCodes.DependencyUnavailable, "The database was unreachable."));

        Result<IncidentSnapshot> restored = RoundTrip(result);

        restored.IsFailure.ShouldBeTrue();
        restored.Error.Code.ShouldBe(ApplicationErrorCodes.DependencyUnavailable);
        restored.Error.IsRetryable.ShouldBeTrue();
    }

    [Fact]
    public void AUnitResultDistinguishesSuccessFromFailure()
    {
        RoundTrip(Result.Success()).IsSuccess.ShouldBeTrue();
        RoundTrip(Result.Failure(Error.Permanent(ApplicationErrorCodes.WorkflowCancelled, "Stopped.")))
            .Error.Code.ShouldBe(ApplicationErrorCodes.WorkflowCancelled);
    }

    [Fact]
    public void AnAgentResultKeepsItsFindingIntact()
    {
        EvidenceId evidence = EvidenceId.New();

        AgentRunResult run = new(
            "database-expert",
            "1.0.0",
            AgentRunId.New(),
            AgentRunStatus.Succeeded,
            IsShadow: false,
            new AgentFinding(
                "Connection pool exhaustion.",
                [Observation.Verified("Active connections hit the maximum.", [evidence])],
                [new Hypothesis("A slow query held connections open.", [evidence], [], Confidence.From(0.6m))],
                ["Query logs were not retained."],
                ["Check pg_stat_activity."],
                Confidence.From(0.75m)),
            [evidence],
            null,
            null);

        AgentRunResult restored = RoundTrip(run);

        AgentFinding finding = restored.Finding.ShouldNotBeNull();
        finding.Confidence.Value.ShouldBe(0.75m);
        finding.Observations[0].IsVerified.ShouldBeTrue();
        finding.Observations[0].EvidenceIds.ShouldBe([evidence]);
        finding.Hypotheses[0].Confidence.Value.ShouldBe(0.6m);
        restored.EvidenceIds.ShouldBe([evidence]);
    }

    /// <summary>
    /// Every type an activity returns, converted through Temporal's converter.
    /// </summary>
    /// <remarks>
    /// This exists because listing root types is not enough when Result carries a
    /// custom converter: the generator stops at the converter boundary and never
    /// records metadata for the success type inside. Nothing fails at build time,
    /// and the first sign is an activity throwing NoMetadataForType after a real
    /// workflow has already started and spent its budget.
    ///
    /// Naming each return type here makes that a build-and-test failure instead.
    /// </remarks>
    [Fact]
    public void EveryActivityReturnTypeHasSerializationMetadata()
    {
        ConfigurationSnapshot snapshot = new(
            ConfigurationSnapshotId.New(),
            "agents-1", "prompts-1", SreAiAgent.TestKit.PinnedModelFixtures.Scripted(), "tools-1", "policies-1",
            new Dictionary<string, string>(StringComparer.Ordinal),
            DateTimeOffset.UnixEpoch);

        RoundTrip(Result<ResolvedConfiguration>.Success(
            new ResolvedConfiguration(snapshot, "prompts-1", SreAiAgent.TestKit.PinnedModelFixtures.Scripted(), "tools-1", "policies-1")))
            .Value.ModelAssignment.Classifier.DeploymentConfigurationHash.ShouldBe(SreAiAgent.TestKit.PinnedModelFixtures.Scripted().Classifier.DeploymentConfigurationHash);

        RoundTrip(Result<IncidentClassification>.Success(new IncidentClassification(
            ["database"], ClassificationMethod.Deterministic, Confidence.Certain, "Matched a label rule.")))
            .Value.IncidentTypes.ShouldBe(["database"]);

        RoundTrip(Result<AgentSelection>.Success(new AgentSelection(
            [new SelectedAgent("database-expert", "1.0.0", IsShadow: false)],
            [new ExcludedAgent("kubernetes-expert", "1.0.0", "Not eligible.")])))
            .Value.Agents[0].AgentName.ShouldBe("database-expert");

        RoundTrip(Result<CorrelationResult>.Success(new CorrelationResult(
            "Connection pool exhaustion.", "database", Confidence.From(0.7m), [], [], [])))
            .Value.Confidence.Value.ShouldBe(0.7m);

        RoundTrip(Result<RecommendationSet>.Success(new RecommendationSet(
            [new ProposedRecommendation(1, "Raise the pool size", "It is saturated", RiskLevel.ReadOnly, Confidence.From(0.5m), [])])))
            .Value.Recommendations[0].Rank.ShouldBe(1);

        RoundTrip(Result<ApprovalDecision>.Success(
            new ApprovalDecision(IsApprovalRequired: false, null, "V1 tools are read-only.")))
            .Value.IsApprovalRequired.ShouldBeFalse();

        RoundTrip(Result<PersistedReport>.Success(new PersistedReport(RootCauseReportId.New(), 3)))
            .Value.Version.ShouldBe(3);
    }

    private T RoundTrip<T>(T value)
    {
        Payload payload = _converter.ToPayload(value);
        return _converter.ToValue<T>(payload);
    }
}
