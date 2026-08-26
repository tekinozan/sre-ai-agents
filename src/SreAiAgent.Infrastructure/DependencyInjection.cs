using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Registries;

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
        services.TryAddSingleton<IReadOnlyToolRegistry, ReadOnlyToolRegistry>();
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
