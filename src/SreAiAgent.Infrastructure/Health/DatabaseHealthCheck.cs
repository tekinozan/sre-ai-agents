using Microsoft.Extensions.Diagnostics.HealthChecks;
using SreAiAgent.Infrastructure.Persistence;

namespace SreAiAgent.Infrastructure.Health;

/// <summary>Reports whether the business store is reachable.</summary>
/// <remarks>
/// Readiness only. Every write path in the platform commits to PostgreSQL, so an
/// API that cannot reach it can accept an alert and lose it; refusing traffic
/// lets the load balancer send that alert to a replica that can.
/// </remarks>
internal sealed class DatabaseHealthCheck(SreDbContext database) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await database.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database did not accept a connection.");
        }
        catch (Npgsql.NpgsqlException exception)
        {
            return HealthCheckResult.Unhealthy("The database did not accept a connection.", exception);
        }
    }
}
