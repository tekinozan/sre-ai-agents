// Composition root for the HTTP surface (ARCHITECTURE.md 13).
// Endpoints, authentication, telemetry and application wiring are added in
// later commits; this file stays a composition root and never holds business rules.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

WebApplication app = builder.Build();

app.MapGet("/health/live", static () => Results.Ok());

await app.RunAsync(CancellationToken.None).ConfigureAwait(false);
