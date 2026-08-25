// Composition root for the investigation worker (ARCHITECTURE.md 19.1).
// Workflow worker registration and consumers are wired in later commits.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

using IHost host = builder.Build();

// The host installs its own SIGTERM/SIGINT handling for graceful shutdown
// (ARCHITECTURE.md 19.2), so no external token participates here.
await host.RunAsync(CancellationToken.None).ConfigureAwait(false);
