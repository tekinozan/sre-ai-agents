namespace SreAiAgent.Api.Endpoints;

/// <summary>
/// Liveness and readiness (ARCHITECTURE.md 12.1, 19.2).
/// </summary>
/// <remarks>
/// The two answer different questions and must not share a check. Liveness asks
/// whether the process should be restarted; readiness asks whether it should
/// receive traffic. A liveness probe that touched the database would restart
/// every API pod during a database failover, turning a recoverable dependency
/// outage into a full outage.
/// </remarks>
public static class HealthEndpoints
{
    /// <summary>Maps the health endpoints.</summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.MapGet("/health/live", static () => Results.Ok(new { status = "live" }))
            .AllowAnonymous()
            .WithTags("Health");

        builder.MapHealthChecks("/health/ready");

        return builder;
    }
}
