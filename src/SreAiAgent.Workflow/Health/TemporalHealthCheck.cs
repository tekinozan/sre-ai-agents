using Microsoft.Extensions.Diagnostics.HealthChecks;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace SreAiAgent.Workflow.Health;

internal sealed class TemporalHealthCheck(ITemporalClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.Connection.CheckHealthAsync().ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The Temporal frontend reported unhealthy.");
        }
        catch (RpcException exception)
        {
            return HealthCheckResult.Unhealthy(
                "The Temporal frontend did not answer a health check.", exception);
        }
    }
}
