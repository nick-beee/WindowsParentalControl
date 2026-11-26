using Serilog;

namespace ParentalControl.Core.Logging;

public static class LoggingConfig
{
    /// <summary>
    /// Creates the file logger in <paramref name="logDirectory"/>. Call it only after the data folder is secured
    /// (<see cref="Data.DatabaseManager.Initialize"/>). There is no fallback to the TEMP folder: a LocalSystem service must
    /// not write to a folder that a standard user can create before it, because a link there sends the writes to any file
    /// (require-admin-sign-in, finding S2). If the folder cannot be created, this method throws.
    /// </summary>
    public static ILogger CreateLogger(string componentName, string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(logDirectory, $"{componentName}-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
