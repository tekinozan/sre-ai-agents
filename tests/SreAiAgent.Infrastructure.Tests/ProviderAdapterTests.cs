using System.Diagnostics;
using System.Net;
using System.Text.Json;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Infrastructure.Llm.Providers;

namespace SreAiAgent.Infrastructure.Tests;

public sealed class ProviderAdapterTests
{
    private static readonly JsonElement EmptySchema = JsonDocument.Parse("{}").RootElement;

    private static ProviderChatRequest Request() => new(
        "system", "user", "model-1", EmptySchema, 0.5f, 1000, 42, new Dictionary<string, string>());

    public static TheoryData<ProviderAdapterKind> HttpAdapters() => new()
    {
        ProviderAdapterKind.OpenAi,
        ProviderAdapterKind.OpenAiCompatible,
        ProviderAdapterKind.AzureOpenAi,
        ProviderAdapterKind.Anthropic,
        ProviderAdapterKind.Gemini
    };

    private static IProviderChatClient CreateAdapter(ProviderAdapterKind kind, HttpMessageHandler handler, out ProviderConnectionRevision revision)
    {
        revision = ProviderConnectionRevision.Restore(
            ProviderConnectionRevisionId.New(), ProviderConnectionId.New(), "tenant", 1, kind,
            "https://test.local", null, "2024-01-01", null, "cred-1", ModelCapability.None,
            "hash", DateTimeOffset.UnixEpoch, "actor");

        var clients = new StubHttpClientFactory(handler, "https://test.local/");
        var credentials = Substitute.For<ICredentialResolver>();
        credentials.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CredentialValue("secret"));

        return kind switch
        {
            ProviderAdapterKind.OpenAi => new OpenAiProviderChatClient(clients, credentials, revision),
            ProviderAdapterKind.OpenAiCompatible => new OpenAiCompatibleProviderChatClient(clients, credentials, revision),
            ProviderAdapterKind.AzureOpenAi => new AzureOpenAiProviderChatClient(clients, credentials, revision),
            ProviderAdapterKind.Anthropic => new AnthropicProviderChatClient(clients, credentials, revision),
            ProviderAdapterKind.Gemini => new GeminiProviderChatClient(clients, credentials, revision),
            _ => throw new UnreachableException()
        };
    }

    private static string SuccessBody(ProviderAdapterKind kind, string text)
    {
        if (kind == ProviderAdapterKind.Gemini)
        {
            return $$"""
            {
                "responseId": "req-1",
                "modelVersion": "gemini-2.5-pro",
                "candidates": [{"content": {"role": "model", "parts": [{"text": {{JsonSerializer.Serialize(text)}}}]}, "finishReason": "STOP"}],
                "usageMetadata": {"promptTokenCount": 10, "candidatesTokenCount": 20}
            }
            """;
        }

        if (kind == ProviderAdapterKind.Anthropic)
        {
            return $$"""
            {
                "id": "req-1",
                "model": "claude-3-haiku",
                "stop_reason": "end_turn",
                "content": [{"type": "text", "text": "{{text}}"}],
                "usage": {"input_tokens": 10, "output_tokens": 20}
            }
            """;
        }
        else
        {
            return $$"""
            {
                "id": "req-1",
                "model": "gpt-4",
                "choices": [{"message": {"content": {{JsonSerializer.Serialize(text)}}}, "finish_reason": "stop"}],
                "usage": {"prompt_tokens": 10, "completion_tokens": 20}
            }
            """;
        }
    }

    [Theory]
    [MemberData(nameof(HttpAdapters))]
    public async Task AStructured200MapsEveryOutputField(ProviderAdapterKind kind)
    {
        var handler = new StubHttpMessageHandler(SuccessBody(kind, "hello"));
        var adapter = CreateAdapter(kind, handler, out _);

        var result = await adapter.GetResponseAsync(Request(), TestContext.Current.CancellationToken);

        var response = result.Value;
        response.Text.ShouldBe("hello");
        response.InputTokens.ShouldBe(10);
        response.OutputTokens.ShouldBe(20);
        response.RequestId.ShouldBe("req-1");
        response.ObservedModel.ShouldBe(kind switch
        {
            ProviderAdapterKind.Anthropic => "claude-3-haiku",
            ProviderAdapterKind.Gemini => "gemini-2.5-pro",
            _ => "gpt-4",
        });
        response.FinishReason.ShouldBe(kind switch
        {
            ProviderAdapterKind.Anthropic => "end_turn",
            ProviderAdapterKind.Gemini => "STOP",
            _ => "stop",
        });
    }

    [Theory]
    [MemberData(nameof(HttpAdapters))]
    public async Task StatusCodesMapToTheErrorTaxonomy(ProviderAdapterKind kind)
    {
        var cases = new (HttpStatusCode Status, string ExpectedCode)[]
        {
            (HttpStatusCode.Unauthorized, ApplicationErrorCodes.LlmAuthenticationFailed),
            (HttpStatusCode.TooManyRequests, ApplicationErrorCodes.LlmRateLimited),
            (HttpStatusCode.ServiceUnavailable, ApplicationErrorCodes.LlmUnavailable),
            (HttpStatusCode.BadRequest, ApplicationErrorCodes.LlmInvalidRequest)
        };

        foreach (var (status, code) in cases)
        {
            var handler = new StubHttpMessageHandler("{}", status);
            var adapter = CreateAdapter(kind, handler, out _);

            var result = await adapter.GetResponseAsync(Request(), TestContext.Current.CancellationToken);

            result.IsSuccess.ShouldBeFalse($"Expected {status} to fail.");
            result.Error.Code.ToString().ShouldBe(code);
        }
    }

    [Theory]
    [MemberData(nameof(HttpAdapters))]
    public async Task A200WithNullContentYieldsEmptyTextWithUsagePreserved(ProviderAdapterKind kind)
    {
        string body = kind == ProviderAdapterKind.Gemini
            ? """
              {
                  "responseId": "req-1",
                  "modelVersion": "gemini",
                  "candidates": [{"content": {"parts": []}, "finishReason": "STOP"}],
                  "usageMetadata": {"promptTokenCount": 5, "candidatesTokenCount": 0}
              }
              """
            : kind == ProviderAdapterKind.Anthropic
            ? """
              {
                  "id": "req-1",
                  "model": "claude",
                  "stop_reason": "end_turn",
                  "content": [],
                  "usage": {"input_tokens": 5, "output_tokens": 0}
              }
              """
            : """
              {
                  "id": "req-1",
                  "model": "gpt-4",
                  "choices": [{"message": {"content": null}, "finish_reason": "stop"}],
                  "usage": {"prompt_tokens": 5, "completion_tokens": 0}
              }
              """;

        var handler = new StubHttpMessageHandler(body);
        var adapter = CreateAdapter(kind, handler, out _);

        var result = await adapter.GetResponseAsync(Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldBeEmpty();
        result.Value.InputTokens.ShouldBe(5);
        result.Value.OutputTokens.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(HttpAdapters))]
    public async Task TheOutboundRequestNeverCarriesTheCredentialInALoggedOrSerializedForm(ProviderAdapterKind kind)
    {
        var handler = new StubHttpMessageHandler(SuccessBody(kind, "hello"));
        var adapter = CreateAdapter(kind, handler, out _);

        await adapter.GetResponseAsync(Request(), TestContext.Current.CancellationToken);

        HttpRequestMessage sent = handler.RequestsMessage.Single();
        string contentString = handler.RequestBodies.Single();

        // The credential is transmitted in a header, so a lookup by name must
        // find it; a naive log of the request URI or the request body must not.
        // HttpRequestMessage.ToString() includes headers by design and cannot be
        // relied on to redact them, so the body is the assertion boundary.
        sent.RequestUri!.ToString().ShouldNotContain("secret");
        contentString.ShouldNotContain("secret");

        if (kind == ProviderAdapterKind.AzureOpenAi)
        {
            sent.Headers.GetValues("api-key").Single().ShouldBe("secret");
        }
        else if (kind == ProviderAdapterKind.Anthropic)
        {
            sent.Headers.GetValues("x-api-key").Single().ShouldBe("secret");
        }
        else if (kind == ProviderAdapterKind.Gemini)
        {
            // In a header, never as the ?key= query parameter Google also
            // accepts: a URL is what proxies and access logs keep.
            sent.Headers.GetValues("x-goog-api-key").Single().ShouldBe("secret");
        }
        else
        {
            sent.Headers.Authorization!.Parameter.ShouldBe("secret");
        }
    }

    private static readonly JsonElement AnswerSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""").RootElement;

    private static ProviderChatRequest DialectRequest(params (string Name, string Value)[] parameters) => new(
        "system", "user", "model-1", AnswerSchema, 0f, 1000, null,
        parameters.ToDictionary(static item => item.Name, static item => item.Value, StringComparer.Ordinal));

    private static async Task<JsonElement> SentBodyAsync(ProviderAdapterKind kind, ProviderChatRequest request)
    {
        var handler = new StubHttpMessageHandler(SuccessBody(kind, "{}"));
        var adapter = CreateAdapter(kind, handler, out _);

        (await adapter.GetResponseAsync(request, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        return JsonDocument.Parse(handler.RequestBodies.Single()).RootElement;
    }

    [Fact]
    public async Task OpenAiSendsTheTokenLimitAsMaxCompletionTokens()
    {
        // OpenAI deprecated max_tokens and its reasoning models reject it with a
        // 400, which the gateway reports as a permanent invalid request (D45).
        JsonElement body = await SentBodyAsync(ProviderAdapterKind.OpenAi, DialectRequest());

        body.GetProperty("max_completion_tokens").GetInt32().ShouldBe(1000);
        body.TryGetProperty("max_tokens", out _).ShouldBeFalse();
        body.GetProperty("response_format").GetProperty("type").GetString().ShouldBe("json_schema");
        body.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean().ShouldBeTrue();
        body.GetProperty("temperature").GetSingle().ShouldBe(0f);
    }

    [Theory]
    [InlineData(ProviderAdapterKind.OpenAiCompatible)]
    [InlineData(ProviderAdapterKind.AzureOpenAi)]
    public async Task CompatibleServersKeepMaxTokensUnlessTheDeploymentSaysOtherwise(ProviderAdapterKind kind)
    {
        JsonElement defaults = await SentBodyAsync(kind, DialectRequest());
        defaults.GetProperty("max_tokens").GetInt32().ShouldBe(1000);
        defaults.TryGetProperty("max_completion_tokens", out _).ShouldBeFalse();

        JsonElement overridden = await SentBodyAsync(kind, DialectRequest(("max_tokens_field", "max_completion_tokens")));
        overridden.GetProperty("max_completion_tokens").GetInt32().ShouldBe(1000);
        overridden.TryGetProperty("max_tokens", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task JsonObjectModeCarriesTheSchemaInThePromptInsteadOfTheResponseFormat()
    {
        // DeepSeek, among others, accepts json_object and rejects json_schema.
        JsonElement body = await SentBodyAsync(
            ProviderAdapterKind.OpenAiCompatible, DialectRequest(("response_format", "json_object")));

        JsonElement format = body.GetProperty("response_format");
        format.GetProperty("type").GetString().ShouldBe("json_object");
        format.TryGetProperty("json_schema", out _).ShouldBeFalse();

        string system = body.GetProperty("messages")[0].GetProperty("content").GetString()!;
        system.ShouldStartWith("system");
        system.ShouldContain("JSON Schema");
        system.ShouldContain("\"required\":[\"answer\"]");
    }

    [Fact]
    public async Task ADeploymentCanOmitTheTemperature()
    {
        // Reasoning models accept only their default temperature.
        JsonElement body = await SentBodyAsync(ProviderAdapterKind.OpenAi, DialectRequest(("temperature", "omit")));

        body.TryGetProperty("temperature", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ProviderAdapterKind.OpenAiCompatible, "response_format", "json_object", null)]
    [InlineData(ProviderAdapterKind.OpenAi, "max_tokens_field", "max_completion_tokens", null)]
    [InlineData(ProviderAdapterKind.OpenAiCompatible, "response_format", "yaml", "must be one of")]
    [InlineData(ProviderAdapterKind.Anthropic, "response_format", "json_object", "only to OpenAI-compatible")]
    [InlineData(ProviderAdapterKind.Scripted, "temperature", "omit", "only to OpenAI-compatible")]
    public void DialectParametersAreCheckedAgainstTheAdapterWhenTheDeploymentIsCreated(
        ProviderAdapterKind kind, string name, string value, string? problem)
    {
        string? result = OpenAiDialect.Validate(
            kind, new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value });

        if (problem is null)
        {
            result.ShouldBeNull();
        }
        else
        {
            result.ShouldNotBeNull().ShouldContain(problem);
        }
    }

    [Fact]
    public async Task GeminiSendsItsOwnShapeAndCountsThinkingTokensAsOutput()
    {
        var handler = new StubHttpMessageHandler("""
            {
                "responseId": "resp-9",
                "modelVersion": "gemini-2.5-pro-002",
                "candidates": [{"content": {"parts": [{"text": "{\"answer\":"}, {"text": "\"ok\"}"}]}, "finishReason": "STOP"}],
                "usageMetadata": {"promptTokenCount": 100, "candidatesTokenCount": 40, "thoughtsTokenCount": 60}
            }
            """);
        var adapter = CreateAdapter(ProviderAdapterKind.Gemini, handler, out _);

        var result = await adapter.GetResponseAsync(DialectRequest(), TestContext.Current.CancellationToken);

        // Thinking tokens are billed as output; leaving them out under-reported
        // every thinking model's cost and therefore every budget (B).
        result.Value.Text.ShouldBe("{\"answer\":\"ok\"}");
        result.Value.InputTokens.ShouldBe(100);
        result.Value.OutputTokens.ShouldBe(100);
        result.Value.ObservedProvider.ShouldBe("gemini");

        HttpRequestMessage sent = handler.RequestsMessage.Single();
        // The revision pins the API version; the shared test revision pins 2024-01-01.
        sent.RequestUri!.AbsolutePath.ShouldEndWith("/2024-01-01/models/model-1:generateContent");
        sent.RequestUri.Query.ShouldBeEmpty();

        JsonElement body = JsonDocument.Parse(handler.RequestBodies.Single()).RootElement;
        body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("system");
        body.GetProperty("contents")[0].GetProperty("role").GetString().ShouldBe("user");
        JsonElement config = body.GetProperty("generationConfig");
        config.GetProperty("responseMimeType").GetString().ShouldBe("application/json");
        config.GetProperty("responseJsonSchema").GetProperty("required")[0].GetString().ShouldBe("answer");
        config.GetProperty("maxOutputTokens").GetInt32().ShouldBe(1000);
        config.TryGetProperty("seed", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AGeminiResponseCutAtTheTokenLimitReportsAFinishReasonTheGatewayTreatsAsTruncation()
    {
        var handler = new StubHttpMessageHandler("""
            {"candidates": [{"content": {"parts": [{"text": "{"}]}, "finishReason": "MAX_TOKENS"}],
             "usageMetadata": {"promptTokenCount": 1, "candidatesTokenCount": 1}}
            """);
        var adapter = CreateAdapter(ProviderAdapterKind.Gemini, handler, out _);

        var result = await adapter.GetResponseAsync(Request(), TestContext.Current.CancellationToken);

        // LlmGateway matches "max_tokens" ignoring case.
        result.Value.FinishReason.ShouldBe("MAX_TOKENS", StringCompareShould.IgnoreCase);
        string.Equals(result.Value.FinishReason, "max_tokens", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
    }

    [Theory]
    [InlineData("response_format", "json_object")]
    [InlineData("max_tokens_field", "max_tokens")]
    public void TheOpenAiDialectIsRefusedOnGemini(string name, string value)
        => OpenAiDialect.Validate(
                ProviderAdapterKind.Gemini, new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value })
            .ShouldNotBeNull();

    [Fact]
    public void AGeminiRevisionWithoutAPinnedVersionUsesV1Beta()
    {
        ProviderConnectionRevision unpinned = ProviderConnectionRevision.Restore(
            ProviderConnectionRevisionId.New(), ProviderConnectionId.New(), "tenant", 1, ProviderAdapterKind.Gemini,
            "https://generativelanguage.googleapis.com", null, null, null, "cred-1", ModelCapability.None,
            "hash", DateTimeOffset.UnixEpoch, "actor");

        GeminiProviderChatClient.VersionOf(unpinned).ShouldBe("v1beta");
    }
}
