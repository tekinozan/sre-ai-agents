using System.Text.Json;
using System.Text.Json.Serialization;

namespace SreAiAgent.Infrastructure.Llm.Providers;

// Nullable fields are left out of the body when null (WhenWritingNull below),
// which is how a deployment's dialect omits a field rather than sending a
// default the server rejects (OpenAiDialect).
internal sealed record OpenAiChatRequest(
    string Model,
    IReadOnlyList<ProviderMessage> Messages,
    float? Temperature,
    int? MaxTokens,
    int? MaxCompletionTokens,
    OpenAiResponseFormat ResponseFormat,
    int? Seed);

internal sealed record ProviderMessage(string Role, string Content);
internal sealed record OpenAiResponseFormat(string Type, OpenAiJsonSchema? JsonSchema);
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

// Gemini's generateContent shape. Camel case on the wire, unlike the others,
// so the context below names each member explicitly.
internal sealed record GeminiGenerateRequest(
    [property: JsonPropertyName("systemInstruction")] GeminiContent SystemInstruction,
    [property: JsonPropertyName("contents")] IReadOnlyList<GeminiContent> Contents,
    [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

internal sealed record GeminiContent(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("parts")] IReadOnlyList<GeminiPart>? Parts);

internal sealed record GeminiPart([property: JsonPropertyName("text")] string? Text);

internal sealed record GeminiGenerationConfig(
    [property: JsonPropertyName("temperature")] float Temperature,
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
    [property: JsonPropertyName("responseMimeType")] string ResponseMimeType,
    [property: JsonPropertyName("responseJsonSchema")] JsonElement ResponseJsonSchema,
    [property: JsonPropertyName("seed")] int? Seed);

internal sealed record GeminiGenerateResponse(
    [property: JsonPropertyName("candidates")] IReadOnlyList<GeminiCandidate>? Candidates,
    [property: JsonPropertyName("usageMetadata")] GeminiUsage? UsageMetadata,
    [property: JsonPropertyName("modelVersion")] string? ModelVersion,
    [property: JsonPropertyName("responseId")] string? ResponseId);

internal sealed record GeminiCandidate(
    [property: JsonPropertyName("content")] GeminiContent? Content,
    [property: JsonPropertyName("finishReason")] string? FinishReason);

internal sealed record GeminiUsage(
    [property: JsonPropertyName("promptTokenCount")] long PromptTokenCount,
    [property: JsonPropertyName("candidatesTokenCount")] long CandidatesTokenCount,
    [property: JsonPropertyName("thoughtsTokenCount")] long ThoughtsTokenCount);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiChatResponse))]
[JsonSerializable(typeof(AnthropicChatRequest))]
[JsonSerializable(typeof(AnthropicChatResponse))]
[JsonSerializable(typeof(GeminiGenerateRequest))]
[JsonSerializable(typeof(GeminiGenerateResponse))]
internal sealed partial class ProviderJsonContext : JsonSerializerContext;
