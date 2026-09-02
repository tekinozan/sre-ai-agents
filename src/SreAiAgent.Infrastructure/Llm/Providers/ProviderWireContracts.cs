using System.Text.Json;
using System.Text.Json.Serialization;

namespace SreAiAgent.Infrastructure.Llm.Providers;

internal sealed record OpenAiChatRequest(
    string Model,
    IReadOnlyList<ProviderMessage> Messages,
    float Temperature,
    int MaxTokens,
    OpenAiResponseFormat ResponseFormat,
    int? Seed);

internal sealed record ProviderMessage(string Role, string Content);
internal sealed record OpenAiResponseFormat(string Type, OpenAiJsonSchema JsonSchema);
internal sealed record OpenAiJsonSchema(string Name, bool Strict, JsonElement Schema);

internal sealed record OpenAiChatResponse(
    string? Id,
    string? Model,
    IReadOnlyList<OpenAiChoice>? Choices,
    OpenAiUsage? Usage);

internal sealed record OpenAiChoice(OpenAiMessage? Message, string? FinishReason);
internal sealed record OpenAiMessage(string? Content);
internal sealed record OpenAiUsage(long PromptTokens, long CompletionTokens);

internal sealed record AnthropicChatRequest(
    string Model,
    string System,
    IReadOnlyList<ProviderMessage> Messages,
    int MaxTokens,
    float Temperature,
    AnthropicOutputConfig OutputConfig);

internal sealed record AnthropicOutputConfig(AnthropicFormat Format);
internal sealed record AnthropicFormat(string Type, JsonElement Schema);

internal sealed record AnthropicChatResponse(
    string? Id,
    string? Model,
    IReadOnlyList<AnthropicContent>? Content,
    string? StopReason,
    AnthropicUsage? Usage);

internal sealed record AnthropicContent(string? Type, string? Text);
internal sealed record AnthropicUsage(long InputTokens, long OutputTokens);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiChatResponse))]
[JsonSerializable(typeof(AnthropicChatRequest))]
[JsonSerializable(typeof(AnthropicChatResponse))]
internal sealed partial class ProviderJsonContext : JsonSerializerContext;
