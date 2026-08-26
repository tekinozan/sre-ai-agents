using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SreAiAgent.Application.Abstractions.Observability;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Observability;
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
    /// V1 ships deterministic fakes only, and this method fails fast rather than
    /// silently substituting them when real providers are asked for. A platform
    /// that quietly investigated fabricated data while reporting real conclusions
    /// would be worse than one that refuses to start, and the refusal is visible
    /// during a deploy.
    ///
    /// The real Prometheus, log and Kubernetes adapters plug in here behind the
    /// same ports; nothing above this line changes when they arrive.
    /// </remarks>
    public static IServiceCollection AddSreObservability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ObservabilityOptions>()
            .Validate(
                static options => options.UseFakeProviders,
                "Observability:UseFakeProviders must be true. V1 implements deterministic fake providers "
                + "only; the real backend adapters are not built yet, and silently investigating fabricated "
                + "data while reporting real conclusions is worse than refusing to start.")
            .ValidateOnStart();

        services.TryAddSingleton<IMetricsProvider>(static provider =>
            new FakeMetricsProvider(provider.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<ILogProvider>(static _ => new FakeLogProvider());

        services.TryAddSingleton<IKubernetesObservability>(static provider =>
            new FakeKubernetesObservability(provider.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<IDatabaseObservability>(static _ => new FakeDatabaseObservability());

        return services;
    }

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
        services.TryAddSingleton<IToolGateway, ToolGateway>();

        services.AddSingleton<IToolAdapter, PrometheusQueryAdapter>();
        services.AddSingleton<IToolAdapter, PrometheusQueryRangeAdapter>();
        services.AddSingleton<IToolAdapter, LogSearchAdapter>();
        services.AddSingleton<IToolAdapter, KubernetesPodsAdapter>();
        services.AddSingleton<IToolAdapter, KubernetesEventsAdapter>();
        services.AddSingleton<IToolAdapter, DatabaseHealthAdapter>();

        return services;
    }
}
