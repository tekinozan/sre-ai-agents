using System.Net.Http.Json;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Llm.Providers;

internal sealed class AzureOpenAiProviderChatClient : OpenAiCompatibleProviderChatClient
{
    public AzureOpenAiProviderChatClient(
        IHttpClientFactory clients,
        ICredentialResolver credentials,
        ProviderConnectionRevision revision)
        : base(clients, credentials, revision, "azure-openai", ProviderClientCatalog.Descriptor(ProviderAdapterKind.AzureOpenAi))
    {
    }

    public override async Task<Result<ProviderChatResponse>> GetResponseAsync(
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

        if (string.IsNullOrWhiteSpace(Revision.ApiVersion))
        {
            return Result<ProviderChatResponse>.Failure(Error.Permanent(
                ApplicationErrorCodes.LlmInvalidRequest,
                "Azure OpenAI requires a pinned API version."));
        }

        OpenAiChatRequest payload = new(
            request.ProviderModel,
            [new ProviderMessage("system", request.SystemPrompt), new ProviderMessage("user", request.UserPrompt)],
            request.Temperature,
            request.MaxOutputTokens,
            new OpenAiResponseFormat("json_schema", new OpenAiJsonSchema("sre_response", true, request.JsonSchema)),
            request.Seed);
        string relative = $"openai/deployments/{Uri.EscapeDataString(request.ProviderModel)}/chat/completions"
            + $"?api-version={Uri.EscapeDataString(Revision.ApiVersion)}";
        using HttpRequestMessage message = new(HttpMethod.Post, BuildEndpoint(Revision.Endpoint, relative))
        {
            Content = JsonContent.Create(payload, ProviderJsonContext.Default.OpenAiChatRequest),
        };
        message.Headers.TryAddWithoutValidation("api-key", credential.Value.Value);
        return await SendAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
