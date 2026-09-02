using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Llm.Providers;

internal sealed class OpenAiProviderChatClient : OpenAiCompatibleProviderChatClient
{
    public OpenAiProviderChatClient(
        IHttpClientFactory clients,
        ICredentialResolver credentials,
        ProviderConnectionRevision revision)
        : base(clients, credentials, revision, "openai", ProviderClientCatalog.Descriptor(ProviderAdapterKind.OpenAi))
    {
    }
}

internal class OpenAiCompatibleProviderChatClient : IProviderChatClient
{
    protected readonly IHttpClientFactory Clients;
    protected readonly ICredentialResolver Credentials;
    protected readonly ProviderConnectionRevision Revision;
    private readonly string _observedProvider;

    public OpenAiCompatibleProviderChatClient(
        IHttpClientFactory clients,
        ICredentialResolver credentials,
        ProviderConnectionRevision revision)
        : this(clients, credentials, revision, "openai-compatible", ProviderClientCatalog.Descriptor(ProviderAdapterKind.OpenAiCompatible))
    {
    }

    protected OpenAiCompatibleProviderChatClient(
        IHttpClientFactory clients,
        ICredentialResolver credentials,
        ProviderConnectionRevision revision,
        string observedProvider,
        ProviderCapabilityDescriptor descriptor)
    {
        Clients = clients;
        Credentials = credentials;
        Revision = revision;
        _observedProvider = observedProvider;
        CapabilityDescriptor = descriptor;
    }

    public ProviderCapabilityDescriptor CapabilityDescriptor { get; }

    public virtual async Task<Result<ProviderChatResponse>> GetResponseAsync(
        ProviderChatRequest request,
        CancellationToken cancellationToken)
    {
        Result<CredentialValue> credential = await Credentials
            .ResolveAsync(Revision.CredentialRef, cancellationToken)
            .ConfigureAwait(false);
        if (credential.IsFailure)
        {
            return Result<ProviderChatResponse>.Failure(credential.Error);
        }

        OpenAiChatRequest payload = new(
            request.ProviderModel,
            [new ProviderMessage("system", request.SystemPrompt), new ProviderMessage("user", request.UserPrompt)],
            request.Temperature,
            request.MaxOutputTokens,
            new OpenAiResponseFormat("json_schema", new OpenAiJsonSchema("sre_response", Strict: true, request.JsonSchema)),
            request.Seed);

        Uri endpoint = BuildEndpoint(Revision.Endpoint, "chat/completions");
        using HttpRequestMessage message = new(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, ProviderJsonContext.Default.OpenAiChatRequest),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value.Value);
        if (!string.IsNullOrWhiteSpace(Revision.Organization))
        {
            message.Headers.TryAddWithoutValidation("OpenAI-Organization", Revision.Organization);
        }

        return await SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<Result<ProviderChatResponse>> SendAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = Clients.CreateClient(ProviderHttpClients.Chat);
            using HttpResponseMessage response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<ProviderChatResponse>.Failure(MapStatus(response.StatusCode));
            }

            OpenAiChatResponse? payload = await response.Content
                .ReadFromJsonAsync(ProviderJsonContext.Default.OpenAiChatResponse, cancellationToken)
                .ConfigureAwait(false);
            OpenAiChoice? choice = payload?.Choices is { Count: > 0 } choices ? choices[0] : null;
            if (payload is null)
            {
                return Result<ProviderChatResponse>.Failure(Error.Transient(
                    ApplicationErrorCodes.LlmProviderError,
                    "The model provider returned an invalid response envelope."));
            }

            string content = choice?.Message?.Content ?? string.Empty;

            string requestId = Header(response, "x-request-id") ?? payload.Id ?? string.Empty;
            return Result<ProviderChatResponse>.Success(new ProviderChatResponse(
                content,
                _observedProvider,
                payload.Model ?? string.Empty,
                payload.Usage?.PromptTokens ?? 0,
                payload.Usage?.CompletionTokens ?? 0,
                requestId,
                choice?.FinishReason ?? "unknown"));
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

    internal static Error MapStatus(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Error.Permanent(
                ApplicationErrorCodes.LlmAuthenticationFailed, "The model provider rejected its configured credential."),
            HttpStatusCode.TooManyRequests => Error.Transient(
                ApplicationErrorCodes.LlmRateLimited, "The model provider rate limit was reached."),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => Error.Transient(
                ApplicationErrorCodes.LlmTimeout, "The model provider request timed out."),
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => Error.Permanent(
                ApplicationErrorCodes.LlmInvalidRequest, "The model provider rejected the request."),
            _ when (int)statusCode >= 500 => Error.Transient(
                ApplicationErrorCodes.LlmUnavailable, "The model provider is unavailable."),
            _ => Error.Transient(ApplicationErrorCodes.LlmProviderError, "The model provider returned an error."),
        };

    internal static Uri BuildEndpoint(string baseAddress, string relative)
    {
        string normalized = baseAddress.EndsWith('/') ? baseAddress : baseAddress + "/";
        if (Uri.TryCreate(new Uri(normalized, UriKind.Absolute), relative, out Uri? result))
        {
            return result;
        }
        throw new InvalidOperationException("Geçersiz API Endpoint URL'si.");
    }

    internal static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
}

internal static class ProviderHttpClients
{
    public const string Chat = "sre-llm-provider";
}
