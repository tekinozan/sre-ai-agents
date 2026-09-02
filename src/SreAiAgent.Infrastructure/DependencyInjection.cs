using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan);

        services.TryAddScoped<IChatClientResolver, ChatClientResolver>();
        services.TryAddScoped<IModelProviderControlPlane, ModelProviderControlPlane>();
        // Scoped, not singleton: it stages an audit row per call into the unit of
        // work of whichever activity is running, so the row commits with the work
        // it describes.
        services.TryAddScoped<ILlmGateway, LlmGateway>();

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

    private static void AddOptionsWithValidation<TOptions>(this IServiceCollection services, string sectionName)
        where TOptions : class
        => services
            .AddOptions<TOptions>()
            .BindConfiguration(sectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
}
