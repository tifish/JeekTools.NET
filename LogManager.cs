using System.Reflection;
using Microsoft.Extensions.Logging;
using Utf8StringInterpolation;
using ZLogger;
using ZLogger.Providers;

namespace JeekTools;

/// <summary>
///     Usage:
///     private static readonly ILogger Log = LogManager.CreateLogger(nameof(ClassName));
///     Log.ZLogInformation($"Count: {count}");
/// </summary>
public static class LogManager
{
    private static ILoggerFactory? _factory;

    public static ILogger CreateLogger<T>()
    {
        if (_factory == null)
            EnableLogging();

        return _factory!.CreateLogger<T>();
    }

    public static ILogger CreateLogger(string categoryName)
    {
        if (_factory == null)
            EnableLogging();

        return _factory!.CreateLogger(categoryName);
    }

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public static bool ToConsole { get; set; }
    public static bool ToFile { get; set; } = true;
    public static TimeSpan RetainFileLimit { get; set; } = TimeSpan.FromDays(7);
    public static string LogsDirectory { get; set; } = "Logs";
    public static RollingInterval RollingInterval { get; set; } = RollingInterval.Day;
    public static int RollingSizeKB { get; set; } = 1024;

    public static void EnableLogging()
    {
        _factory?.Dispose();

        _factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(MinimumLevel);

            if (ToConsole)
                logging.AddZLoggerConsole(UsePlaitText);

            if (ToFile)
            {
                var appName = Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly()?.Location) ?? "Unknown";
                var logsDir = Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase ?? string.Empty, LogsDirectory);

                // For each file in logsDir, if the file time is older than Now - RetainFileLimit, delete it.
                if (Directory.Exists(logsDir))
                {
                    var now = DateTime.Now;
                    foreach (var file in Directory.EnumerateFiles(logsDir, $"{appName}_*.log"))
                    {
                        var fileTime = File.GetLastWriteTime(file);
                        if (now - fileTime > RetainFileLimit)
                            try
                            {
                                File.Delete(file);
                            }
                            catch
                            {
                                // ignored
                            }
                    }
                }

                logging.AddZLoggerRollingFile(options =>
                {
                    options.FilePathSelector = (timestamp, sequenceNumber) =>
                    {
                        CurrentLogFile = Path.Join(logsDir, $"{appName}_{timestamp.ToLocalTime():yyyy-MM-dd_HH-mm-ss}_{sequenceNumber}.log");
                        return CurrentLogFile;
                    };
                    options.RollingInterval = RollingInterval;
                    options.RollingSizeKB = RollingSizeKB;
                    UsePlaitText(options);
                });
            }

            void UsePlaitText(ZLoggerOptions options)
            {
                options.UsePlainTextFormatter(formatter =>
                {
                    formatter.SetPrefixFormatter($"{0} [{1:short}] [{2}] ",
                        (in MessageTemplate template, in LogInfo info) =>
                            template.Format(info.Timestamp, info.LogLevel, info.Category));
                    formatter.SetExceptionFormatter((writer, ex)
                        => Utf8String.Format(writer, $"\n{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace ?? ""}"));
                });
            }
        });
    }

    public static void DisableLogging()
    {
        _factory?.Dispose();
        _factory = LoggerFactory.Create(logging => { logging.ClearProviders(); });
    }

    public static string CurrentLogFile { get; private set; } = "";
}
