using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Audit;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Llm;

internal sealed partial class LlmGateway : ILlmGateway
{
    private readonly IChatClientResolver _resolver;
    private readonly StructuredOutputValidator _validator;
    private readonly SreMetrics _metrics;
    private readonly LlmOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditSink _audit;
    private readonly ITenantBudgetGuard _budget;
    private readonly ILogger<LlmGateway> _logger;

    public LlmGateway(
        IChatClientResolver resolver,
        StructuredOutputValidator validator,
        SreMetrics metrics,
        IOptions<LlmOptions> options,
        IAuditSink audit,
        ITenantBudgetGuard budget,
        TimeProvider timeProvider,
        ILogger<LlmGateway> logger)
    {
        _resolver = resolver;
        _validator = validator;
        _metrics = metrics;
        _options = options.Value;
        _audit = audit;
        _budget = budget;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<Result<LlmResult<T>>> GenerateStructuredAsync<T>(
        LlmRequest<T> request,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        using Activity? activity = SreActivitySources.Llm.StartActivity(SreSpanNames.LlmCall, ActivityKind.Client);
        activity
            .SetSafeTag(TraceAttributes.IncidentId, request.Metadata.IncidentId.ToString())
            .SetSafeTag(TraceAttributes.AgentName, request.Metadata.AgentName)
            .SetSafeTag(TraceAttributes.PromptName, request.Metadata.PromptName)
            .SetSafeTag(TraceAttributes.PromptVersion, request.Metadata.PromptVersion)
            .SetSafeTag(TraceAttributes.PromptHash, request.Metadata.PromptHash)
            .SetSafeTag(TraceAttributes.ModelProvider, request.Candidate.AdapterKind.ToString())
            .SetSafeTag(TraceAttributes.ModelName, request.Candidate.DeploymentLogicalName)
            .SetSafeTag(TraceAttributes.ModelDeploymentVersion, request.Candidate.DeploymentVersion)
            .SetSafeTag(TraceAttributes.ModelAssignmentRelease, request.Candidate.AssignmentReleaseId.Value.ToString())
            .SetSafeTag(TraceAttributes.Tenant, request.Metadata.Tenant);

        long startTimestamp = _timeProvider.GetTimestamp();
        Result<JsonElement> schema = _validator.ResolveSchema(request.ResponseSchemaName);
        if (schema.IsFailure)
        {
            return Fail(request, activity, Elapsed(startTimestamp), schema.Error, Accounting.Empty);
        }

        // Before anything that could reach the provider: an exhausted budget
        // means no outbound call at all, not a call whose cost is regretted
        // afterwards. The refusal is audited like any failure, at zero cost.
        Result budget = await _budget.CheckAsync(request.Metadata.Tenant, cancellationToken).ConfigureAwait(false);
        if (budget.IsFailure)
        {
            return Fail(request, activity, Elapsed(startTimestamp), budget.Error, Accounting.Empty);
        }

        Result<IProviderChatClient> resolved = await _resolver
            .ResolveAsync(request.Candidate, cancellationToken)
            .ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Fail(request, activity, Elapsed(startTimestamp), resolved.Error, Accounting.Empty);
        }

        int? seed = null;
        if (request.Candidate.ProviderParameters.TryGetValue("seed", out string? configuredSeed))
        {
            seed = int.Parse(configuredSeed, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        ProviderChatRequest providerRequest = new(
            request.SystemPrompt,
            request.UserPrompt,
            request.Candidate.ProviderModel,
            schema.Value,
            request.Candidate.Temperature,
            request.Candidate.MaxOutputTokens,
            seed,
            request.Candidate.ProviderParameters);

        using CancellationTokenSource timeoutSource = new(request.Timeout, _timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                Result<ProviderChatResponse> response = await resolved.Value
                    .GetResponseAsync(providerRequest, linked.Token)
                    .ConfigureAwait(false);
                Result<LlmResult<T>> outcome = response.IsFailure
                    ? Fail(
                        request,
                        activity,
                        Elapsed(startTimestamp),
                        response.Error,
                        Accounting.Empty)
                    : Interpret(request, response.Value, Elapsed(startTimestamp), activity);

                // One retry budget: transport/HTTP status retries are handled by the
                // HTTP resilience pipeline. The gateway loop retries only semantic response
                // failures that are invisible to HTTP middleware (schema-invalid, truncated, empty output).
                if (outcome.IsSuccess
                    || attempt == _options.MaxRetries
                    || !IsSemanticRetryable(outcome.Error.Code))
                {
                    return outcome;
                }

                // Exponential backoff with jitter: starts at 100ms on first retry (attempt = 0), then 200ms, 400ms...
                TimeSpan backoff = TimeSpan.FromMilliseconds(
                    (100 * (1 << attempt)) + Random.Shared.Next(0, 100));

                await Task.Delay(backoff, _timeProvider, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                return Fail(
                    request,
                    activity,
                    Elapsed(startTimestamp),
                    Error.Transient(
                        ApplicationErrorCodes.LlmTimeout,
                        $"Model call exceeded its {request.Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s timeout."),
                    Accounting.Empty);
            }
            catch (TimeoutRejectedException exception)
            {
                LogProviderFailure(_logger, request.Candidate.AdapterKind.ToString(), request.Metadata.AgentName, exception);
                return Fail(
                    request,
                    activity,
                    Elapsed(startTimestamp),
                    Error.Transient(ApplicationErrorCodes.LlmTimeout, "The model provider request timed out."),
                    Accounting.Empty);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogProviderFailure(_logger, request.Candidate.AdapterKind.ToString(), request.Metadata.AgentName, exception);
                return Fail(
                    request,
                    activity,
                    Elapsed(startTimestamp),
                    Error.Transient(ApplicationErrorCodes.LlmProviderError, "The model provider returned an error."),
                    Accounting.Empty);
            }
        }

        // The loop returns on the final attempt; the compiler cannot prove that invariant.
        return Fail(
            request,
            activity,
            Elapsed(startTimestamp),
            Error.Transient(ApplicationErrorCodes.LlmProviderError, "The model provider returned an error."),
            Accounting.Empty);
    }

    private Result<LlmResult<T>> Interpret<T>(
        LlmRequest<T> request,
        ProviderChatResponse response,
        TimeSpan latency,
        Activity? activity)
        where T : class
    {
        Accounting accounting = new(
            response.InputTokens,
            response.OutputTokens,
            request.Candidate.EstimateCost(response.InputTokens, response.OutputTokens),
            response.ObservedProvider,
            response.ObservedModel,
            response.RequestId,
            response.FinishReason);

        if (string.Equals(response.FinishReason, "length", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response.FinishReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(request, activity, latency, Error.Transient(
                ApplicationErrorCodes.LlmStructuredOutputInvalid,
                "The model response was truncated by the output token limit."), accounting);
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            return Fail(request, activity, latency, Error.Transient(
                ApplicationErrorCodes.LlmStructuredOutputInvalid,
                "The model returned no content."), accounting);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(response.Text, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return Fail(request, activity, latency, Error.Transient(
                ApplicationErrorCodes.LlmStructuredOutputInvalid,
                "The model response was not valid JSON."), accounting);
        }

        using (document)
        {
            Result validation = _validator.Validate(request.ResponseSchemaName, document.RootElement);
            if (validation.IsFailure)
            {
                LogSchemaRejected(_logger, request.ResponseSchemaName, request.Metadata.AgentName, validation.Error.Message);
                return Fail(request, activity, latency, validation.Error, accounting);
            }

            if (LlmJsonContext.Default.GetTypeInfo(typeof(T)) is not JsonTypeInfo<T> typeInfo)
            {
                return Fail(request, activity, latency, Error.Permanent(
                    ApplicationErrorCodes.ConfigInvalid,
                    $"No serialization metadata is generated for '{typeof(T).Name}'. Add it to LlmJsonContext."),
                    accounting);
            }

            T? value;
            try
            {
                value = document.RootElement.Deserialize(typeInfo);
            }
            catch (JsonException)
            {
                return Fail(request, activity, latency, Error.Transient(
                    ApplicationErrorCodes.LlmStructuredOutputInvalid,
                    "The model response could not be bound to its contract."), accounting);
            }

            if (value is null)
            {
                return Fail(request, activity, latency, Error.Transient(
                    ApplicationErrorCodes.LlmStructuredOutputInvalid,
                    "The model response bound to null."), accounting);
            }

            _metrics.LlmCall(
                request.Candidate.AdapterKind.ToString(),
                request.Candidate.DeploymentLogicalName,
                request.Candidate.DeploymentVersion,
                "succeeded",
                accounting.InputTokens,
                accounting.OutputTokens,
                accounting.EstimatedCost);
            activity.SetSafeTag(TraceAttributes.ResultStatus, "succeeded");
            Audit(request, "succeeded", null, accounting, latency);
            return Result<LlmResult<T>>.Success(new LlmResult<T>(
                value,
                accounting.ObservedProvider,
                accounting.ObservedModel,
                ClampTokenCount(accounting.InputTokens),
                ClampTokenCount(accounting.OutputTokens),
                latency,
                accounting.ProviderRequestId,
                accounting.FinishReason,
                accounting.EstimatedCost));
        }
    }

    private Result<LlmResult<T>> Fail<T>(
        LlmRequest<T> request,
        Activity? activity,
        TimeSpan latency,
        Error error,
        Accounting accounting)
        where T : class
    {
        string outcome = ClassifyOutcome(error.Code);
        activity
            .SetSafeTag(TraceAttributes.ResultStatus, outcome)
            .SetSafeTag(TraceAttributes.ErrorCode, error.Code.Value);
        activity?.SetStatus(ActivityStatusCode.Error);
        _metrics.LlmCall(
            request.Candidate.AdapterKind.ToString(),
            request.Candidate.DeploymentLogicalName,
            request.Candidate.DeploymentVersion,
            outcome,
            accounting.InputTokens,
            accounting.OutputTokens,
            accounting.EstimatedCost);
        Audit(request, outcome, error, accounting, latency);
        return Result<LlmResult<T>>.Failure(error);
    }

    private void Audit<T>(
        LlmRequest<T> request,
        string outcome,
        Error? error,
        Accounting accounting,
        TimeSpan latency)
        where T : class
    {
        PinnedModelCandidate candidate = request.Candidate;
        _audit.RecordLlmCall(new LlmCallAudit(
            LlmCallId.New(),
            request.Metadata.IncidentId,
            request.Metadata.AgentRunId,
            request.Metadata.Tenant,
            request.Metadata.AgentName,
            request.Metadata.PromptName,
            request.Metadata.PromptVersion,
            request.Metadata.PromptHash,
            candidate.AssignmentReleaseId.Value,
            candidate.AssignmentConfigurationHash,
            candidate.DeploymentVersionId.Value,
            candidate.DeploymentConfigurationHash,
            candidate.ProviderConnectionRevisionId.Value,
            candidate.ProviderConfigurationHash,
            candidate.AdapterKind.ToString(),
            candidate.ProviderModel,
            accounting.ObservedProvider,
            accounting.ObservedModel,
            accounting.ProviderRequestId,
            accounting.FinishReason,
            outcome,
            error?.Code,
            accounting.InputTokens,
            accounting.OutputTokens,
            accounting.EstimatedCost,
            latency,
            _timeProvider.GetUtcNow()));
    }

    private TimeSpan Elapsed(long startTimestamp) => _timeProvider.GetElapsedTime(startTimestamp);
    private static int ClampTokenCount(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    private sealed record Accounting(
        long InputTokens,
        long OutputTokens,
        decimal EstimatedCost,
        string ObservedProvider,
        string ObservedModel,
        string ProviderRequestId,
        string FinishReason)
    {
        public static Accounting Empty { get; } = new(0, 0, 0m, string.Empty, string.Empty, string.Empty, "unknown");
    }

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Model provider {Provider} failed for agent {AgentName}.")]
    private static partial void LogProviderFailure(
        ILogger logger,
        string provider,
        string agentName,
        Exception exception);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Model output rejected against schema {SchemaName} for agent {AgentName}: {Detail}")]
    private static partial void LogSchemaRejected(
        ILogger logger,
        string schemaName,
        string agentName,
        string detail);

    private static bool IsSemanticRetryable(ErrorCode code)
        => code == ApplicationErrorCodes.LlmStructuredOutputInvalid;

    private static string ClassifyOutcome(ErrorCode code)
        => code == ApplicationErrorCodes.LlmTimeout ? "timeout" : "failed";
}
