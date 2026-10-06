using SreAiAgent.Workflow.Temporal;
using Temporalio.Common;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Worker;

namespace SreAiAgent.Workflow.Tests;

/// <summary>
/// Replays a recorded investigation against the current workflow code.
/// </summary>
/// <remarks>
/// A workflow change that alters the sequence of commands an existing history
/// recorded does not fail at deploy time. It fails on the first in-flight
/// investigation a new worker resumes, as a nondeterminism error that holds the
/// workflow id and blocks a retry. This catches it on the pull request instead,
/// offline: the replayer runs the engine's core in-process and needs no server.
///
/// The fixture was recorded from the compose stack driving one alert to a
/// complete report (production-readiness Phase 5), with the scripted model and
/// the fake observability adapters, so it carries no credential and no real
/// telemetry. A deliberate change to the workflow's command sequence needs a
/// Temporal patch marker and a newly recorded fixture, never an edit to this
/// one.
/// </remarks>
public sealed class WorkflowReplayTests
{
    private const string RecordedWorkflowId = "incident-01a10b17-9f42-7274-8822-47208f09ba9c";

    [Fact]
    public async Task ARecordedInvestigationReplaysDeterministicallyAgainstTheCurrentWorkflow()
    {
        string json = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "investigation-history.json"),
            TestContext.Current.CancellationToken);

        // The same converter and interceptor a production worker runs with: the
        // tracing interceptor reaches the worker through the client, and it
        // takes part in every workflow task.
        WorkflowReplayer replayer = new(
            new WorkflowReplayerOptions
            {
                DataConverter = WorkflowPayloadJson.DataConverter,
                Interceptors = [new TracingInterceptor()],
            }.AddWorkflow<TemporalInvestigationWorkflow>());

        WorkflowReplayResult result = await replayer.ReplayWorkflowAsync(
            WorkflowHistory.FromJson(RecordedWorkflowId, json),
            throwOnReplayFailure: false,
            TestContext.Current.CancellationToken);

        result.ReplayFailure.ShouldBeNull();
    }
}
