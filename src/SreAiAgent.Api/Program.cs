using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SreAiAgent.Api.Endpoints;
using SreAiAgent.Api.Http;
using SreAiAgent.Api.Security;
using SreAiAgent.Application;
using SreAiAgent.Contracts.Serialization;
using SreAiAgent.Infrastructure;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Observability.Telemetry;
using SreAiAgent.Workflow;

// Composition root for the HTTP surface (ARCHITECTURE.md 13). Everything here is
// wiring; no file in this project decides anything about an incident.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddSreConfiguration(builder.Configuration)
    .AddSreRegistries()
    .AddSreMetrics()
    .AddSreLlm()
    .AddSreObservability(builder.Environment)
    .AddSreTools()
    .AddSrePersistence()
    .AddSreApplication()
    .AddSreAlertIngestion()
    .AddSreOutboxDispatcher()
    .AddSreWorkflow(builder.Configuration)
    .AddSreInvestigationUseCases()
    .AddSreTelemetry(builder.Configuration, builder.Environment);

// Bound eagerly because authentication has to be configured before the container
// is built, and a misconfigured security section must stop the host rather than
// surface on the first unauthenticated request.
SecurityOptions security = new();
builder.Configuration.GetSection(SecurityOptions.SectionName).Bind(security);

builder.Services.AddSreSecurity(security, builder.Environment);

// The source-generated resolver and the wire shape that goes with it
// (docs/DECISIONS.md D10). The naming policy is set explicitly rather than
// inherited: ASP.NET Core's web defaults are camelCase, and the contracts, the
// examples in ARCHITECTURE.md 8.2 and the wire-format tests are all snake_case.
// Leaving the default in place produced an HTTP surface that disagreed with the
// only written specification of its own payloads.
builder.Services.ConfigureHttpJsonOptions(static options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, SreAiAgentJsonContext.Default);
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.MaxDepth = 32;
});

builder.Services.Configure<KestrelServerOptions>(options =>
    options.Limits.MaxRequestBodySize = security.MaxRequestBodyBytes);

// ASP.NET Core instrumentation and the scrape endpoint are added here rather
// than in AddSreTelemetry, because the workers have neither: an HTTP server they
// do not run cannot be instrumented, and a scrape endpoint would be a second
// listener whose only job is metrics.
builder.Services.AddOpenTelemetry()
    .WithTracing(static tracing => tracing.AddAspNetCoreInstrumentation())
    .WithMetrics(static metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddSreHealthChecks().AddSreWorkflowHealthCheck(builder.Configuration);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Partitioned by principal rather than globally. A shared limit lets one
    // noisy integration exhaust the budget for every other tenant, which turns a
    // client's bug into this platform's outage.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = security.RateLimitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

WebApplication app = builder.Build();

// Correlation runs before anything that logs, so every record of a request
// carries the same id, including the ones written while it fails.
app.UseMiddleware<CorrelationIdMiddleware>();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// GET /metrics (ARCHITECTURE.md 12.1). Anonymous on purpose: a scrape happens
// before a token exists, and the endpoint exposes counters and histograms rather
// than incident content.
if (builder.Configuration.GetValue("Telemetry:EnablePrometheusEndpoint", defaultValue: true))
{
    app.MapPrometheusScrapingEndpoint();
}

app.MapHealthEndpoints();
app.MapIncidentEndpoints();
app.MapAlertEndpoints();
app.MapApprovalEndpoints();
app.MapModelProviderEndpoints();
app.MapStreamEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point marker, so the integration suite can host this assembly.</summary>
public partial class Program;
