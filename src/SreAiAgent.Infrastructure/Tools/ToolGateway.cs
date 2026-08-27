using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Microsoft.Extensions.Logging;
using SreAiAgent.Application.Abstractions.Security;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Audit;

namespace SreAiAgent.Infrastructure.Tools;

/// <summary>
/// The only path from an agent to a real system (ARCHITECTURE.md 6.2).
/// </summary>
/// <remarks>
/// Implements the pipeline of section 6.2 in order: registry lookup, schema
/// validation, authorization, risk ceiling, argument policy, rate limit, timeout,
/// typed adapter, result cap, redaction, evidence, audit. Every stage is
/// deterministic and none of them consults a model.
///
/// A refusal is a normal return value rather than an exception. A denial is
/// information the agent should reason about and the report should record;
/// throwing would push it into a catch block where that context is routinely
/// lost.
/// </remarks>
internal sealed partial class ToolGateway : IToolGateway
{
    private readonly IReadOnlyToolRegistry _tools;
    private readonly ToolRateLimiter _rateLimiter;
    private readonly IRedactor _redactor;
    private readonly ToolAdapterCatalog _adapters;
    private readonly SreMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ToolGateway> _logger;

    private readonly IToolExecutionContextResolver _contextResolver;
    private readonly IAuditSink _audit;

    public ToolGateway(
        IReadOnlyToolRegistry tools,
        ToolRateLimiter rateLimiter,
        IRedactor redactor,
        ToolAdapterCatalog adapters,
        IToolExecutionContextResolver contextResolver,
        IAuditSink audit,
        SreMetrics metrics,
        TimeProvider timeProvider,
        ILogger<ToolGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        _adapters = adapters;
        _tools = tools;
        _rateLimiter = rateLimiter;
        _redactor = redactor;
        _contextResolver = contextResolver;
        _audit = audit;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        ToolCallId toolCallId = ToolCallId.New();
        long start = _timeProvider.GetTimestamp();

        using Activity? activity = SreActivitySources.Tool.StartActivity(
            SreSpanNames.ToolExecute, ActivityKind.Client);

        activity
            .SetSafeTag(TraceAttributes.ToolName, request.ToolName)
            .SetSafeTag(TraceAttributes.ToolVersion, request.ToolVersion)
            .SetSafeTag(TraceAttributes.IncidentId, request.IncidentId.ToString())
            .SetSafeTag(TraceAttributes.AgentName, request.AgentName)
            .SetSafeTag(TraceAttributes.Tenant, context.Tenant)
            .SetSafeTag(TraceAttributes.Environment, context.Environment);

        // Stage 1: registry lookup. Only the read-only registry is consulted, and
        // no write registry is registered in V1, so a write tool is unreachable
        // rather than merely refused.
        ToolDescriptor? descriptor = await _tools
            .FindAsync(request.ToolName, request.ToolVersion, cancellationToken)
            .ConfigureAwait(false);

        if (descriptor is null)
        {
            return Refuse(
                request, activity, toolCallId, start,
                Error.Permanent(
                    ApplicationErrorCodes.ToolNotFound,
                    $"Tool '{request.ToolName}:{request.ToolVersion}' is not in the pinned tool release."),
                countAsDenial: false);
        }

        activity.SetSafeTag(TraceAttributes.ToolRisk, descriptor.Risk.ToString());

        // Stage 2: schema validation. Arguments are model output and untrusted
        // until this passes.
        Result schema = ValidateArguments(descriptor, request.Arguments);
        if (schema.IsFailure)
        {
            return Refuse(request, activity, toolCallId, start, schema.Error, countAsDenial: false);
        }

        // Stages 3 to 5: authorization, risk ceiling, argument policy.
        ToolAuthorization authorization = await _contextResolver
            .ResolveAsync(request, context, cancellationToken)
            .ConfigureAwait(false);

        Result policy = ToolPolicyEngine.Evaluate(
            descriptor,
            request,
            context,
            authorization.Policy,
            authorization.ActorScopes,
            authorization.AgentAllowedTools,
            _timeProvider.GetUtcNow());

        if (policy.IsFailure)
        {
            LogDenied(_logger, request.ToolName, request.AgentName, policy.Error.Message);
            return Refuse(request, activity, toolCallId, start, policy.Error, countAsDenial: true);
        }

        // Stage 6: rate limit.
        Result rateLimit = _rateLimiter.TryAcquire(
            context.Tenant, request.IncidentId, descriptor.Name, authorization.Policy.RateLimits);

        if (rateLimit.IsFailure)
        {
            return Refuse(request, activity, toolCallId, start, rateLimit.Error, countAsDenial: true);
        }

        if (!_adapters.TryGet(descriptor.Name, out IToolAdapter adapter))
        {
            // Registered in the release but with no adapter compiled in. Caught by
            // startup validation normally; treated as configuration rather than a
            // tool failure so it is not retried.
            return Refuse(
                request, activity, toolCallId, start,
                Error.Permanent(
                    ApplicationErrorCodes.ToolNotFound,
                    $"No adapter is implemented for tool '{descriptor.Name}'."),
                countAsDenial: false);
        }

        // Stage 7: timeout. Enforced by the gateway rather than trusted to the
        // adapter, so one misbehaving backend client cannot consume the agent's
        // whole budget.
        using CancellationTokenSource timeout = new(descriptor.Timeout, _timeProvider);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        ToolAdapterResult adapterResult;
        try
        {
            // Stage 8: typed adapter.
            adapterResult = await adapter
                .ExecuteAsync(request.Arguments, context, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Refuse(
                request, activity, toolCallId, start,
                Error.Transient(
                    ApplicationErrorCodes.ToolTimeout,
                    $"Tool '{descriptor.Name}' exceeded its {descriptor.Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s timeout."),
                countAsDenial: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAdapterFailure(_logger, descriptor.Name, descriptor.Provider, exception);
            return Refuse(
                request, activity, toolCallId, start,
                Error.Transient(
                    ApplicationErrorCodes.ToolExecutionFailed,
                    $"Tool '{descriptor.Name}' failed against provider '{descriptor.Provider}'."),
                countAsDenial: false);
        }

        // Stage 9: result-size cap, applied before redaction so a hostile payload
        // cannot make the redactor do unbounded work.
        (string capped, bool truncated) = Cap(adapterResult.ContentJson, descriptor.MaxResultBytes);

        // Stage 10: redaction. Nothing reaches evidence or a prompt before this.
        RedactionResult redaction = _redactor.Redact(capped);

        TimeSpan duration = _timeProvider.GetElapsedTime(start);
        _metrics.ToolCall(descriptor.Name, descriptor.Version, "succeeded", duration);
        activity.SetSafeTag(TraceAttributes.ResultStatus, "succeeded");

        // Stages 11 and 12: evidence and audit. The evidence record is built here
        // and persisted by the caller inside its unit of work, so it commits with
        // the rest of the investigation rather than in a separate transaction
        // that could survive a rollback.
        NewEvidence evidence = new()
        {
            IncidentId = request.IncidentId,
            AgentRunId = request.AgentRunId,
            ToolCallId = toolCallId,
            SourceType = adapterResult.SourceType,
            Provider = adapterResult.Provider,
            ToolName = descriptor.Name,
            ToolVersion = descriptor.Version,
            Summary = adapterResult.Summary,
            ContentJson = redaction.Content,
            ContentHash = Hash(redaction.Content),
            RedactionStatus = redaction.IsClean ? RedactionStatus.Clean : RedactionStatus.Redacted,
            RedactedValueCount = redaction.ReplacedValueCount,
            IsTruncated = truncated,
            ObservationWindowStart = adapterResult.WindowStart,
            ObservationWindowEnd = adapterResult.WindowEnd,
            CollectedAt = _timeProvider.GetUtcNow(),
        };

        EvidenceRecord record = EvidenceRecord.Create(evidence);
        await _contextResolver.RecordEvidenceAsync(record, cancellationToken).ConfigureAwait(false);

        // Stage 13: audit. Staged in the same unit of work as the evidence, so
        // the row describing the call and the row holding its result commit
        // together or not at all.
        Audit(request, toolCallId, descriptor.Version, "succeeded", error: null, duration);

        return ToolExecutionResult.Success(
            toolCallId,
            record.Id,
            adapterResult.Summary,
            redaction.Content,
            duration,
            truncated,
            redaction.ReplacedValueCount);
    }

    private static Result ValidateArguments(ToolDescriptor descriptor, JsonElement arguments)
    {
        JsonSchema schema = JsonSchema.FromText(descriptor.Definition.ParameterSchema.GetRawText());
        EvaluationResults results = schema.Evaluate(
            arguments, new EvaluationOptions { OutputFormat = OutputFormat.List });

        return results.IsValid
            ? Result.Success()
            : Result.Failure(Error.Permanent(
                ApplicationErrorCodes.ToolArgumentsInvalid,
                $"Arguments for '{descriptor.Name}' did not satisfy its schema."));
    }

    /// <summary>
    /// Truncates on a UTF-8 byte budget rather than a character count, because
    /// the cap exists to bound what travels to a model and is stored, and both
    /// are measured in bytes.
    /// </summary>
    private static (string Content, bool Truncated) Cap(string content, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(content) <= maxBytes)
        {
            return (content, false);
        }

        // Conservative: at most four bytes per character, so slicing here can
        // never split a surrogate pair or a multi-byte sequence.
        int characters = Math.Min(content.Length, maxBytes / 4);
        return (content[..characters], true);
    }

    private static string Hash(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private ToolExecutionResult Refuse(
        ToolExecutionRequest request,
        Activity? activity,
        ToolCallId toolCallId,
        long start,
        Error error,
        bool countAsDenial)
    {
        TimeSpan duration = _timeProvider.GetElapsedTime(start);

        activity
            .SetSafeTag(TraceAttributes.ResultStatus, countAsDenial ? "denied" : "failed")
            .SetSafeTag(TraceAttributes.ErrorCode, error.Code.Value);
        activity?.SetStatus(ActivityStatusCode.Error);

        _metrics.ToolCall(request.ToolName, request.ToolVersion, countAsDenial ? "denied" : "failed", duration);

        if (countAsDenial)
        {
            // Denials are counted separately from failures. A sustained rise means
            // an agent keeps asking for something it may not have, which is worth
            // an alert even though nothing is broken.
            _metrics.ToolPolicyDenied(request.ToolName, error.Code.Value);
        }

        // Refusals are audited exactly like successes. "The agent asked for the
        // Kubernetes API and was refused" is the most useful line in a review of
        // the platform's own behaviour, and a trail holding only successes cannot
        // produce it.
        Audit(request, toolCallId, request.ToolVersion, countAsDenial ? "denied" : "failed", error, duration);

        return ToolExecutionResult.Failure(toolCallId, error, duration);
    }

    private void Audit(
        ToolExecutionRequest request,
        ToolCallId toolCallId,
        string toolVersion,
        string outcome,
        Error? error,
        TimeSpan duration)
        => _audit.RecordToolCall(new ToolCallAudit(
            toolCallId,
            request.IncidentId,
            request.AgentRunId,
            request.ToolName,
            toolVersion,
            request.AgentName,
            outcome,
            error?.Code,
            request.Arguments.GetRawText(),
            duration,
            _timeProvider.GetUtcNow()));

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Tool {ToolName} denied for agent {AgentName}: {Reason}")]
    private static partial void LogDenied(ILogger logger, string toolName, string agentName, string reason);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Tool {ToolName} failed against provider {Provider}.")]
    private static partial void LogAdapterFailure(
        ILogger logger, string toolName, string provider, Exception exception);
}
