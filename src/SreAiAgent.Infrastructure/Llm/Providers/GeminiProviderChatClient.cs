using System.Net.Http.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Llm.Providers;

/// <summary>
/// Google's Gemini API on its own wire (gemini-budgets-notifications.md G-1).
/// </summary>
/// <remarks>
/// Gemini also answers on an OpenAI-compatible path, which D45's dialect covers.
/// The native one is kept because it says more: structured output is a JSON
/// Schema the server enforces, the system prompt is a first-class instruction,
/// and usage reports thinking tokens separately. Those are billed as output, so
/// they are counted as output here; dropping them under-reported the cost of
/// every thinking model.
/// </remarks>
internal sealed class GeminiProviderChatClient(
    IHttpClientFactory clients,
    ICredentialResolver credentials,
    ProviderConnectionRevision revision) : IProviderChatClient
{
    /// <summary>The header Gemini takes its key in, rather than the query string where it would be logged.</summary>
    internal const string KeyHeader = "x-goog-api-key";

    public ProviderCapabilityDescriptor CapabilityDescriptor { get; } =
        ProviderClientCatalog.Descriptor(ProviderAdapterKind.Gemini);

    /// <summary>The API version a revision pins, <c>v1beta</c> when it pins none.</summary>
    internal static string VersionOf(ProviderConnectionRevision revision)
        => string.IsNullOrWhiteSpace(revision.ApiVersion) ? "v1beta" : revision.ApiVersion;

    public async Task<Result<ProviderChatResponse>> GetResponseAsync(
        ProviderChatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result<CredentialValue> credential = await credentials
            .ResolveAsync(revision.CredentialRef, cancellationToken)
            .ConfigureAwait(false);
        if (credential.IsFailure)
        {
            return Result<ProviderChatResponse>.Failure(credential.Error);
        }

        GeminiGenerateRequest payload = new(
            new GeminiContent(Role: null, [new GeminiPart(request.SystemPrompt)]),
            [new GeminiContent("user", [new GeminiPart(request.UserPrompt)])],
            new GeminiGenerationConfig(
                request.Temperature,
                request.MaxOutputTokens,
                "application/json",
                request.JsonSchema,
                request.Seed));

        string relative = $"{VersionOf(revision)}/models/{Uri.EscapeDataString(request.ProviderModel)}:generateContent";
        using HttpRequestMessage message = new(
            HttpMethod.Post,
            OpenAiCompatibleProviderChatClient.BuildEndpoint(revision.Endpoint, relative))
        {
            Content = JsonContent.Create(payload, ProviderJsonContext.Default.GeminiGenerateRequest),
        };
        message.Headers.TryAddWithoutValidation(KeyHeader, credential.Value.Value);

        try
        {
            HttpClient client = clients.CreateClient(ProviderHttpClients.Chat);
            using HttpResponseMessage response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<ProviderChatResponse>.Failure(
                    OpenAiCompatibleProviderChatClient.MapStatus(response.StatusCode));
            }

            GeminiGenerateResponse? result = await response.Content
                .ReadFromJsonAsync(ProviderJsonContext.Default.GeminiGenerateResponse, cancellationToken)
                .ConfigureAwait(false);
            if (result is null)
            {
                return Result<ProviderChatResponse>.Failure(Error.Transient(
                    ApplicationErrorCodes.LlmProviderError,
                    "The model provider returned an invalid response envelope."));
            }

            GeminiCandidate? candidate = result.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
            string text = string.Concat(
                candidate?.Content?.Parts?.Select(static part => part.Text ?? string.Empty) ?? []);

            return Result<ProviderChatResponse>.Success(new ProviderChatResponse(
                text,
                "gemini",
                result.ModelVersion ?? string.Empty,
                result.UsageMetadata?.PromptTokenCount ?? 0,
                (result.UsageMetadata?.CandidatesTokenCount ?? 0) + (result.UsageMetadata?.ThoughtsTokenCount ?? 0),
                result.ResponseId ?? string.Empty,
                candidate?.FinishReason ?? "unknown"));
        }
        catch (HttpRequestException)
        {
            return Result<ProviderChatResponse>.Failure(Error.Transient(
                ApplicationErrorCodes.LlmUnavailable,
                "The model provider is unavailable."));
        }
        catch (BrokenCircuitException)
        {
            return Result<ProviderChatResponse>.Failure(Error.Transient(
                ApplicationErrorCodes.LlmUnavailable,
                "The model provider is unavailable."));
        }
        catch (TimeoutRejectedException)
        {
            return Result<ProviderChatResponse>.Failure(Error.Transient(
                ApplicationErrorCodes.LlmTimeout,
                "The model provider request timed out."));
        }
        catch (System.Text.Json.JsonException)
        {
            return Result<ProviderChatResponse>.Failure(Error.Transient(
                ApplicationErrorCodes.LlmProviderError,
                "The model provider returned an invalid response envelope."));
        }
    }
}
