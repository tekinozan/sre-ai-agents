using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Agents;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure;
using SreAiAgent.Infrastructure.Audit;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm;
using SreAiAgent.Infrastructure.Llm.Providers;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;
using SreAiAgent.Infrastructure.Persistence.Repositories;
using SreAiAgent.Infrastructure.Registries;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Proves that a genuinely <em>resolved</em> credential -- not merely the
/// reference stored for it -- never reaches a pinned
/// <see cref="ConfigurationSnapshot"/>/<see cref="PinnedModelAssignment"/>, the
/// audit trail, or a log line (ADP-102).
/// </summary>
/// <remarks>
/// <see cref="CredentialReferenceHttpTests"/> already proves the reference
/// stored in <c>provider_connection_revisions</c> and echoed by the API is
/// never a literal secret. That leaves the surface this suite's class
/// comment could otherwise claim without a test actually exercising it: what
/// happens once the platform has <em>read</em> the secret out of its
/// <c>env://</c> location and is holding it in memory. This suite drives
/// that path for real: the connection, revision, deployment and assignment
/// are created and activated directly against real rows (mirroring
/// <c>ModelControlPlaneTests.CreateAndActivateMinimalReleaseAsync</c> --
/// activation's own evaluation/verification checks are a different concern,
/// already covered there); <see cref="ConfigurationPinner"/> then pins a real
/// snapshot from those rows; <see cref="ExternalReferenceCredentialResolver"/>
/// (the real implementation, not a stub) reads the sentinel out of the actual
/// process environment; <see cref="OpenAiProviderChatClient"/> puts it on a
/// real <c>Authorization</c> header, proven by a handler that inspects the
/// outbound request before returning a canned response; and
/// <see cref="LlmGateway"/> records a real <see cref="AuditStore"/> row,
/// committed to Postgres and read back afterward, plus real log output
/// captured across the whole flow.
///
/// One part of the platform's boundary this suite does not drive: whatever a
/// real provider's own request/response logging does with the header once it
/// leaves this process on the wire. That is the provider's trust boundary,
/// not this platform's, and untestable from here by construction.
/// </remarks>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CredentialLifecycleLeakTests(PostgresFixture postgres)
{
    [Fact]
    public async Task AResolvedCredentialNeverReachesThePinnedSnapshotTheAuditEntryOrAnyLogLine()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "credential-leak-" + Guid.NewGuid().ToString("N");
        string sentinelVarName = "SRE_CREDENTIAL_LEAK_SENTINEL_" + Guid.NewGuid().ToString("N");

        // A GUID string is distinctive enough that an accidental substring
        // match (e.g. the literal word "secret") cannot produce a false pass.
        string sentinel = Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(sentinelVarName, sentinel);

        try
        {
            const string InnerClassificationJson =
                """{"incident_types":["database"],"confidence":0.9,"rationale":"scripted"}""";
            string outerChatJson = $$"""
                {
                    "id": "req-leak-test-1",
                    "model": "gpt-4o",
                    "choices": [{"message": {"content": {{JsonSerializer.Serialize(InnerClassificationJson)}}}, "finish_reason": "stop"}],
                    "usage": {"prompt_tokens": 10, "completion_tokens": 20}
                }
                """;

            RecordingHttpMessageHandler stubHandler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(outerChatJson, Encoding.UTF8, "application/json"),
            });

            CapturingLoggerProvider logs = new();

            await using ServiceProvider provider = BuildServiceProvider(stubHandler, logs);
            await using AsyncServiceScope scope = provider.CreateAsyncScope();

            SreDbContext context = scope.ServiceProvider.GetRequiredService<SreDbContext>();
            ModelProviderRepository repository = scope.ServiceProvider.GetRequiredService<ModelProviderRepository>();
            IConfigurationPinner pinner = scope.ServiceProvider.GetRequiredService<IConfigurationPinner>();
            IChatClientResolver chatClientResolver = scope.ServiceProvider.GetRequiredService<IChatClientResolver>();
            ILlmGateway gateway = scope.ServiceProvider.GetRequiredService<ILlmGateway>();
            RecordingAuditSink auditSink = (RecordingAuditSink)scope.ServiceProvider.GetRequiredService<IAuditSink>();

            TenantScope tenantScope = TenantScope.TenantWide(tenant);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // 1. Real rows: a connection/revision whose CredentialRef points at
            // the sentinel env var, a deployment and an activated assignment --
            // mirroring ModelControlPlaneTests.CreateAndActivateMinimalReleaseAsync,
            // but through a real adapter kind (OpenAi) rather than Scripted, since
            // Scripted never resolves a credential at all.
            ProviderConnectionEntity connection = new()
            {
                Id = ProviderConnectionId.New(),
                Tenant = tenant,
                DisplayName = "leak-test-connection",
                CreatedAt = now,
            };
            context.ProviderConnections.Add(connection);
            await context.SaveChangesAsync(ct);

            ProviderConnectionRevisionEntity revision = new()
            {
                Id = ProviderConnectionRevisionId.New(),
                ProviderConnectionId = connection.Id,
                Tenant = tenant,
                Revision = 1,
                AdapterKind = ProviderAdapterKind.OpenAi,
                Endpoint = "https://fake-provider.invalid",
                CredentialRef = $"env://{sentinelVarName}",
                ConfigurationHash = "leak-test-revision-hash",
                CreatedAt = now,
                CreatedBy = "test",
            };
            context.ProviderConnectionRevisions.Add(revision);
            connection.ActiveRevisionId = revision.Id;
            await context.SaveChangesAsync(ct);

            ModelDeploymentVersionEntity deployment = new()
            {
                Id = ModelDeploymentVersionId.New(),
                Tenant = tenant,
                LogicalName = "leak-test-deployment",
                Version = 1,
                ProviderConnectionRevisionId = revision.Id,
                ProviderModel = "gpt-4o",
                MaxOutputTokens = 512,
                TimeoutMilliseconds = 30_000,
                ProviderParametersJson = "{}",
                Currency = "USD",
                ConfigurationHash = "leak-test-deployment-hash",
                CreatedAt = now,
                CreatedBy = "test",
            };
            context.ModelDeploymentVersions.Add(deployment);
            await context.SaveChangesAsync(ct);

            ModelAssignmentReleaseEntity release = new()
            {
                Id = ModelAssignmentReleaseId.New(),
                Tenant = tenant,
                Name = "leak-test-release",
                Version = 1,
                ClassifierDeploymentVersionId = deployment.Id,
                CorrelatorDeploymentVersionId = deployment.Id,
                ConfigurationHash = "leak-test-release-hash",
                CreatedAt = now,
                CreatedBy = "test",
            };
            context.ModelAssignmentReleases.Add(release);
            context.ModelAssignmentAgentSlots.Add(new ModelAssignmentAgentSlotEntity
            {
                AssignmentReleaseId = release.Id,
                Tenant = tenant,
                AgentName = "test-expert",
                DeploymentVersionId = deployment.Id,
            });
            await context.SaveChangesAsync(ct);

            context.TenantActiveModelAssignments.Add(new TenantActiveModelAssignmentEntity
            {
                Tenant = tenant,
                AssignmentReleaseId = release.Id,
                ActivatedAt = now,
                ActivatedBy = "test",
            });
            await context.SaveChangesAsync(ct);

            // 2. Pin a real snapshot from those rows.
            Result<ConfigurationSnapshot> pinned = await pinner.PinCurrentAsync(tenant, now, ct);
            pinned.IsSuccess.ShouldBeTrue();
            ConfigurationSnapshot snapshot = pinned.Value;

            AssertNoSentinel(snapshot, sentinel);
            // The one credential-shaped field a PinnedModelCandidate carries is
            // a configuration hash, never the reference and never the value.
            snapshot.ModelAssignment.Classifier.ProviderConfigurationHash.ShouldBe("leak-test-revision-hash");

            // 3. Resolve a real chat client for the pinned classifier candidate --
            // this is where ExternalReferenceCredentialResolver actually reads
            // the sentinel out of the process environment.
            Result<IProviderChatClient> resolvedClient = await chatClientResolver.ResolveAsync(
                snapshot.ModelAssignment.Classifier, ct);
            resolvedClient.IsSuccess.ShouldBeTrue();

            // 4. Drive a real LlmGateway call using that resolved client.
            LlmRequest<ClassificationPayload> request = new()
            {
                Candidate = snapshot.ModelAssignment.Classifier,
                ResponseSchemaName = StructuredOutputValidator.ClassificationSchemaName,
                SystemPrompt = "system prompt",
                UserPrompt = "user prompt",
                Timeout = TimeSpan.FromSeconds(10),
                Metadata = new LlmCallMetadata
                {
                    IncidentId = IncidentId.New(),
                    AgentName = "classifier",
                    PromptName = "classifier.v1",
                    PromptVersion = "1",
                    PromptHash = "hash-1",
                    Tenant = tenant,
                    CorrelationId = "corr-leak-test",
                },
            };

            Result<LlmResult<ClassificationPayload>> result = await gateway.GenerateStructuredAsync(request, ct);

            result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : string.Empty);

            // Proof a real secret was actually resolved and used: the outbound
            // request really carried it. Everything asserted from here on is
            // that it went no further than this one header.
            stubHandler.LastAuthorizationHeaderValue.ShouldBe(sentinel);

            // 5. The audit entry LlmGateway recorded, committed to Postgres.
            await context.SaveChangesAsync(ct);

            auditSink.LastLlmCall.ShouldNotBeNull();
            AssertNoSentinel(auditSink.LastLlmCall!, sentinel);

            await using SreDbContext readBack = postgres.CreateContext();
            LlmCallEntity persisted = await readBack.LlmCalls
                .SingleAsync(call => call.Id == auditSink.LastLlmCall!.LlmCallId.Value, ct);
            AssertNoSentinel(persisted, sentinel);

            // 6. Logs captured for the whole flow above.
            IReadOnlyList<string> capturedMessages = logs.Messages;
            capturedMessages.ShouldNotBeEmpty();
            foreach (string message in capturedMessages)
            {
                message.ShouldNotContain(sentinel);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelVarName, null);
        }
    }

    /// <summary>
    /// Walks every public property reachable from <paramref name="root"/>
    /// (records, dictionaries, lists) and asserts no string value equals or
    /// contains <paramref name="sentinel"/>. Reflection rather than a
    /// hand-picked field list, so a field added later to any of these shapes
    /// is covered by construction rather than by remembering to update this
    /// test.
    /// </summary>
    private static void AssertNoSentinel(object? root, string sentinel)
    {
        Walk(root, sentinel, new HashSet<object>(ReferenceEqualityComparer.Instance));

        static void Walk(object? value, string sentinel, HashSet<object> visited)
        {
            switch (value)
            {
                case null:
                    return;
                case string text:
                    text.ShouldNotContain(sentinel);
                    return;
                case Guid or DateTime or DateTimeOffset or TimeSpan or decimal:
                    return;
            }

            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum)
            {
                return;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (object? item in enumerable)
                {
                    Walk(item, sentinel, visited);
                }

                return;
            }

            if (type.IsClass && !visited.Add(value))
            {
                return;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object? propertyValue;
                try
                {
                    propertyValue = property.GetValue(value);
                }
                catch (TargetInvocationException)
                {
                    continue;
                }

                Walk(propertyValue, sentinel, visited);
            }
        }
    }

    private ServiceProvider BuildServiceProvider(HttpMessageHandler stubHandler, ILoggerProvider loggerProvider)
    {
        ServiceCollection services = new();

        services.AddScoped(_ => postgres.CreateContext());
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ModelProviderRepository>();
        services.AddScoped<IProviderRevisionReader>(p => p.GetRequiredService<ModelProviderRepository>());
        services.AddScoped<IConfigurationPinner, ConfigurationPinner>();
        services.AddScoped<IAuditSink>(p => new RecordingAuditSink(new AuditStore(p.GetRequiredService<SreDbContext>())));

        services.AddSingleton(Options.Create(new RegistryOptions
        {
            AgentRelease = "release",
            PromptRelease = "release",
            ModelRelease = "release",
            ToolRelease = "release",
            PolicyRelease = "release",
            FlagRelease = "release",
        }));

        IAgentFlagEvaluator flags = Substitute.For<IAgentFlagEvaluator>();
        flags.DeclaredStates().Returns(new Dictionary<string, string>(StringComparer.Ordinal));
        services.AddSingleton(flags);

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(environment);

        services.AddSingleton(TimeProvider.System);

        services.AddSingleton(Options.Create(new LlmOptions
        {
            AllowedCustomEndpointHosts = Array.Empty<string>(),
            RequestTimeoutSeconds = 10,
            MaxRetries = 0,
        }));

        services.AddLogging(builder => builder.AddProvider(loggerProvider));

        // Real ICredentialResolver, IChatClientResolver, ILlmGateway,
        // StructuredOutputValidator: nothing here is a stub or a substitute.
        services.AddSreLlm();
        services.AddSreMetrics();

        // Overrides the primary handler of the named client AddSreLlm() already
        // registered, layering onto the one production resilience pipeline
        // rather than standing up a second one (RetryExhaustionHandler and the
        // standard resilience handler both still wrap this).
        services.AddHttpClient(ProviderHttpClients.Chat)
            .ConfigurePrimaryHttpMessageHandler(() => stubHandler);

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Captures the outbound <c>Authorization</c> header and returns a canned response.</summary>
    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? LastAuthorizationHeaderValue { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastAuthorizationHeaderValue = request.Headers.Authorization?.Parameter;
            return Task.FromResult(responder(request));
        }
    }

    /// <summary>
    /// Wraps the real <see cref="AuditStore"/> so the test can inspect the exact
    /// <see cref="LlmCallAudit"/> value handed to it, while the call still
    /// commits to Postgres exactly as production does.
    /// </summary>
    private sealed class RecordingAuditSink(IAuditSink inner) : IAuditSink
    {
        public LlmCallAudit? LastLlmCall { get; private set; }

        public void RecordToolCall(ToolCallAudit call) => inner.RecordToolCall(call);

        public void RecordLlmCall(LlmCallAudit call)
        {
            LastLlmCall = call;
            inner.RecordLlmCall(call);
        }
    }

    /// <summary>
    /// Captures every formatted log message emitted during the test, across
    /// every category -- not just event ids, so a substring scan for the
    /// sentinel is meaningful. No log-capture convention already existed in
    /// this suite for full message content (<c>ProviderResilienceTests</c>'s
    /// <c>CapturingLoggerProvider</c> records only event ids); this is that
    /// same shape, extended to keep the formatted message and any scope state.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) { return [.. _messages]; } }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                lock (owner._messages)
                {
                    owner._messages.Add(state?.ToString() ?? string.Empty);
                }

                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._messages)
                {
                    owner._messages.Add(formatter(state, exception));
                    if (exception is not null)
                    {
                        owner._messages.Add(exception.ToString());
                    }
                }
            }
        }
    }
}
