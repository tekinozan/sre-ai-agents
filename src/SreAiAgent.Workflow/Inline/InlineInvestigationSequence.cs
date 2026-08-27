using Microsoft.Extensions.DependencyInjection;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Workflow.Inline;

/// <summary>
/// Schedules the fan-out on the thread pool, for the in-process executor.
/// </summary>
/// <remarks>
/// Parallelism is bounded. Each agent holds a model connection and a database
/// connection, and an unbounded fan-out on a wide incident is a self-inflicted
/// outage of the very backends being investigated.
/// </remarks>
/// <param name="maxDegreeOfParallelism">Concurrent agent runs, from AgentRuntimeOptions.</param>
/// <param name="scopeFactory">Supplies one dependency scope per agent run.</param>
internal sealed class InlineInvestigationSequence(
    int maxDegreeOfParallelism,
    IServiceScopeFactory scopeFactory) : InvestigationSequence
{
    /// <inheritdoc />
    /// <remarks>
    /// Each agent gets its own scope, and therefore its own DbContext. A context
    /// is not thread-safe and throws the moment a second concurrent query starts
    /// on it, so sharing the investigation's scope across a fan-out fails as soon
    /// as two experts are selected: the exact case the fan-out exists for.
    /// </remarks>
    protected override async Task<AgentRunResult> RunOneAgentAsync(
        IInvestigationActivities activities,
        RunAgentInput input,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<IInvestigationActivities>()
            .RunAgentAsync(input, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override Task ForEachAgentAsync(
        int count,
        Func<int, CancellationToken, Task> body,
        CancellationToken cancellationToken)
        => Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = cancellationToken,
            },
            (index, token) => new ValueTask(body(index, token)));

    /// <inheritdoc />
    /// <remarks>
    /// Refuses rather than simulates. A signal needs a durable, addressable
    /// instance to be delivered to, and pretending to wait for one would produce
    /// something with the same shape as an approval and none of its meaning. V1
    /// never reaches this branch because every tool is read-only; a run that does
    /// reach it has a configuration problem worth failing loudly on.
    /// </remarks>
    protected override Task<Result> WaitForApprovalAsync(
        ApprovalDecision decision,
        InvestigationWorkflowInput input,
        CancellationToken cancellationToken)
        => Task.FromResult(Result.Failure(Error.Permanent(
            ApplicationErrorCodes.ConfigInvalid,
            "An approval is required, which the inline executor cannot wait for.")));
}
