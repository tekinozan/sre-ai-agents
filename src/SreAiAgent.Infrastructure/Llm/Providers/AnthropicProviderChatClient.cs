using System.Net.Http.Json;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Llm.Providers;

internal sealed class AnthropicProviderChatClient : IProviderChatClient
{
    private readonly IHttpClientFactory _clients;
    private readonly ICredentialResolver _credentials;
    private readonly ProviderConnectionRevision _revision;

    public AnthropicProviderChatClient(
        IHttpClientFactory clients,
        ICredentialResolver credentials,
        ProviderConnectionRevision revision)
    {
        _clients = clients;
        _credentials = credentials;
        _revision = revision;
    }

    public ProviderCapabilityDescriptor CapabilityDescriptor { get; } = ProviderClientCatalog.Descriptor(ProviderAdapterKind.Anthropic);

    public async Task<Result<ProviderChatResponse>> GetResponseAsync(
        ProviderChatRequest request,
        CancellationToken cancellationToken)
    {
        Result<CredentialValue> credential = await _credentials
            .ResolveAsync(_revision.CredentialRef, cancellationToken)
            .ConfigureAwait(false);
        if (credential.IsFailure)
        {
            return Result<ProviderChatResponse>.Failure(credential.Error);
        }

        AnthropicChatRequest payload = new(
            request.ProviderModel,
            request.SystemPrompt,
            [new ProviderMessage("user", request.UserPrompt)],
            request.MaxOutputTokens,
            request.Temperature,
            new AnthropicOutputConfig(new AnthropicFormat("json_schema", request.JsonSchema)));
        using HttpRequestMessage message = new(
            HttpMethod.Post,
            OpenAiCompatibleProviderChatClient.BuildEndpoint(_revision.Endpoint, "messages"))
        {
            Content = JsonContent.Create(payload, ProviderJsonContext.Default.AnthropicChatRequest),
        };
        message.Headers.TryAddWithoutValidation("x-api-key", credential.Value.Value);
        message.Headers.TryAddWithoutValidation("anthropic-version", _revision.ApiVersion ?? "2023-06-01");

        try
        {
            HttpClient client = _clients.CreateClient(ProviderHttpClients.Chat);
            using HttpResponseMessage response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<ProviderChatResponse>.Failure(
                    OpenAiCompatibleProviderChatClient.MapStatus(response.StatusCode));
            }

            AnthropicChatResponse? result = await response.Content
                .ReadFromJsonAsync(ProviderJsonContext.Default.AnthropicChatResponse, cancellationToken)
                .ConfigureAwait(false);
            string text = result?.Content?
                .FirstOrDefault(static item => string.Equals(item.Type, "text", StringComparison.Ordinal))?.Text
                ?? string.Empty;
            if (result is null)
            {
                return Result<ProviderChatResponse>.Failure(Error.Transient(
                    ApplicationErrorCodes.LlmProviderError,
                    "The model provider returned an invalid response envelope."));
            }

            return Result<ProviderChatResponse>.Success(new ProviderChatResponse(
                text,
                "anthropic",
                result.Model ?? string.Empty,
                result.Usage?.InputTokens ?? 0,
                result.Usage?.OutputTokens ?? 0,
                OpenAiCompatibleProviderChatClient.Header(response, "request-id") ?? result.Id ?? string.Empty,
                result.StopReason ?? "unknown"));
        }
        catch (HttpRequestException)
        {
            return Result<ProviderChatResponse>.Failure(Error.Transient(
                ApplicationErrorCodes.LlmUnavailable,
                "The model provider is unavailable."));
        }
        catch (System.Text.Json.JsonException)
        {
            return Result<ProviderChatResponse>.Failure(Error.Transient(
                ApplicationErrorCodes.LlmProviderError,
                "The model provider returned an invalid response envelope."));
        }
    }
}
