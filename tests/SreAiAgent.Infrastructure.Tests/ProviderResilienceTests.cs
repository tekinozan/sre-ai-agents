using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly.CircuitBreaker;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Audit;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm;
using SreAiAgent.Infrastructure.Llm.Providers;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Proves outbound model HTTP client resilience: per-attempt timeouts,
/// single retry budget split between HTTP pipeline and LlmGateway, Retry-After adherence,
/// and circuit-breaker behavior (ARCHITECTURE.md 11.3, 14).
/// </summary>
public sealed class ProviderResilienceTests
{
    private const string SuccessChatJson = """
        {
            "id": "req-resilience-1",
            "model": "gpt-4o",
            "choices": [{"message": {"content": "{\"status\":\"ok\"}"}, "finish_reason": "stop"}],
            "usage": {"prompt_tokens": 10, "completion_tokens": 20}
        }
        """;

    // Matches ProviderResilienceLogging.LogModelHttpRetryExhausted's [LoggerMessage] EventId.
    private const int RetryExhaustedEventId = 2004;

    // Matches ProviderResilienceLogging.LogModelCircuitBreakerOpened's [LoggerMessage] EventId.
    private const int CircuitBreakerOpenedEventId = 2003;

    private static readonly JsonElement EmptySchema = JsonDocument.Parse("{}").RootElement;

    private static ProviderChatRequest CreateChatRequest() => new(
        "system", "user", "gpt-4o", EmptySchema, 0.5f, 1000, 42, new Dictionary<string, string>(StringComparer.Ordinal));

    private static ProviderConnectionRevision CreateRevision() => ProviderConnectionRevision.Restore(
        ProviderConnectionRevisionId.New(),
        ProviderConnectionId.New(),
        "test-tenant",
        1,
        ProviderAdapterKind.OpenAi,
        "https://api.openai.com/v1",
        null, null, null,
        "file://test-key",
        ModelCapability.StructuredOutput | ModelCapability.SystemMessage,
        "hash",
        DateTimeOffset.UnixEpoch,
        "test-actor");

    private static PinnedModelCandidate CreateCandidate(string tenant, string slot) => new()
    {
        Tenant = tenant,
        AssignmentReleaseId = new ModelAssignmentReleaseId(Guid.CreateVersion7()),
        AssignmentConfigurationHash = "assign-hash",
        Slot = slot,
        DeploymentVersionId = new ModelDeploymentVersionId(Guid.CreateVersion7()),
        DeploymentLogicalName = slot,
        DeploymentVersion = 1,
        DeploymentConfigurationHash = "deploy-hash",
        ProviderConnectionRevisionId = new ProviderConnectionRevisionId(Guid.CreateVersion7()),
        ProviderConnectionRevision = 1,
        ProviderConfigurationHash = "conn-hash",
        AdapterKind = ProviderAdapterKind.OpenAi,
        ProviderModel = "gpt-4o",
        Capabilities = ModelCapability.None,
        Temperature = 0.5f,
        MaxOutputTokens = 1000,
        Timeout = TimeSpan.FromSeconds(30),
        InputCostPerMillionTokens = 1m,
        OutputCostPerMillionTokens = 2m,
        Currency = "USD",
        ProviderParameters = new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static LlmCallMetadata CreateMetadata() => new()
    {
        IncidentId = IncidentId.New(),
        AgentName = "classifier",
        PromptName = "classifier.v1",
        PromptVersion = "1",
        PromptHash = "hash-1",
        Tenant = "test-tenant",
        CorrelationId = "corr-1",
    };

    private sealed class StubCredentialResolver(string secret = "test-key") : ICredentialResolver
    {
        public ValueTask<Result<CredentialValue>> ResolveAsync(string credentialReference, CancellationToken cancellationToken)
            => ValueTask.FromResult(Result<CredentialValue>.Success(new CredentialValue(secret)));
    }

    /// <summary>Captures <see cref="ILogger"/> event ids without a logging provider package.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<int> _eventIds = [];

        public IReadOnlyList<int> EventIds
        {
            get { lock (_eventIds) { return [.. _eventIds]; } }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._eventIds)
                {
                    owner._eventIds.Add(eventId.Id);
                }
            }
        }
    }

    /// <summary>Simulates a transport-level failure that never reaches an HTTP response.</summary>
    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new HttpRequestException("Simulated transport failure.");
        }
    }

    private static ServiceProvider BuildServiceProvider(
        HttpMessageHandler stubHandler,
        Action<HttpStandardResilienceOptions>? configureResilience = null,
        int requestTimeoutSeconds = 5,
        ILoggerProvider? loggerProvider = null)
    {
        ServiceCollection services = new();

        services.AddLogging(builder =>
        {
            if (loggerProvider is not null)
            {
                builder.AddProvider(loggerProvider);
            }
        });
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new LlmOptions
        {
            RequestTimeoutSeconds = requestTimeoutSeconds,
            MaxRetries = 2,
        }));
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(environment);
        services.AddSingleton<ICredentialResolver>(new StubCredentialResolver());

        services.AddSreLlm();

        services.AddHttpClient(ProviderHttpClients.Chat)
            .ConfigurePrimaryHttpMessageHandler(() => stubHandler);

        if (configureResilience is not null)
        {
            // Layers test-specific overrides onto the ONE pipeline AddSreLlm already
            // registered by name, rather than calling AddStandardResilienceHandler a
            // second time: that would stack a second ResilienceHandler on the named
            // client, doubling every retry, timeout and circuit-breaker decision instead
            // of tuning the production pipeline under test.
            services.Configure(ProviderHttpClients.ChatResiliencePipelineName, configureResilience);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task RateLimitWithRetryAfterRetriesAndSucceedsWithExactRequestCountTwo()
    {
        int callCount = 0;
        StubHttpMessageHandler stub = new(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                HttpResponseMessage rateLimited = new(HttpStatusCode.TooManyRequests);
                rateLimited.Headers.TryAddWithoutValidation("Retry-After", "0");
                return rateLimited;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessChatJson, System.Text.Encoding.UTF8, "application/json"),
            };
        });

        await using ServiceProvider provider = BuildServiceProvider(stub);
        IHttpClientFactory clients = provider.GetRequiredService<IHttpClientFactory>();
        ICredentialResolver credentials = new StubCredentialResolver();

        OpenAiProviderChatClient client = new(clients, credentials, CreateRevision());

        Result<ProviderChatResponse> response = await client.GetResponseAsync(
            CreateChatRequest(), TestContext.Current.CancellationToken);

        response.IsSuccess.ShouldBeTrue();
        callCount.ShouldBe(2, "The HTTP resilience pipeline must consume Retry-After: 0 and retry once.");
        stub.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Repeated503ExhaustsHttpPipelineAndSurfacesUnavailableWithoutGatewayRetryCycle()
    {
        int callCount = 0;
        StubHttpMessageHandler stub = new(request =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":\"overloaded\"}", System.Text.Encoding.UTF8, "application/json"),
            };
        });

        // A 503 carries no Retry-After, so the pipeline falls back to its own backoff.
        // Fixed, jitter-free delays keep the attempt count AND elapsed time deterministic
        // instead of depending on the production 2s-exponential default.
        await using ServiceProvider provider = BuildServiceProvider(stub, configureResilience: options =>
        {
            options.Retry.Delay = TimeSpan.FromMilliseconds(20);
            options.Retry.BackoffType = Polly.DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });
        IHttpClientFactory clients = provider.GetRequiredService<IHttpClientFactory>();
        ICredentialResolver credentials = new StubCredentialResolver();

        OpenAiProviderChatClient client = new(clients, credentials, CreateRevision());

        Stopwatch sw = Stopwatch.StartNew();
        Result<ProviderChatResponse> response = await client.GetResponseAsync(
            CreateChatRequest(), TestContext.Current.CancellationToken);
        sw.Stop();

        response.IsFailure.ShouldBeTrue();
        response.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.LlmUnavailable.ToString());
        callCount.ShouldBe(4, "Standard resilience pipeline performs 1 initial attempt + 3 retries = 4 attempts total.");
        stub.Requests.Count.ShouldBe(4);
        sw.Elapsed.ShouldBeGreaterThanOrEqualTo(
            TimeSpan.FromMilliseconds(50), "Three 20ms backoffs must actually elapse between the four attempts.");
        sw.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(2), "The pipeline must give up promptly once its bounded retries are spent.");
    }

    [Fact]
    public async Task Repeated429ExhaustsHttpPipelineAndSurfacesRateLimited()
    {
        int callCount = 0;
        StubHttpMessageHandler stub = new(request =>
        {
            callCount++;
            HttpResponseMessage msg = new(HttpStatusCode.TooManyRequests);
            msg.Headers.TryAddWithoutValidation("Retry-After", "0");
            return msg;
        });

        await using ServiceProvider provider = BuildServiceProvider(stub);
        IHttpClientFactory clients = provider.GetRequiredService<IHttpClientFactory>();
        ICredentialResolver credentials = new StubCredentialResolver();

        OpenAiProviderChatClient client = new(clients, credentials, CreateRevision());

        Stopwatch sw = Stopwatch.StartNew();
        Result<ProviderChatResponse> response = await client.GetResponseAsync(
            CreateChatRequest(), TestContext.Current.CancellationToken);
        sw.Stop();

        response.IsFailure.ShouldBeTrue();
        response.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.LlmRateLimited.ToString());
        callCount.ShouldBe(4, "Standard resilience pipeline performs 1 initial attempt + 3 retries = 4 attempts total.");
        stub.Requests.Count.ShouldBe(4);
        sw.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(2), "Retry-After: 0 must be honoured instead of falling back to exponential backoff.");
    }

    [Fact]
    public async Task HugeRetryAfterDoesNotSleepIndefinitelyAndIsBoundedByCallerTimeout()
    {
        StubHttpMessageHandler stub = new(request =>
        {
            HttpResponseMessage msg = new(HttpStatusCode.TooManyRequests);
            // Huge Retry-After header: pipeline must respect CTS timeout rather than sleeping forever
            msg.Headers.TryAddWithoutValidation("Retry-After", "3600");
            return msg;
        });

        await using ServiceProvider provider = BuildServiceProvider(stub);
        IHttpClientFactory clients = provider.GetRequiredService<IHttpClientFactory>();
        ICredentialResolver credentials = new StubCredentialResolver();

        OpenAiProviderChatClient client = new(clients, credentials, CreateRevision());

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
        Stopwatch sw = Stopwatch.StartNew();

        Func<Task> act = async () => await client.GetResponseAsync(CreateChatRequest(), cts.Token);
        await act.ShouldThrowAsync<OperationCanceledException>();

        sw.Stop();
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CircuitBreakerOpensUnderControlledFailureRatio()
    {
        int callCount = 0;
        bool circuitOpened = false;

        StubHttpMessageHandler stub = new(request =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        CapturingLoggerProvider logs = new();
        await using ServiceProvider provider = BuildServiceProvider(
            stub,
            configureResilience: options =>
            {
                // Retries disabled: each GetResponseAsync call below must correspond to
                // exactly one HTTP attempt, so the circuit trips on a known call and the
                // "third call blocked" assertion is deterministic rather than depending on
                // how many attempts the (unconfigured) default retry count would spend
                // inside a single call.
                options.Retry.ShouldHandle = static _ => ValueTask.FromResult(false);
                options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(200);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromMilliseconds(500); // 500ms >= 2 * 200ms
                options.CircuitBreaker.MinimumThroughput = 2;
                options.CircuitBreaker.FailureRatio = 0.5;
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(10);

                // Wrap, not replace: AddSreLlm's own Configure (applied before this one, since
                // named-options configure delegates run in registration order) already assigned
                // OnOpened to log EventId 2003. Overwriting it here would silence that real
                // operational signal instead of merely observing the transition alongside it.
                Func<OnCircuitOpenedArguments<HttpResponseMessage>, ValueTask> productionOnOpened =
                    options.CircuitBreaker.OnOpened
                    ?? throw new InvalidOperationException(
                        "AddSreLlm must assign CircuitBreaker.OnOpened before this test's override runs.");
                options.CircuitBreaker.OnOpened = async args =>
                {
                    await productionOnOpened(args).ConfigureAwait(false);
                    circuitOpened = true;
                };
            },
            loggerProvider: logs);

        IHttpClientFactory clients = provider.GetRequiredService<IHttpClientFactory>();
        ICredentialResolver credentials = new StubCredentialResolver();

        OpenAiProviderChatClient client = new(clients, credentials, CreateRevision());

        // First call fails with 503 (one attempt: retries are disabled above)
        await client.GetResponseAsync(CreateChatRequest(), TestContext.Current.CancellationToken);
        callCount.ShouldBe(1);
        // Second call fails with 503 (tripping failure ratio 1.0 >= 0.5, throughput 2 >= 2)
        await client.GetResponseAsync(CreateChatRequest(), TestContext.Current.CancellationToken);
        callCount.ShouldBe(2);

        circuitOpened.ShouldBeTrue("Circuit breaker must transition to Open after meeting failure ratio and throughput threshold.");
        logs.EventIds.Count(static id => id == CircuitBreakerOpenedEventId).ShouldBe(
            1, "The production OnOpened callback (EventId 2003) must still fire exactly once, not be bypassed by the test's own hook.");

        // Third call immediately rejected by open circuit without hitting stub
        Result<ProviderChatResponse> rejected = await client.GetResponseAsync(CreateChatRequest(), TestContext.Current.CancellationToken);
        rejected.IsFailure.ShouldBeTrue();
        callCount.ShouldBe(2, "Third call must be blocked by the open circuit without sending an HTTP request.");
    }

    [Fact]
    public async Task RepeatedTransportExceptionExhaustsHttpPipelineAndLogsRetryExhaustionExactlyOnce()
    {
        ThrowingHttpMessageHandler stub = new();
        CapturingLoggerProvider logs = new();
        await using ServiceProvider provider = BuildServiceProvider(stub, loggerProvider: logs);
        IHttpClientFactory clients = provider.GetRequiredService<IHttpClientFactory>();
        ICredentialResolver credentials = new StubCredentialResolver();

        OpenAiProviderChatClient client = new(clients, credentials, CreateRevision());

        Result<ProviderChatResponse> response = await client.GetResponseAsync(
            CreateChatRequest(), TestContext.Current.CancellationToken);

        response.IsFailure.ShouldBeTrue();
        response.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.LlmUnavailable.ToString());
        stub.CallCount.ShouldBe(4, "Standard resilience pipeline performs 1 initial attempt + 3 retries = 4 attempts "
            + "total, even when every attempt fails with a transport exception rather than a response.");
        logs.EventIds.Count(static id => id == RetryExhaustedEventId).ShouldBe(
            1, "A transport exception that keeps failing after every retry must still report exhaustion exactly "
                + "once: Outcome.Result is null for an exception outcome, so RecordRetry must reach the current "
                + "attempt's request some other way.");
    }

    [Fact]
    public async Task LlmGatewayMakesNoOutboundCallOnceTheTenantBudgetIsSpent()
    {
        // gemini-budgets-notifications.md B: an exhausted budget means no call
        // reaches the provider at all, and the refusal is still audited.
        IChatClientResolver resolver = Substitute.For<IChatClientResolver>();
        IAuditSink audit = Substitute.For<IAuditSink>();
        ITenantBudgetGuard spent = Substitute.For<ITenantBudgetGuard>();
        spent.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(Error.Permanent(ApplicationErrorCodes.BudgetExceeded, "Spent.")));

        IMeterFactory meterFactory = Substitute.For<IMeterFactory>();
        meterFactory.Create(Arg.Any<MeterOptions>()).Returns(new Meter("test"));
        LlmGateway gateway = new(
            resolver,
            new StructuredOutputValidator(),
            new SreMetrics(meterFactory),
            Microsoft.Extensions.Options.Options.Create(new LlmOptions { MaxRetries = 2, RequestTimeoutSeconds = 5 }),
            audit,
            spent,
            TimeProvider.System,
            NullLogger<LlmGateway>.Instance);

        Result<LlmResult<ClassificationPayload>> result = await gateway.GenerateStructuredAsync(
            new LlmRequest<ClassificationPayload>
            {
                Candidate = CreateCandidate("test-tenant", "classifier"),
                ResponseSchemaName = StructuredOutputValidator.ClassificationSchemaName,
                SystemPrompt = "system prompt",
                UserPrompt = "user prompt",
                Timeout = TimeSpan.FromSeconds(5),
                Metadata = CreateMetadata(),
            },
            TestContext.Current.CancellationToken);

        result.Error.Code.ShouldBe(ApplicationErrorCodes.BudgetExceeded);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<PinnedModelCandidate>(), Arg.Any<CancellationToken>());
        audit.Received(1).RecordLlmCall(Arg.Is<LlmCallAudit>(call =>
            call.ErrorCode == ApplicationErrorCodes.BudgetExceeded && call.EstimatedCost == 0m));
    }

    [Fact]
    public async Task LlmGatewayDoesNotRetryTransportErrorsReturnedByClient()
    {
        int callCount = 0;
        IProviderChatClient mockClient = Substitute.For<IProviderChatClient>();
        mockClient.GetResponseAsync(Arg.Any<ProviderChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult(Result<ProviderChatResponse>.Failure(
                    Error.Transient(ApplicationErrorCodes.LlmUnavailable, "The model provider is unavailable.")));
            });

        IChatClientResolver resolver = Substitute.For<IChatClientResolver>();
        resolver.ResolveAsync(Arg.Any<PinnedModelCandidate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IProviderChatClient>.Success(mockClient)));

        StructuredOutputValidator validator = new();
        IMeterFactory meterFactory = Substitute.For<IMeterFactory>();
        meterFactory.Create(Arg.Any<MeterOptions>()).Returns(new Meter("test"));
        SreMetrics metrics = new(meterFactory);
        IAuditSink audit = Substitute.For<IAuditSink>();

        LlmGateway gateway = new(
            resolver,
            validator,
            metrics,
            Microsoft.Extensions.Options.Options.Create(new LlmOptions { MaxRetries = 2, RequestTimeoutSeconds = 5 }),
            audit,
            new UnlimitedBudgetGuard(),
            TimeProvider.System,
            NullLogger<LlmGateway>.Instance);

        PinnedModelCandidate candidate = CreateCandidate("test-tenant", "classifier");
        LlmRequest<ClassificationPayload> request = new()
        {
            Candidate = candidate,
            ResponseSchemaName = StructuredOutputValidator.ClassificationSchemaName,
            SystemPrompt = "system prompt",
            UserPrompt = "user prompt",
            Timeout = TimeSpan.FromSeconds(5),
            Metadata = CreateMetadata(),
        };

        Result<LlmResult<ClassificationPayload>> result = await gateway.GenerateStructuredAsync(
            request, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.LlmUnavailable.ToString());
        callCount.ShouldBe(1, "LlmGateway must NOT retry LlmUnavailable; the transport layer owns transport retries.");
    }

    [Fact]
    public async Task LlmGatewayRetriesSemanticFailureUpToMaxRetries()
    {
        int callCount = 0;
        IProviderChatClient mockClient = Substitute.For<IProviderChatClient>();
        mockClient.GetResponseAsync(Arg.Any<ProviderChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                // Returns invalid JSON content -> Interpret turns this into LlmStructuredOutputInvalid
                return Task.FromResult(Result<ProviderChatResponse>.Success(new ProviderChatResponse(
                    "not valid json",
                    "test-provider",
                    "gpt-4o",
                    10,
                    20,
                    "req-1",
                    "stop")));
            });

        IChatClientResolver resolver = Substitute.For<IChatClientResolver>();
        resolver.ResolveAsync(Arg.Any<PinnedModelCandidate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IProviderChatClient>.Success(mockClient)));

        StructuredOutputValidator validator = new();
        IMeterFactory meterFactory = Substitute.For<IMeterFactory>();
        meterFactory.Create(Arg.Any<MeterOptions>()).Returns(new Meter("test"));
        SreMetrics metrics = new(meterFactory);
        IAuditSink audit = Substitute.For<IAuditSink>();

        LlmGateway gateway = new(
            resolver,
            validator,
            metrics,
            Microsoft.Extensions.Options.Options.Create(new LlmOptions { MaxRetries = 2, RequestTimeoutSeconds = 5 }),
            audit,
            new UnlimitedBudgetGuard(),
            TimeProvider.System,
            NullLogger<LlmGateway>.Instance);

        PinnedModelCandidate candidate = CreateCandidate("test-tenant", "classifier");
        LlmRequest<ClassificationPayload> request = new()
        {
            Candidate = candidate,
            ResponseSchemaName = StructuredOutputValidator.ClassificationSchemaName,
            SystemPrompt = "system prompt",
            UserPrompt = "user prompt",
            Timeout = TimeSpan.FromSeconds(5),
            Metadata = CreateMetadata(),
        };

        Result<LlmResult<ClassificationPayload>> result = await gateway.GenerateStructuredAsync(
            request, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.LlmStructuredOutputInvalid.ToString());
        callCount.ShouldBe(3, "LlmGateway must retry semantic failure up to MaxRetries (1 initial + 2 retries = 3).");
    }

    private sealed class SlowHttpMessageHandler(TimeSpan delay, HttpResponseMessage response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return response;
        }
    }

    [Fact]
    public async Task PipelineAttemptTimeoutMapsToLlmTimeoutNotAnUnhandledException()
    {
        // Longer than the 100ms attempt timeout configured below, so Polly's own timeout
        // strategy — not the caller's cancellation token — gives up first.
        SlowHttpMessageHandler stub = new(
            TimeSpan.FromMilliseconds(500),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessChatJson, System.Text.Encoding.UTF8, "application/json"),
            });

        await using ServiceProvider provider = BuildServiceProvider(stub, configureResilience: options =>
        {
            options.Retry.ShouldHandle = static _ => ValueTask.FromResult(false);
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(100);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromMilliseconds(500);
        });
        IHttpClientFactory clients = provider.GetRequiredService<IHttpClientFactory>();
        ICredentialResolver credentials = new StubCredentialResolver();

        OpenAiProviderChatClient client = new(clients, credentials, CreateRevision());

        Result<ProviderChatResponse> response = await client.GetResponseAsync(
            CreateChatRequest(), TestContext.Current.CancellationToken);

        response.IsFailure.ShouldBeTrue();
        response.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.LlmTimeout.ToString());
    }

    [Fact]
    public async Task AdapterToGatewayPathForPollyTimeoutSurfacesLlmTimeoutAndTimeoutTelemetry()
    {
        SlowHttpMessageHandler stub = new(
            TimeSpan.FromMilliseconds(500),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessChatJson, System.Text.Encoding.UTF8, "application/json"),
            });

        await using ServiceProvider provider = BuildServiceProvider(stub, configureResilience: options =>
        {
            options.Retry.ShouldHandle = static _ => ValueTask.FromResult(false);
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(100);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromMilliseconds(500);
        });
        IHttpClientFactory clients = provider.GetRequiredService<IHttpClientFactory>();
        ICredentialResolver credentials = new StubCredentialResolver();

        OpenAiProviderChatClient client = new(clients, credentials, CreateRevision());

        IChatClientResolver resolver = Substitute.For<IChatClientResolver>();
        resolver.ResolveAsync(Arg.Any<PinnedModelCandidate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IProviderChatClient>.Success(client)));

        StructuredOutputValidator validator = new();
        IMeterFactory meterFactory = Substitute.For<IMeterFactory>();
        Meter meter = new(SreMetrics.MeterName);
        meterFactory.Create(Arg.Any<MeterOptions>()).Returns(meter);
        SreMetrics metrics = new(meterFactory);
        IAuditSink audit = Substitute.For<IAuditSink>();

        using MeterListener meterListener = new();
        string? recordedMetricStatus = null;
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == SreMetrics.MeterName && instrument.Name == "sre_llm_requests_total")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == TraceAttributes.ResultStatus)
                {
                    recordedMetricStatus = tag.Value as string;
                }
            }
        });
        meterListener.Start();

        string? recordedActivityResultStatus = null;
        string? recordedActivityErrorCode = null;
        using ActivityListener activityListener = new()
        {
            ShouldListenTo = source => source.Name == SreActivitySources.Llm.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                recordedActivityResultStatus = activity.GetTagItem(TraceAttributes.ResultStatus) as string;
                recordedActivityErrorCode = activity.GetTagItem(TraceAttributes.ErrorCode) as string;
            },
        };
        ActivitySource.AddActivityListener(activityListener);

        LlmGateway gateway = new(
            resolver,
            validator,
            metrics,
            Microsoft.Extensions.Options.Options.Create(new LlmOptions { MaxRetries = 2, RequestTimeoutSeconds = 5 }),
            audit,
            new UnlimitedBudgetGuard(),
            TimeProvider.System,
            NullLogger<LlmGateway>.Instance);

        PinnedModelCandidate candidate = CreateCandidate("test-tenant", "classifier");
        LlmRequest<ClassificationPayload> request = new()
        {
            Candidate = candidate,
            ResponseSchemaName = StructuredOutputValidator.ClassificationSchemaName,
            SystemPrompt = "system prompt",
            UserPrompt = "user prompt",
            Timeout = TimeSpan.FromSeconds(5),
            Metadata = CreateMetadata(),
        };

        Result<LlmResult<ClassificationPayload>> result = await gateway.GenerateStructuredAsync(
            request, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.LlmTimeout.ToString());
        recordedMetricStatus.ShouldBe("timeout");
        recordedActivityResultStatus.ShouldBe("timeout");
        recordedActivityErrorCode.ShouldBe(ApplicationErrorCodes.LlmTimeout.Value);
        audit.Received(1).RecordLlmCall(Arg.Is<LlmCallAudit>(a =>
            a.Outcome == "timeout"
            && a.ErrorCode == ApplicationErrorCodes.LlmTimeout));
    }

    [Fact]
    public async Task ProviderRevisionVerifierTimeoutSurfacesLlmTimeout()
    {
        SlowHttpMessageHandler stub = new(
            TimeSpan.FromMilliseconds(500),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", System.Text.Encoding.UTF8, "application/json"),
            });

        await using ServiceProvider provider = BuildServiceProvider(stub, configureResilience: options =>
        {
            options.Retry.ShouldHandle = static _ => ValueTask.FromResult(false);
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(100);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromMilliseconds(500);
        });

        ProviderRevisionVerifier verifier = provider.GetRequiredService<ProviderRevisionVerifier>();

        Result<ProviderVerificationProbe> result = await verifier.VerifyAsync(
            CreateRevision(), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.LlmTimeout.ToString());
        result.Error.Message.ShouldBe("The model provider request timed out.");
    }

    [Fact]
    public async Task ProviderRevisionVerifierOpenCircuitSurfacesLlmUnavailable()
    {
        int callCount = 0;
        StubHttpMessageHandler stub = new(request =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        await using ServiceProvider provider = BuildServiceProvider(stub, configureResilience: options =>
        {
            options.Retry.ShouldHandle = static _ => ValueTask.FromResult(false);
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(200);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromMilliseconds(500);
            options.CircuitBreaker.MinimumThroughput = 2;
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(10);
        });

        ProviderRevisionVerifier verifier = provider.GetRequiredService<ProviderRevisionVerifier>();

        Result<ProviderVerificationProbe> probe1 = await verifier.VerifyAsync(CreateRevision(), TestContext.Current.CancellationToken);
        probe1.IsFailure.ShouldBeTrue();
        callCount.ShouldBe(1);

        Result<ProviderVerificationProbe> probe2 = await verifier.VerifyAsync(CreateRevision(), TestContext.Current.CancellationToken);
        probe2.IsFailure.ShouldBeTrue();
        callCount.ShouldBe(2);

        Result<ProviderVerificationProbe> probe3 = await verifier.VerifyAsync(CreateRevision(), TestContext.Current.CancellationToken);
        probe3.IsFailure.ShouldBeTrue();
        probe3.Error.Code.ToString().ShouldBe(ApplicationErrorCodes.LlmUnavailable.ToString());
        probe3.Error.Message.ShouldBe("The model provider is unavailable.");
        callCount.ShouldBe(2, "Open circuit must reject the probe without sending an HTTP request.");
    }

    [Fact]
    public async Task ProviderRevisionVerifierPreservesCallerCancellation()
    {
        SlowHttpMessageHandler stub = new(
            TimeSpan.FromSeconds(2),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", System.Text.Encoding.UTF8, "application/json"),
            });

        await using ServiceProvider provider = BuildServiceProvider(stub, configureResilience: options =>
        {
            options.Retry.ShouldHandle = static _ => ValueTask.FromResult(false);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(20);
        });

        ProviderRevisionVerifier verifier = provider.GetRequiredService<ProviderRevisionVerifier>();

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

        Func<Task> act = async () => await verifier.VerifyAsync(CreateRevision(), cts.Token);
        await act.ShouldThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RetryThatSucceedsOnFinalAttemptDoesNotLogRetryExhaustion()
    {
        int callCount = 0;
        StubHttpMessageHandler stub = new(request =>
        {
            callCount++;
            if (callCount <= 3)
            {
                HttpResponseMessage rateLimited = new(HttpStatusCode.TooManyRequests);
                rateLimited.Headers.TryAddWithoutValidation("Retry-After", "0");
                return rateLimited;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessChatJson, System.Text.Encoding.UTF8, "application/json"),
            };
        });

        CapturingLoggerProvider logs = new();
        await using ServiceProvider provider = BuildServiceProvider(stub, loggerProvider: logs);
        IHttpClientFactory clients = provider.GetRequiredService<IHttpClientFactory>();
        ICredentialResolver credentials = new StubCredentialResolver();

        OpenAiProviderChatClient client = new(clients, credentials, CreateRevision());

        Result<ProviderChatResponse> response = await client.GetResponseAsync(
            CreateChatRequest(), TestContext.Current.CancellationToken);

        response.IsSuccess.ShouldBeTrue();
        callCount.ShouldBe(4, "Three retries exhaust the every-retry-fails budget before the final attempt succeeds.");
        logs.EventIds.ShouldNotContain(
            RetryExhaustedEventId,
            "The final attempt succeeded, so nothing was actually exhausted — Retry.OnRetry must not have guessed otherwise before that attempt ran.");
    }

    [Fact]
    public async Task ExhaustingAllRetriesLogsRetryExhaustionExactlyOnce()
    {
        StubHttpMessageHandler stub = new(request =>
        {
            HttpResponseMessage msg = new(HttpStatusCode.TooManyRequests);
            msg.Headers.TryAddWithoutValidation("Retry-After", "0");
            return msg;
        });

        CapturingLoggerProvider logs = new();
        await using ServiceProvider provider = BuildServiceProvider(stub, loggerProvider: logs);
        IHttpClientFactory clients = provider.GetRequiredService<IHttpClientFactory>();
        ICredentialResolver credentials = new StubCredentialResolver();

        OpenAiProviderChatClient client = new(clients, credentials, CreateRevision());

        Result<ProviderChatResponse> response = await client.GetResponseAsync(
            CreateChatRequest(), TestContext.Current.CancellationToken);

        response.IsFailure.ShouldBeTrue();
        logs.EventIds.Count(static id => id == RetryExhaustedEventId).ShouldBe(
            1, "Exhaustion is one terminal outcome, observed once after the pipeline gives up — never once per retry.");
    }
}
