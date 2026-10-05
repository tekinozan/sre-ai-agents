using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Security;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm;
using SreAiAgent.Infrastructure.Llm.Providers;
using SreAiAgent.Infrastructure.Prompting;
using SreAiAgent.Infrastructure.Registries;
using SreAiAgent.Infrastructure.Security;

namespace SreAiAgent.Infrastructure;

/// <summary>
/// Composition root helpers shared by the API and both workers
/// (ARCHITECTURE.md 13).
/// </summary>
/// <remarks>
/// There is no assembly scanning here. Every registration is written out, which
/// makes the dependency graph greppable and means a type is never wired up
/// because it happened to match a naming convention. It also keeps startup fast
/// and trimming viable.
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Binds and validates options, then registers the configuration registries.
    /// </summary>
    /// <remarks>
    /// Every options type uses <c>ValidateOnStart</c>. A misconfigured process
    /// must fail during deployment, where a failing pod is visible, rather than
    /// at the first incident, where it is an outage.
    /// </remarks>
    public static IServiceCollection AddSreConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptionsWithValidation<DatabaseOptions>(DatabaseOptions.SectionName);
        services.AddOptionsWithValidation<KafkaOptions>(KafkaOptions.SectionName);
        services.AddOptionsWithValidation<LlmOptions>(LlmOptions.SectionName);
        services.AddOptionsWithValidation<RegistryOptions>(RegistryOptions.SectionName);
        services.AddOptionsWithValidation<AgentRuntimeOptions>(AgentRuntimeOptions.SectionName);
        services.AddOptionsWithValidation<SecurityOptions>(SecurityOptions.SectionName);
        services.AddOptionsWithValidation<WorkflowOptions>(WorkflowOptions.SectionName);
        services.AddOptionsWithValidation<ObservabilityOptions>(ObservabilityOptions.SectionName);
        services.AddOptionsWithValidation<TelemetryOptions>(TelemetryOptions.SectionName);
        services.AddOptionsWithValidation<OutboxOptions>(OutboxOptions.SectionName);
        services.AddOptionsWithValidation<RetentionOptions>(RetentionOptions.SectionName);

        return services;
    }

    /// <summary>
    /// Registers the configuration registries and the startup validator.
    /// </summary>
    /// <remarks>
    /// Registries are singletons because they are immutable frozen dictionaries
    /// built once from disk. Making them scoped would rebuild every artifact per
    /// request, which is both slow and a correctness problem: two requests could
    /// then observe different configuration mid-deploy.
    /// </remarks>
    public static IServiceCollection AddSreRegistries(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<YamlArtifactLoader>();

        services.TryAddSingleton<IPromptRegistry, PromptRegistry>();
        services.TryAddSingleton<IModelProfileRegistry, ModelProfileRegistry>();
        services.TryAddSingleton<IAgentRegistry, AgentRegistry>();
        services.TryAddSingleton<ReadOnlyToolRegistry>();
        services.TryAddSingleton<IReadOnlyToolRegistry>(static provider =>
            provider.GetRequiredService<ReadOnlyToolRegistry>());

        // The same instance behind both ports. Routing only needs to ask whether
        // a tool exists, and giving it the full registry would let a component
        // that should merely check availability start executing tools.
        services.TryAddSingleton<IReadOnlyToolRegistryProbe>(static provider =>
            provider.GetRequiredService<ReadOnlyToolRegistry>());
        services.TryAddSingleton<IPolicyRegistry, PolicyRegistry>();
        services.TryAddSingleton<IAgentFlagEvaluator, AgentFlagEvaluator>();

        // IWriteToolRegistry is deliberately not registered. V1 has no write
        // tools, and leaving the service unresolvable means a component that
        // tries to take a dependency on one fails at startup rather than
        // silently receiving an empty registry it might later be populated
        // (ARCHITECTURE.md 15.2).

        services.AddHostedService<RegistryValidator>();

        return services;
    }

    /// <summary>
    /// Registers prompt rendering, redaction and the model gateway.
    /// </summary>
    /// <remarks>
    /// Wires up the LLM provider resolution subsystem, schema validation, prompt
    /// rendering, and redaction (docs/DECISIONS.md D09). Everything the platform
    /// depends on, namely schema validation, budgets, audit and error mapping,
    /// lives in LlmGateway rather than in the provider adapter.
    /// </remarks>
    public static IServiceCollection AddSreLlm(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IRedactor, RegexRedactor>();
        services.TryAddSingleton<IPromptRenderer, ControlledPromptRenderer>();
        services.TryAddSingleton<StructuredOutputValidator>();
        services.TryAddSingleton<ScriptedChatClient>();
        services.TryAddSingleton<ProviderEndpointPolicy>();
        services.TryAddSingleton<ICredentialResolver, ExternalReferenceCredentialResolver>();
        services.TryAddSingleton<ProviderClientCatalog>();
        services.TryAddSingleton<ProviderRevisionVerifier>();

        services.AddHttpClient(ProviderHttpClients.Chat)
            // Infinite on the client, deliberately: with a resilience handler the
            // pipeline's total timeout governs, and a shorter client timeout would
            // cut the pipeline off mid-retry.
            .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan)
            // Outer to the resilience handler added below (handlers registered earlier
            // wrap those registered later): it calls the pipeline exactly once per
            // logical request and so only ever observes the terminal outcome after every
            // retry the pipeline was going to make has already happened.
            .AddHttpMessageHandler(static provider => new RetryExhaustionHandler(
                provider.GetRequiredService<ILoggerFactory>().CreateLogger("SreAiAgent.Infrastructure.Llm.ProviderResilience")))
            .AddStandardResilienceHandler()
            .Configure((resilience, provider) =>
            {
                LlmOptions options = provider.GetRequiredService<IOptions<LlmOptions>>().Value;
                ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("SreAiAgent.Infrastructure.Llm.ProviderResilience");
                TimeSpan perAttempt = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);

                resilience.AttemptTimeout.Timeout = perAttempt;

                // Total timeout allows for the retry attempts underneath it.
                // Standard resilience pipeline allows 3 retries (4 attempts total).
                // Setting perAttempt * 5 ensures the total timeout does not starve the retries.
                resilience.TotalRequestTimeout.Timeout = perAttempt * 5;

                // Circuit breaker sampling duration must be at least twice the attempt timeout.
                resilience.CircuitBreaker.SamplingDuration = perAttempt * 2;

                // Respect Retry-After header from 429
                resilience.Retry.ShouldRetryAfterHeader = true;

                resilience.Retry.OnRetry = args =>
                {
                    ProviderResilienceLogging.LogModelHttpRetry(logger, args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds);

                    // Records the attempt count for RetryExhaustionHandler to read once the
                    // pipeline finishes; whether this retry (or a later one) succeeds is not
                    // known yet, so nothing is logged as "exhausted" from here.
                    RetryExhaustionHandler.RecordRetry(args.Context, args.AttemptNumber + 1);
                    return ValueTask.CompletedTask;
                };

                resilience.CircuitBreaker.OnOpened = args =>
                {
                    ProviderResilienceLogging.LogModelCircuitBreakerOpened(logger, args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                };
            });

        services.TryAddScoped<IChatClientResolver, ChatClientResolver>();
        services.TryAddScoped<IModelProviderControlPlane, ModelProviderControlPlane>();
        // Scoped, not singleton: it stages an audit row per call into the unit of
        // work of whichever activity is running, so the row commits with the work
        // it describes.
        services.TryAddScoped<ILlmGateway, LlmGateway>();

        // No ceiling unless persistence registers the real guard, which it adds
        // rather than tries, so it wins in either registration order.
        services.TryAddScoped<ITenantBudgetGuard, UnlimitedBudgetGuard>();

        return services;
    }

    /// <summary>Registers the metric instruments.</summary>
    public static IServiceCollection AddSreMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMetrics();
        services.TryAddSingleton<SreMetrics>();

        return services;
    }

    internal static void AddOptionsWithValidation<TOptions>(this IServiceCollection services, string sectionName)
        where TOptions : class
        => services
            .AddOptions<TOptions>()
            .BindConfiguration(sectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
}
