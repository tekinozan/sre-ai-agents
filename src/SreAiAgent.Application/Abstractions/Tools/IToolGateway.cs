namespace SreAiAgent.Application.Abstractions.Tools;

/// <summary>
/// The only path from an agent to the outside world (ARCHITECTURE.md 6.2).
/// </summary>
/// <remarks>
/// The model produces a typed request; this gateway decides whether it runs. The
/// full pipeline is registry lookup, schema validation, authorization scope, risk
/// ceiling, argument and time-window policy, secret resolution, rate limit,
/// timeout, typed adapter, result-size limit, redaction, evidence creation and
/// audit. Every step is deterministic, which is the point: prompt-level
/// instructions are advice, and advice is not a security control.
/// </remarks>
public interface IToolGateway
{
    /// <summary>Runs a tool, or explains why it did not.</summary>
    /// <param name="request">What the agent asked for.</param>
    /// <param name="context">Tenant, environment, allowlist, actor, risk ceiling, deadline.</param>
    /// <param name="cancellationToken">Cancels the execution.</param>
    Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken);
}
