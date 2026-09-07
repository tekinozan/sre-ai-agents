using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SreAiAgent.AlertWorker;
using SreAiAgent.Infrastructure.Llm;
using SreAiAgent.Infrastructure.Messaging;
using SreAiAgent.Infrastructure.Messaging.Consumers;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.InvestigationWorker;
using Temporalio.Extensions.Hosting;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// HTTP health-host smoke tests for worker applications (ARCHITECTURE.md 19.2).
/// </summary>
/// <remarks>
/// These tests verify HTTP listener startup, routing, and health endpoint mapping (/health/live
/// and /health/ready) through the actual production composition roots (AlertWorkerApplication and
/// InvestigationWorkerApplication). They are health-host smoke tests, not complete worker-startup
/// lifecycle tests: external database/broker background loops are asserted for presence and then
/// isolated to avoid live network dependencies during unit/smoke CI. Full multi-service live lifecycle
/// proof is performed against the Docker Compose stack.
/// </remarks>
public sealed class WorkerHealthListenerSmokeTests
{
    private static readonly string[] TestArgs =
    [
        "--environment=Development",
        "--Health:Url=http://127.0.0.1:0",
        "--Database:ConnectionString=Host=localhost;Database=sre_test;Username=test;Password=test",
        "--Kafka:BootstrapServers=localhost:9092",
        "--Workflow:Engine=inline",
        $"--Registries:ConfigurationRoot={RepositoryPaths.Config}",
        $"--Registries:PromptRoot={RepositoryPaths.Prompts}",
    ];

    [Fact]
    public async Task AlertWorkerHealthHostSmokeTestBootsAndAnswersHealthEndpoints()
    {
        WebApplication app = AlertWorkerApplication.CreateApp(
            TestArgs,
            configure: static builder =>
            {
                // Assert expected production hosted services are registered before isolating for smoke test
                Type[] expectedHostedServices =
                [
                    typeof(DatabaseMigrator),
                    typeof(ModelAssignmentBootstrapper),
                    typeof(AlertReceivedConsumer),
                    typeof(OutboxDispatcher),
                    typeof(RetentionService),
                ];

                foreach (Type serviceType in expectedHostedServices)
                {
                    ServiceDescriptor? descriptor = builder.Services
                        .FirstOrDefault(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == serviceType);

                    descriptor.ShouldNotBeNull($"AlertWorker must register {serviceType.Name} as an IHostedService.");
                    builder.Services.Remove(descriptor);
                }

                // Replace external readiness dependencies (Kafka/DB) with test readiness
                builder.Services.Configure<HealthCheckServiceOptions>(static options =>
                {
                    options.Registrations.Clear();
                });
                builder.Services.AddHealthChecks()
                    .AddCheck("test-ready", static () => HealthCheckResult.Healthy(), tags: ["ready"]);
            });

        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            string address = app.Urls.First();
            using HttpClient client = new() { BaseAddress = new Uri(address) };

            // 1. /health/live mapped by AlertWorkerApplication
            HttpResponseMessage liveResponse = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
            liveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

            JsonElement liveBody = await liveResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            liveBody.GetProperty("status").GetString().ShouldBe("live");

            // 2. /health/ready mapped by AlertWorkerApplication
            HttpResponseMessage readyResponse = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            readyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvestigationWorkerHealthHostSmokeTestBootsAndAnswersHealthEndpoints()
    {
        WebApplication app = InvestigationWorkerApplication.CreateApp(
            TestArgs,
            configure: static builder =>
            {
                // Assert expected production hosted services are registered before isolating for smoke test
                Type[] expectedHostedServices =
                [
                    typeof(DatabaseMigrator),
                    typeof(ModelAssignmentBootstrapper),
                    typeof(InvestigationRequestedConsumer),
                    typeof(OutboxDispatcher),
                ];

                foreach (Type serviceType in expectedHostedServices)
                {
                    ServiceDescriptor? descriptor = builder.Services
                        .FirstOrDefault(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == serviceType);

                    descriptor.ShouldNotBeNull($"InvestigationWorker must register {serviceType.Name} as an IHostedService.");
                    builder.Services.Remove(descriptor);
                }

                // Replace external readiness dependencies (Kafka/DB/Temporal) with test readiness
                builder.Services.Configure<HealthCheckServiceOptions>(static options =>
                {
                    options.Registrations.Clear();
                });
                builder.Services.AddHealthChecks()
                    .AddCheck("test-ready", static () => HealthCheckResult.Healthy(), tags: ["ready"]);
            });

        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            string address = app.Urls.First();
            using HttpClient client = new() { BaseAddress = new Uri(address) };

            // 1. /health/live mapped by InvestigationWorkerApplication
            HttpResponseMessage liveResponse = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
            liveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

            JsonElement liveBody = await liveResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            liveBody.GetProperty("status").GetString().ShouldBe("live");

            // 2. /health/ready mapped by InvestigationWorkerApplication
            HttpResponseMessage readyResponse = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            readyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvestigationWorkerRegistersTemporalWorkerWhenEngineIsTemporal()
    {
        string[] inlineArgs =
        [
            "--environment=Development",
            "--Health:Url=http://127.0.0.1:0",
            "--Database:ConnectionString=Host=localhost;Database=sre_test;Username=test;Password=test",
            "--Kafka:BootstrapServers=localhost:9092",
            "--Workflow:Engine=inline",
            $"--Registries:ConfigurationRoot={RepositoryPaths.Config}",
            $"--Registries:PromptRoot={RepositoryPaths.Prompts}",
        ];

        string[] temporalArgs =
        [
            "--environment=Development",
            "--Health:Url=http://127.0.0.1:0",
            "--Database:ConnectionString=Host=localhost;Database=sre_test;Username=test;Password=test",
            "--Kafka:BootstrapServers=localhost:9092",
            "--Workflow:Engine=temporal",
            "--Workflow:TaskQueue=sre-investigation",
            $"--Registries:ConfigurationRoot={RepositoryPaths.Config}",
            $"--Registries:PromptRoot={RepositoryPaths.Prompts}",
        ];

        IServiceCollection? inlineServices = null;
        IServiceCollection? temporalServices = null;

        WebApplication inlineApp = InvestigationWorkerApplication.CreateApp(
            inlineArgs,
            configure: builder => inlineServices = builder.Services);

        WebApplication temporalApp = InvestigationWorkerApplication.CreateApp(
            temporalArgs,
            configure: builder => temporalServices = builder.Services);

        try
        {
            inlineServices.ShouldNotBeNull();
            temporalServices.ShouldNotBeNull();

            // 1. Differential assertion: exactly one additional IHostedService is registered under Temporal
            int inlineHostedCount = inlineServices.Count(static s => s.ServiceType == typeof(IHostedService));
            int temporalHostedCount = temporalServices.Count(static s => s.ServiceType == typeof(IHostedService));

            temporalHostedCount.ShouldBe(
                inlineHostedCount + 1,
                "Temporal engine must register exactly one additional IHostedService for the Temporal worker.");

            // The additional hosted service in temporalServices is the singleton factory registered by AddHostedTemporalWorker
            ServiceDescriptor temporalHostedDescriptor = temporalServices
                .Where(static s => s.ServiceType == typeof(IHostedService))
                .Single(static s => s.ImplementationFactory != null);

            temporalHostedDescriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
            inlineServices.Any(static s => s.ServiceType == typeof(IHostedService) && s.ImplementationFactory != null)
                .ShouldBeFalse("Inline engine must not register a factory-backed IHostedService.");

            inlineServices.Any(static s => s.ServiceType == typeof(IConfigureOptions<TemporalWorkerServiceOptions>))
                .ShouldBeFalse("Inline engine must not configure TemporalWorkerServiceOptions.");

            ServiceDescriptor? namedOptionsDescriptor = temporalServices
                .FirstOrDefault(static s => s.ServiceType == typeof(IConfigureOptions<TemporalWorkerServiceOptions>)
                    && s.ImplementationInstance is ConfigureNamedOptions<TemporalWorkerServiceOptions> named
                    && string.Equals(named.Name, "sre-investigation", StringComparison.Ordinal));

            namedOptionsDescriptor.ShouldNotBeNull(
                "Temporal worker registration must configure named TemporalWorkerServiceOptions matching task queue 'sre-investigation'.");
        }
        finally
        {
            await inlineApp.DisposeAsync();
            await temporalApp.DisposeAsync();
        }
    }
}
