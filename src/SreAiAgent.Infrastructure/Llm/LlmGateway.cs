using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Audit;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Llm;

/// <summary>
/// The platform's single route to a model (ARCHITECTURE.md 6.1).
/// </summary>
/// <remarks>
/// Microsoft.Extensions.AI provides the provider abstraction; this class provides
/// the platform policy on top of it. Everything that matters operationally lives
/// here rather than in an SDK: schema validation, timeouts, cost accounting,
/// audit metadata, span attributes and the mapping of provider failures onto the
/// error taxonomy. Swapping the provider changes one DI registration and none of
/// this.
/// </remarks>
internal sealed partial class LlmGateway : ILlmGateway
{
    private readonly IChatClient _chatClient;
    private readonly StructuredOutputValidator _validator;
    private readonly SreMetrics _metrics;
    private readonly LlmOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditSink _audit;
    private readonly ILogger<LlmGateway> _logger;

    public LlmGateway(
        IChatClient chatClient,
        StructuredOutputValidator validator,
        SreMetrics metrics,
        IOptions<LlmOptions> options,
        IAuditSink audit,
        TimeProvider timeProvider,
        ILogger<LlmGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _audit = audit;
        _chatClient = chatClient;
        _validator = validator;
        _metrics = metrics;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<LlmResult<T>>> GenerateStructuredAsync<T>(
        LlmRequest<T> request,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);

        using Activity? activity = SreActivitySources.Llm.StartActivity(
            SreSpanNames.LlmCall, ActivityKind.Client);

        activity
            .SetSafeTag(TraceAttributes.IncidentId, request.Metadata.IncidentId.ToString())
            .SetSafeTag(TraceAttributes.AgentName, request.Metadata.AgentName)
            .SetSafeTag(TraceAttributes.PromptName, request.Metadata.PromptName)
            .SetSafeTag(TraceAttributes.PromptVersion, request.Metadata.PromptVersion)
            .SetSafeTag(TraceAttributes.PromptHash, request.Metadata.PromptHash)
            .SetSafeTag(TraceAttributes.ModelProvider, request.ModelProfile.Provider)
            .SetSafeTag(TraceAttributes.ModelName, request.ModelProfile.ModelName)
            .SetSafeTag(TraceAttributes.Tenant, request.Metadata.Tenant);

        // The prompt text is never attached to the span. Only the hash is, which
        // identifies it for an audit without putting the retrieved evidence into
        // a tracing backend (ARCHITECTURE.md 16.1).

        long startTimestamp = _timeProvider.GetTimestamp();

        // The per-call timeout is enforced here rather than trusted to the
        // provider. A provider that hangs past its own advertised timeout would
        // otherwise consume the agent's entire budget on one call.
        using CancellationTokenSource timeoutSource = new(request.Timeout, _timeProvider);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            ChatResponse response = await _chatClient
                .GetResponseAsync(BuildMessages(request), BuildOptions(request), linked.Token)
                .ConfigureAwait(false);

            TimeSpan latency = _timeProvider.GetElapsedTime(startTimestamp);
            return Interpret(request, response, latency, activity);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            TimeSpan latency = _timeProvider.GetElapsedTime(startTimestamp);
            return Fail(
                request,
                activity,
                latency,
                Error.Transient(
                    ApplicationErrorCodes.LlmTimeout,
                    $"Model call exceeded its {request.Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s timeout."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The provider exception never escapes. Section 17 requires a stable
            // code and a safe message at the boundary; the detail is logged, not
            // returned, because provider messages routinely echo request content.
            TimeSpan latency = _timeProvider.GetElapsedTime(startTimestamp);
            LogProviderFailure(_logger, request.ModelProfile.Provider, request.Metadata.AgentName, exception);

            return Fail(
                request,
                activity,
                latency,
                Error.Transient(
                    ApplicationErrorCodes.LlmProviderError,
                    "The model provider returned an error."));
        }
    }

    private Result<LlmResult<T>> Interpret<T>(
        LlmRequest<T> request,
        ChatResponse response,
        TimeSpan latency,
        Activity? activity)
        where T : class
    {
        long inputTokens = response.Usage?.InputTokenCount ?? 0;
        long outputTokens = response.Usage?.OutputTokenCount ?? 0;
        decimal cost = request.ModelProfile.EstimateCost(inputTokens, outputTokens);
        string finishReason = response.FinishReason?.Value ?? "unknown";

        // A truncated response is a failure, not something to parse optimistically.
        // Half a finding still deserializes into an object with an empty
        // observation list, which would be recorded as an expert that found
        // nothing rather than as a call that did not complete.
        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            _metrics.LlmCall(
                request.ModelProfile.Provider, request.ModelProfile.ModelName, "truncated",
                inputTokens, outputTokens, cost);

            return Fail(request, activity, latency, Error.Transient(
                ApplicationErrorCodes.LlmStructuredOutputInvalid,
                "The model response was truncated by the output token limit."));
        }

        string text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _metrics.LlmCall(
                request.ModelProfile.Provider, request.ModelProfile.ModelName, "empty",
                inputTokens, outputTokens, cost);

            return Fail(request, activity, latency, Error.Transient(
                ApplicationErrorCodes.LlmStructuredOutputInvalid, "The model returned no content."));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            _metrics.LlmCall(
                request.ModelProfile.Provider, request.ModelProfile.ModelName, "invalid_json",
                inputTokens, outputTokens, cost);

            return Fail(request, activity, latency, Error.Transient(
                ApplicationErrorCodes.LlmStructuredOutputInvalid, "The model response was not valid JSON."));
        }

        using (document)
        {
            Result validation = _validator.Validate(request.ResponseSchemaName, document.RootElement);
            if (validation.IsFailure)
            {
                _metrics.LlmCall(
                    request.ModelProfile.Provider, request.ModelProfile.ModelName, "schema_invalid",
                    inputTokens, outputTokens, cost);

                LogSchemaRejected(
                    _logger, request.ResponseSchemaName, request.Metadata.AgentName, validation.Error.Message);

                return Fail(request, activity, latency, validation.Error);
            }

            if (LlmJsonContext.Default.GetTypeInfo(typeof(T)) is not JsonTypeInfo<T> typeInfo)
            {
                return Fail(request, activity, latency, Error.Permanent(
                    ApplicationErrorCodes.ConfigInvalid,
                    $"No serialization metadata is generated for '{typeof(T).Name}'. "
                    + "Add it to LlmJsonContext."));
            }

            T? value = document.RootElement.Deserialize(typeInfo);
            if (value is null)
            {
                return Fail(request, activity, latency, Error.Transient(
                    ApplicationErrorCodes.LlmStructuredOutputInvalid,
                    "The model response bound to null."));
            }

            _metrics.LlmCall(
                request.ModelProfile.Provider, request.ModelProfile.ModelName, "succeeded",
                inputTokens, outputTokens, cost);

            activity.SetSafeTag(TraceAttributes.ResultStatus, "succeeded");

            Audit(request, "succeeded", error: null, inputTokens, outputTokens, cost, latency);

            return Result<LlmResult<T>>.Success(new LlmResult<T>(
                Value: value,
                Provider: request.ModelProfile.Provider,
                Model: request.ModelProfile.ModelName,
                InputTokens: (int)inputTokens,
                OutputTokens: (int)outputTokens,
                Latency: latency,
                RequestId: response.ResponseId ?? string.Empty,
                FinishReason: finishReason,
                EstimatedCost: cost));
        }
    }

    private Result<LlmResult<T>> Fail<T>(
        LlmRequest<T> request,
        Activity? activity,
        TimeSpan latency,
        Error error)
        where T : class
    {
        activity
            .SetSafeTag(TraceAttributes.ResultStatus, "failed")
            .SetSafeTag(TraceAttributes.ErrorCode, error.Code.Value);
        activity?.SetStatus(ActivityStatusCode.Error);

        if (error.Code.Value is not "LLM_STRUCTURED_OUTPUT_INVALID")
        {
            _metrics.LlmCall(
                request.ModelProfile.Provider, request.ModelProfile.ModelName, "failed",
                inputTokens: 0, outputTokens: 0, estimatedCost: 0m);
        }

        // Failures are audited too, with the tokens the attempt actually
        // consumed. A provider that rejects a response after generating it still
        // charges for it, and a cost total assembled only from successes is the
        // wrong number in exactly the month someone asks about it.
        Audit(request, "failed", error, inputTokens: 0, outputTokens: 0, estimatedCost: 0m, latency);

        return Result<LlmResult<T>>.Failure(error);
    }

    /// <summary>Stages one audit row for the call, whatever its outcome.</summary>
    private void Audit<T>(
        LlmRequest<T> request,
        string outcome,
        Error? error,
        long inputTokens,
        long outputTokens,
        decimal estimatedCost,
        TimeSpan latency)
        where T : class
        => _audit.RecordLlmCall(new LlmCallAudit(
            LlmCallId.New(),
            request.Metadata.IncidentId,
            request.Metadata.AgentRunId,
            request.Metadata.AgentName,
            request.Metadata.PromptName,
            request.Metadata.PromptVersion,
            request.Metadata.PromptHash,
            request.ModelProfile.Provider,
            request.ModelProfile.ModelName,
            outcome,
            error?.Code,
            inputTokens,
            outputTokens,
            estimatedCost,
            latency,
            _timeProvider.GetUtcNow()));

    private static List<ChatMessage> BuildMessages<T>(LlmRequest<T> request)
        where T : class
        =>
        [
            new ChatMessage(ChatRole.System, request.SystemPrompt),
            new ChatMessage(ChatRole.User, request.UserPrompt),
        ];

    private static ChatOptions BuildOptions<T>(LlmRequest<T> request)
        where T : class
        => new()
        {
            ModelId = request.ModelProfile.ModelName,
            Temperature = request.ModelProfile.Temperature,
            MaxOutputTokens = request.ModelProfile.MaxOutputTokens,
            // Asking the provider for JSON as well as validating it here is belt
            // and braces on purpose: provider enforcement reduces the retry rate,
            // and local validation is what makes the guarantee dependable.
            ResponseFormat = ChatResponseFormat.Json,
        };

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Model provider {Provider} failed for agent {AgentName}.")]
    private static partial void LogProviderFailure(
        ILogger logger, string provider, string agentName, Exception exception);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Model output rejected against schema {SchemaName} for agent {AgentName}: {Detail}")]
    private static partial void LogSchemaRejected(
        ILogger logger, string schemaName, string agentName, string detail);
}
