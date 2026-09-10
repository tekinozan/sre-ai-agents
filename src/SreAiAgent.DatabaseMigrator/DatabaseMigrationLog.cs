using Microsoft.Extensions.Logging;

namespace SreAiAgent.DatabaseMigrator;

internal static partial class DatabaseMigrationLog
{
    [LoggerMessage(EventId = 7002, Level = LogLevel.Information, Message = "Applying business database migrations.")]
    public static partial void Applying(ILogger logger);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Information, Message = "Business database migrations applied successfully.")]
    public static partial void Applied(ILogger logger);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Error, Message = "Business database migration failed.")]
    public static partial void Failed(ILogger logger, Exception exception);
}
