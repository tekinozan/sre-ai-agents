using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Workflow.Temporal;
using Temporalio.Workflows;

namespace SreAiAgent.Workflow.Tests;

/// <summary>
/// Covers the names the trace pipeline relies on at the durable engine's
/// boundary.
/// </summary>
public sealed class WorkflowTracingTests
{
    [Fact]
    public void TheDurableInvestigationSpanNameFollowsTheWorkflowType()
    {
        // Temporal's interceptor names the span RunWorkflow:<type>. Renaming the
        // workflow without this constant would let the sampler drop every
        // durable investigation at a low ingress ratio, silently.
        string? type = WorkflowDefinition.Create<TemporalInvestigationWorkflow>().Name;

        SreSpanNames.DurableInvestigation.ShouldBe("RunWorkflow:" + type);
    }
}
