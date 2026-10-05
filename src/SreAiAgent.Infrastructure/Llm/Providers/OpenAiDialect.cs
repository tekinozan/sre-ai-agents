using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Domain.ModelProviders;

namespace SreAiAgent.Infrastructure.Llm.Providers;

/// <summary>
/// The per-deployment differences between servers that all speak the OpenAI
/// chat-completions shape (docs/DECISIONS.md D45).
/// </summary>
/// <remarks>
/// The shape is shared; the details are not. OpenAI's reasoning models reject
/// <c>max_tokens</c> and any temperature but the default; DeepSeek accepts
/// <c>json_object</c> but not <c>json_schema</c>; Groq, Qwen and Gemini's
/// compatible endpoint vary by model. Guessing from a model name is what this
/// platform refuses to do (ADP-302), so the deployment states it, as allowlisted
/// provider parameters that are part of its immutable configuration hash. A
/// value the adapter does not recognise is refused at creation rather than
/// quietly dropped at call time.
/// </remarks>
internal static class OpenAiDialect
{
    public const string MaxTokensField = "max_tokens_field";
    public const string ResponseFormat = "response_format";
    public const string Temperature = "temperature";

    private const string MaxTokens = "max_tokens";
    private const string MaxCompletionTokens = "max_completion_tokens";
    private const string JsonSchema = "json_schema";
    private const string JsonObject = "json_object";
    private const string Send = "send";
    private const string Omit = "omit";

    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        [MaxTokensField] = [MaxTokens, MaxCompletionTokens],
        [ResponseFormat] = [JsonSchema, JsonObject],
        [Temperature] = [Send, Omit],
    };

    /// <summary>The parameter names this dialect owns.</summary>
    public static IReadOnlyCollection<string> Names => Allowed.Keys;

    /// <summary>
    /// Returns why <paramref name="parameters"/> are not valid for
    /// <paramref name="adapterKind"/>, or <c>null</c> when they are.
    /// </summary>
    public static string? Validate(ProviderAdapterKind adapterKind, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        foreach ((string name, string value) in parameters)
        {
            if (!Allowed.TryGetValue(name, out string[]? values))
            {
                continue;
            }

            if (!SpeaksChatCompletions(adapterKind))
            {
                return $"The '{name}' parameter applies only to OpenAI-compatible adapters.";
            }

            if (!values.Contains(value, StringComparer.Ordinal))
            {
                return $"The '{name}' parameter must be one of: {string.Join(", ", values)}.";
            }
        }

        return null;
    }

    /// <summary>Builds the request body for one call.</summary>
    /// <param name="request">The gateway's request.</param>
    /// <param name="adapterKind">Decides the default token field when the deployment names none.</param>
    public static OpenAiChatRequest Build(ProviderChatRequest request, ProviderAdapterKind adapterKind)
    {
        ArgumentNullException.ThrowIfNull(request);

        // OpenAI's own API deprecated max_tokens and its reasoning models reject
        // it; servers that copy the shape mostly still expect it. Azure follows
        // its API version, so it stays on the older field unless told otherwise.
        string tokenField = Value(request, MaxTokensField)
            ?? (adapterKind == ProviderAdapterKind.OpenAi ? MaxCompletionTokens : MaxTokens);
        bool schemaMode = !string.Equals(Value(request, ResponseFormat), JsonObject, StringComparison.Ordinal);
        bool sendTemperature = !string.Equals(Value(request, Temperature), Omit, StringComparison.Ordinal);

        // In json_object mode the server guarantees JSON but not this schema, so
        // the schema travels in the prompt. The gateway still validates the
        // answer against it, so the guarantee is the platform's, not the model's.
        // OpenAI also refuses json_object unless a message mentions JSON.
        string system = schemaMode
            ? request.SystemPrompt
            : request.SystemPrompt
              + "\n\nRespond with exactly one JSON object that conforms to this JSON Schema:\n"
              + request.JsonSchema.GetRawText();

        return new OpenAiChatRequest(
            request.ProviderModel,
            [new ProviderMessage("system", system), new ProviderMessage("user", request.UserPrompt)],
            sendTemperature ? request.Temperature : null,
            string.Equals(tokenField, MaxTokens, StringComparison.Ordinal) ? request.MaxOutputTokens : null,
            string.Equals(tokenField, MaxCompletionTokens, StringComparison.Ordinal) ? request.MaxOutputTokens : null,
            schemaMode
                ? new OpenAiResponseFormat(JsonSchema, new OpenAiJsonSchema("sre_response", Strict: true, request.JsonSchema))
                : new OpenAiResponseFormat(JsonObject, JsonSchema: null),
            request.Seed);
    }

    private static bool SpeaksChatCompletions(ProviderAdapterKind adapterKind)
        => adapterKind is ProviderAdapterKind.OpenAi or ProviderAdapterKind.OpenAiCompatible or ProviderAdapterKind.AzureOpenAi;

    private static string? Value(ProviderChatRequest request, string name)
        => request.ProviderParameters.TryGetValue(name, out string? value) ? value : null;
}
