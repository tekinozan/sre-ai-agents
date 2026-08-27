using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Observability;

/// <summary>
/// Wires the OpenTelemetry SDK to the instrumentation the platform already emits
/// (ARCHITECTURE.md 16, docs/DECISIONS.md D13).
/// </summary>
/// <remarks>
/// The ActivitySource and Meter definitions live in the application layer because
/// they are BCL primitives; everything in this file is SDK and exporter
/// configuration, which is why it lives here and nowhere else. Code that records
/// a span has no idea whether anything is listening, and that is the point: the
/// same application assembly runs in a test with no exporter at all.
/// </remarks>
public static class TelemetryDependencyInjection
{
    /// <summary>Registers tracing, metrics and log correlation for a host.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">Configuration carrying the Telemetry section.</param>
    /// <param name="environment">Host environment, recorded as a resource attribute.</param>
    public static IServiceCollection AddSreTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        TelemetryOptions telemetry = new();
        configuration.GetSection(TelemetryOptions.SectionName).Bind(telemetry);

        ResourceBuilder resource = ResourceBuilder
            .CreateDefault()
            .AddService(
                serviceName: telemetry.ServiceName,
                serviceVersion: SreActivitySources.Version,

                // The instance id distinguishes replicas. Without it a trace from
                // a misbehaving pod is indistinguishable from its healthy peers,
                // which is exactly the question being asked during a rollout.
                serviceInstanceId: Environment.MachineName)
            .AddAttributes(
            [
                new KeyValuePair<string, object>("deployment.environment", environment.EnvironmentName),
            ]);

        services
            .AddOpenTelemetry()
            .WithTracing(tracing => ConfigureTracing(tracing, resource, telemetry))
            .WithMetrics(metrics => ConfigureMetrics(metrics, resource, telemetry));

        services.AddLogging(logging => logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resource);

            // Scopes and state carry the structured fields section 16.2 asks for:
            // trace id, span id, correlation id, incident id, agent name and error
            // code. Without them a log line is prose that cannot be joined to the
            // trace it belongs to.
            options.IncludeScopes = true;
            options.IncludeFormattedMessage = true;
            options.ParseStateValues = true;

            if (!string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint))
            {
                options.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(telemetry.OtlpEndpoint));
            }
        }));

        return services;
    }

    private static void ConfigureTracing(
        TracerProviderBuilder tracing,
        ResourceBuilder resource,
        TelemetryOptions telemetry)
    {
        tracing
            .SetResourceBuilder(resource)
            .AddSource([.. SreActivitySources.Names])
            .AddHttpClientInstrumentation()

            // Npgsql emits its own activities from a source of its own, so the
            // database work inside an investigation appears on the same trace
            // without the persistence layer being instrumented by hand.
            .AddSource("Npgsql")

            // Investigations are always sampled; HTTP ingress is not.
            //
            // The volumes differ by orders of magnitude and so does the value. An
            // investigation is rare, expensive and the thing someone asks about by
            // name, so a sampled-away trace is precisely the one that was needed.
            // Health probes are the opposite.
            //
            // Not wrapped in a ParentBasedSampler. That wrapper only consults its
            // inner sampler for root spans, and an investigation never is one: it
            // starts under the consumer span that triggered it. The sampler does
            // its own parent-based delegation instead.
            .SetSampler(new InvestigationAwareSampler(telemetry.HttpSamplingRatio));

        if (!string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint))
        {
            tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(telemetry.OtlpEndpoint));
        }
    }

    private static void ConfigureMetrics(
        MeterProviderBuilder metrics,
        ResourceBuilder resource,
        TelemetryOptions telemetry)
    {
        metrics
            .SetResourceBuilder(resource)
            .AddMeter(SreMetrics.MeterName)
            .AddRuntimeInstrumentation()
            .AddHttpClientInstrumentation();

        if (!string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint))
        {
            metrics.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(telemetry.OtlpEndpoint));
        }
    }
}
