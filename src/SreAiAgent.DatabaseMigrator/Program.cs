using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SreAiAgent.DatabaseMigrator;
using SreAiAgent.Infrastructure;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSreDatabasePool();
using IHost host = builder.Build();

ILogger logger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("SreAiAgent.DatabaseMigrator");

try
{
    DatabaseMigrationLog.Applying(logger);
    await host.Services.MigrateSreDatabaseAsync(CancellationToken.None).ConfigureAwait(false);
    DatabaseMigrationLog.Applied(logger);
    return 0;
}
catch (Exception exception)
{
    DatabaseMigrationLog.Failed(logger, exception);
    return 1;
}
