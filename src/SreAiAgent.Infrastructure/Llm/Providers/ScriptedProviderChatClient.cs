using Microsoft.Extensions.AI;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Llm.Providers;

internal sealed class ScriptedProviderChatClient(ScriptedChatClient client) : IProviderChatClient
{
    private readonly ScriptedChatClient _client = client;

    public ProviderCapabilityDescriptor CapabilityDescriptor { get; } = ProviderClientCatalog.Descriptor(ProviderAdapterKind.Scripted);

    public async Task<Result<ProviderChatResponse>> GetResponseAsync(
        ProviderChatRequest request,
        CancellationToken cancellationToken)
    {
        ChatResponse response = await _client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, request.SystemPrompt), new ChatMessage(ChatRole.User, request.UserPrompt)],
            new ChatOptions
            {
                ModelId = request.ProviderModel,
                Temperature = request.Temperature,
                MaxOutputTokens = request.MaxOutputTokens,
                ResponseFormat = ChatResponseFormat.Json,
            },
            cancellationToken).ConfigureAwait(false);
        return Result<ProviderChatResponse>.Success(new ProviderChatResponse(
            response.Text,
            "scripted",
            response.ModelId ?? request.ProviderModel,
            response.Usage?.InputTokenCount ?? 0,
            response.Usage?.OutputTokenCount ?? 0,
            response.ResponseId ?? string.Empty,
            response.FinishReason?.Value ?? "unknown"));
    }
}
