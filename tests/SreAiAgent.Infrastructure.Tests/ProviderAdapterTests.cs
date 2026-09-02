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
        ProviderAdapterKind.Anthropic
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
            _ => throw new UnreachableException()
        };
    }

    private static string SuccessBody(ProviderAdapterKind kind, string text)
    {
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
        response.ObservedModel.ShouldBe(kind == ProviderAdapterKind.Anthropic ? "claude-3-haiku" : "gpt-4");
        response.FinishReason.ShouldBe(kind == ProviderAdapterKind.Anthropic ? "end_turn" : "stop");
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
        string body = kind == ProviderAdapterKind.Anthropic
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
        else
        {
            sent.Headers.Authorization!.Parameter.ShouldBe("secret");
        }
    }
}
