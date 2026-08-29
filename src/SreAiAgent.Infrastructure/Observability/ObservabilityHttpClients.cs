using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Observability;

/// <summary>
/// Named HTTP clients for the observability backends (ARCHITECTURE.md 14).
/// </summary>
/// <remarks>
/// One client per dependency, each with its own resilience pipeline. Section 14
/// asks for that explicitly, and the reason is that these backends fail
/// differently: a metrics store under load sheds requests and recovers in
/// seconds, while a log store answering a wide search may simply be slow. One
/// shared policy would be tuned for neither.
///
/// The per-request timeout sits below the tool timeout on purpose. Section 11.3
/// nests the timeouts so the innermost one fires first; inverted, the tool would
/// give up while the request was still in flight and the retry budget would never
/// be spent.
/// </remarks>
internal static class ObservabilityHttpClients
{
    public const string Prometheus = "sre-prometheus";

    public const string Loki = "sre-loki";

    public static IServiceCollection AddSreObservabilityHttpClients(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddBackend(services, Prometheus, static options => options.PrometheusBaseAddress);
        AddBackend(services, Loki, static options => options.LogBackendBaseAddress);

        return services;
    }

    private static void AddBackend(
        IServiceCollection services,
        string name,
        Func<ObservabilityOptions, string?> baseAddress)
    {
        services.AddHttpClient(name)
            .ConfigureHttpClient((provider, client) =>
            {
                ObservabilityOptions options = provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
                string? configured = baseAddress(options);

                // Registration cannot fail here without taking down hosts that
                // never use this backend, so the check belongs where the provider
                // is selected. AddSreObservability refuses to register a real
                // adapter without an address.
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    client.BaseAddress = new Uri(configured, UriKind.Absolute);
                }

                client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
            })
            .AddStandardResilienceHandler()
            .Configure((resilience, provider) =>
            {
                ObservabilityOptions options = provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
                TimeSpan perAttempt = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);

                resilience.AttemptTimeout.Timeout = perAttempt;

                // The total timeout has to leave room for the retries underneath
                // it, and the circuit breaker's sampling window must be at least
                // twice the attempt timeout or the pipeline refuses to build.
                resilience.TotalRequestTimeout.Timeout = perAttempt * 3;
                resilience.CircuitBreaker.SamplingDuration = perAttempt * 2;
            });
    }
}
