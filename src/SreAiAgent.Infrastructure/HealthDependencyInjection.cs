using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SreAiAgent.Infrastructure.Health;

namespace SreAiAgent.Infrastructure;

/// <summary>
/// Registers shared database and message-broker health checks (ARCHITECTURE.md 15).
/// </summary>
public static class HealthDependencyInjection
{
    public static IServiceCollection AddSreHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Singleton: an admin client owns a connection, and a probe every ten
        // seconds must not open one each time. AddCheck<T> resolves through
        // ActivatorUtilities.GetServiceOrCreateInstance, which is why the singleton
        // registration comes first.
        services.TryAddSingleton<KafkaHealthCheck>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<KafkaHealthCheck>("kafka", tags: ["ready"]);

        return services;
    }
}
