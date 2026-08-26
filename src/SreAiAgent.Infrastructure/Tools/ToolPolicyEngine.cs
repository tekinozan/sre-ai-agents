using System.Text.Json;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Tools;

/// <summary>
/// The deterministic authorization checks a tool call must pass
/// (ARCHITECTURE.md 6.2, 15.1, 15.2, 24.4).
/// </summary>
/// <remarks>
/// Nothing here consults a model, and nothing here is influenced by prompt text.
/// That is the entire design: section 15.4 accepts that injected content can make
/// a model ask for anything, and answers it by making the asking irrelevant. A
/// request that fails any check below never reaches an adapter, whatever the
/// model was persuaded to want.
///
/// The checks are ordered cheapest and most decisive first. Risk ceiling is a
/// single comparison and rejects an entire class of request; schema validation is
/// the most expensive and runs last among the pre-dispatch checks.
/// </remarks>
internal static class ToolPolicyEngine
{
    /// <summary>
    /// Evaluates a request against the descriptor, the execution context and the
    /// pinned policy release.
    /// </summary>
    public static Result Evaluate(
        ToolDescriptor descriptor,
        ToolExecutionRequest request,
        ToolExecutionContext context,
        PolicyRelease policy,
        IReadOnlySet<string> actorScopes,
        IReadOnlySet<string> agentAllowedTools,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        // 1. The agent's own allowlist. An agent may only use tools its
        //    definition grants, even when the tool exists and the actor could
        //    otherwise call it. This is what keeps a database expert out of the
        //    Kubernetes API when a prompt injection suggests it should look there.
        if (!agentAllowedTools.Contains(descriptor.Name))
        {
            return Deny(
                $"Agent '{request.AgentName}' is not allowed to use tool '{descriptor.Name}'.");
        }

        // 2. Risk ceiling. Compared against both the execution context and the
        //    pinned policy: the lower of the two wins, so a permissive context
        //    cannot exceed the release-wide ceiling.
        RiskLevel effectiveCeiling = context.RiskCeiling < policy.RiskCeiling
            ? context.RiskCeiling
            : policy.RiskCeiling;

        if (descriptor.Risk > effectiveCeiling)
        {
            return Deny(
                $"Tool '{descriptor.Name}' has risk {descriptor.Risk} which exceeds the ceiling of {effectiveCeiling}.");
        }

        // 3. Authorization scopes. Every scope the tool declares must be held.
        foreach (string scope in descriptor.RequiredScopes)
        {
            if (!actorScopes.Contains(scope))
            {
                return Deny(
                    $"Actor '{context.Actor}' lacks scope '{scope}' required by tool '{descriptor.Name}'.");
            }
        }

        // 4. Deadline. A call that cannot finish before the workflow deadline is
        //    refused rather than started: beginning work that is guaranteed to be
        //    abandoned costs the backend real load for no possible benefit.
        if (now + descriptor.Timeout > context.Deadline)
        {
            return Deny(
                $"Tool '{descriptor.Name}' cannot complete before the investigation deadline.",
                ApplicationErrorCodes.WorkflowDeadlineExceeded);
        }

        // 5. Argument policy. The schema already said the arguments are
        //    well-formed; this says they are reasonable. A 30-day window is a
        //    valid integer and an indefensible question to ask a metrics backend
        //    during an incident.
        Result arguments = EvaluateArguments(request.Arguments, context, policy);
        if (arguments.IsFailure)
        {
            return arguments;
        }

        return Result.Success();
    }

    private static Result EvaluateArguments(
        JsonElement arguments,
        ToolExecutionContext context,
        PolicyRelease policy)
    {
        if (arguments.ValueKind is not JsonValueKind.Object)
        {
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.ToolArgumentsInvalid, "Tool arguments must be a JSON object."));
        }

        if (arguments.TryGetProperty("window_minutes", out JsonElement window)
            && window.ValueKind is JsonValueKind.Number
            && window.TryGetInt32(out int minutes)
            && minutes > policy.MaxWindowMinutes)
        {
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.ToolPolicyDenied,
                $"Requested window of {minutes.ToString(CultureInfo.InvariantCulture)} minutes exceeds the "
                + $"policy maximum of {policy.MaxWindowMinutes.ToString(CultureInfo.InvariantCulture)}."));
        }

        if (arguments.TryGetProperty("step_seconds", out JsonElement step)
            && step.ValueKind is JsonValueKind.Number
            && step.TryGetInt32(out int seconds)
            && seconds < policy.MinStepSeconds)
        {
            return Result.Failure(Error.Permanent(
                ApplicationErrorCodes.ToolPolicyDenied,
                $"Requested step of {seconds.ToString(CultureInfo.InvariantCulture)}s is finer than the "
                + $"policy minimum of {policy.MinStepSeconds.ToString(CultureInfo.InvariantCulture)}s."));
        }

        // The service allowlist is the tenant isolation boundary. Without it a
        // model could name any service and read another team's, or another
        // customer's, telemetry.
        if (arguments.TryGetProperty("service", out JsonElement service)
            && service.ValueKind is JsonValueKind.String)
        {
            string? requested = service.GetString();
            if (!string.IsNullOrEmpty(requested) && !context.ServiceAllowlist.Contains(requested))
            {
                return Result.Failure(Error.Permanent(
                    ApplicationErrorCodes.ToolPolicyDenied,
                    $"Service '{requested}' is not in the allowlist for this execution."));
            }
        }

        return Result.Success();
    }

    private static Result Deny(string reason, ErrorCode? code = null)
        => Result.Failure(Error.Permanent(code ?? ApplicationErrorCodes.ToolPolicyDenied, reason));
}
