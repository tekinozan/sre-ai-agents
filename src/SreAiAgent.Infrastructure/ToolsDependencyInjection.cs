using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Observability;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Observability.Providers.Fakes;
using SreAiAgent.Infrastructure.Observability.Providers.Kubernetes;
using SreAiAgent.Infrastructure.Observability.Providers.Loki;
using SreAiAgent.Infrastructure.Observability.Providers.Prometheus;
using SreAiAgent.Infrastructure.Observability.Providers.Shared;
using SreAiAgent.Infrastructure.Tools;
using SreAiAgent.Infrastructure.Tools.Adapters;

namespace SreAiAgent.Infrastructure;

/// <summary>
/// Registers the tool gateway, its adapters and the observability backends.
/// </summary>
public static class ToolsDependencyInjection
{
    /// <summary>
    /// Registers the observability backends (ARCHITECTURE.md 19.3).
    /// </summary>
    /// <remarks>
    /// Selection is per port, not one switch, because the ports are not all in
    /// the same state: metrics, logs and Kubernetes have real adapters, database
    /// observability does not. A single boolean could not say that, and pinning
    /// four ports to one value would make the honest configuration inexpressible.
    ///
    /// The validation runs in both directions. A real provider with no address
    /// fails, and so does a fake provider in Production: an investigation that
    /// reported real conclusions from fabricated data is worse than one that
    /// never ran, and the refusal is visible during a deploy rather than during
    /// an incident.
    /// </remarks>
    public static IServiceCollection AddSreObservability(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<ObservabilityOptions>()
            .Validate(
                static options => options.UseFakeProviders is null,
                "Observability:UseFakeProviders no longer exists. Set Observability:MetricsProvider "
                + "(fake|prometheus), Observability:LogProvider (fake|loki) and "
                + "Observability:KubernetesProvider (fake) instead. Ignoring the old key would mean a "
                + "deployment that asked for real backends silently getting fabricated data.")
            .Validate(
                static options => options.MetricsProvider is ObservabilityProviders.Fake
                    or ObservabilityProviders.Prometheus,
                "Observability:MetricsProvider must be 'fake' or 'prometheus'.")
            .Validate(
                static options => options.LogProvider is ObservabilityProviders.Fake
                    or ObservabilityProviders.Loki,
                "Observability:LogProvider must be 'fake' or 'loki'.")
            .Validate(
                static options => options.KubernetesProvider is ObservabilityProviders.Fake
                    or ObservabilityProviders.InCluster,
                "Observability:KubernetesProvider must be 'fake' or 'incluster'.")
            .Validate(
                static options => options.MetricsProvider != ObservabilityProviders.Prometheus
                    || !string.IsNullOrWhiteSpace(options.PrometheusBaseAddress),
                "Observability:PrometheusBaseAddress is required when the metrics provider is 'prometheus'.")
            .Validate(
                static options => options.LogProvider != ObservabilityProviders.Loki
                    || !string.IsNullOrWhiteSpace(options.LogBackendBaseAddress),
                "Observability:LogBackendBaseAddress is required when the log provider is 'loki'.")
            .Validate(
                static options => options.KubernetesProvider != ObservabilityProviders.InCluster
                    || options.KubernetesNamespaceByCluster.Count > 0,
                "Observability:KubernetesNamespaceByCluster must name at least one cluster when the "
                + "Kubernetes provider is 'incluster'. It is the boundary on what this deployment may "
                + "read, and an empty map is not a permissive default.")
            .Validate(
                options => !environment.IsProduction()
                    || (options.MetricsProvider != ObservabilityProviders.Fake
                        && options.LogProvider != ObservabilityProviders.Fake
                        && options.KubernetesProvider != ObservabilityProviders.Fake),
                "A fake observability provider cannot be used in Production. An investigation that reports "
                + "real conclusions from fabricated data is worse than one that never ran.")
            .ValidateOnStart();

        services.AddSreObservabilityHttpClients();

        services.TryAddSingleton<IMetricsProvider>(static provider =>
            Selected(provider).MetricsProvider is ObservabilityProviders.Prometheus
                ? new PrometheusMetricsProvider(
                    provider.GetRequiredService<IHttpClientFactory>(),
                    provider.GetRequiredService<IReadOnlyToolRegistry>(),
                    provider.GetRequiredService<IOptions<ObservabilityOptions>>())
                : new FakeMetricsProvider(provider.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<ILogProvider>(static provider =>
            Selected(provider).LogProvider is ObservabilityProviders.Loki
                ? new LokiLogProvider(
                    provider.GetRequiredService<IHttpClientFactory>(),
                    provider.GetRequiredService<IOptions<ObservabilityOptions>>())
                : new FakeLogProvider());

        // Constructed lazily: InClusterConfig throws outside a cluster, so this
        // registration is harmless in a host that never selects the real adapter.
        services.TryAddSingleton<IKubernetes>(static _ =>
            new Kubernetes(KubernetesClientConfiguration.InClusterConfig()));

        services.TryAddSingleton<IKubernetesObservability>(static provider =>
            Selected(provider).KubernetesProvider is ObservabilityProviders.InCluster
                ? new KubernetesObservability(
                    provider.GetRequiredService<IKubernetes>(),
                    provider.GetRequiredService<IOptions<ObservabilityOptions>>())
                : new FakeKubernetesObservability(provider.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<IDatabaseObservability>(static _ => new FakeDatabaseObservability());

        return services;
    }

    private static ObservabilityOptions Selected(IServiceProvider provider)
        => provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;

    /// <summary>
    /// Registers the tool gateway and one adapter per tool in the release.
    /// </summary>
    /// <remarks>
    /// Adapters are registered individually rather than discovered by scanning.
    /// A tool adapter is the component that actually reaches a production system,
    /// so which ones exist should be visible in a diff rather than inferred from a
    /// naming convention.
    /// </remarks>
    public static IServiceCollection AddSreTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ToolRateLimiter>();

        // Scoped, unlike the gateway: it reads the incident and its snapshot
        // through the request-scoped DbContext and stages evidence into the same
        // unit of work the surrounding activity will commit.
        services.TryAddScoped<IToolExecutionContextResolver, ToolExecutionContextResolver>();
        services.TryAddScoped<Audit.IAuditSink, Audit.AuditStore>();
        services.TryAddSingleton<ToolAdapterCatalog>();

        // Scoped, because it resolves the incident's pinned policy and stages
        // evidence into the unit of work of the activity calling it.
        services.TryAddScoped<IToolGateway, ToolGateway>();

        services.AddSingleton<IToolAdapter, PrometheusQueryAdapter>();
        services.AddSingleton<IToolAdapter, PrometheusQueryRangeAdapter>();
        services.AddSingleton<IToolAdapter, LogSearchAdapter>();
        services.AddSingleton<IToolAdapter, KubernetesPodsAdapter>();
        services.AddSingleton<IToolAdapter, KubernetesEventsAdapter>();
        services.AddSingleton<IToolAdapter, DatabaseHealthAdapter>();

        return services;
    }
}
