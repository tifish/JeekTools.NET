using System.Reflection;
using System.Runtime.InteropServices;
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
    private static readonly object AliasUpdateLock = new();

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

    // Big log file is easy to search and analyze.
    // VSCode will disable highlight when file bigger than 1024 MB, so set it to 1000 MB.
    public static int RollingSizeKB { get; set; } = 1000 * 1024;

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
                var appName =
                    Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly()?.Location)
                    ?? "Unknown";
                var logsDir = Path.Combine(
                    AppDomain.CurrentDomain.SetupInformation.ApplicationBase ?? string.Empty,
                    LogsDirectory
                );
                Directory.CreateDirectory(logsDir);
                var currentAliasPath = Path.Join(logsDir, $"{appName}.log");

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
                        var rollingPath = Path.Join(
                            logsDir,
                            $"{appName}_{timestamp.ToLocalTime():yyyy-MM-dd_HH-mm-ss}_{sequenceNumber}.log"
                        );
                        CurrentRollingLogFile = rollingPath;
                        CurrentLogFile = currentAliasPath; // stable name for "current log"

                        // Best-effort: create/update alias to current rolling file (hardlink only).
                        TryUpdateCurrentLogAlias(currentAliasPath, rollingPath);

                        return rollingPath;
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
                    formatter.SetPrefixFormatter(
                        $"{0} [{1:short}] [{2}] ",
                        (in MessageTemplate template, in LogInfo info) =>
                            template.Format(info.Timestamp, info.LogLevel, info.Category)
                    );
                    formatter.SetExceptionFormatter(
                        (writer, ex) =>
                            Utf8String.Format(
                                writer,
                                $"\n{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace ?? ""}"
                            )
                    );
                });
            }
        });
    }

    public static void DisableLogging()
    {
        _factory?.Dispose();
        _factory = LoggerFactory.Create(logging =>
        {
            logging.ClearProviders();
        });
    }

    /// <summary>
    /// A stable file path without timestamp suffix, e.g. Logs/AppName.log.
    /// It points to the current rolling log file (via symlink/hardlink best-effort).
    /// </summary>
    public static string CurrentLogFile { get; private set; } = "";

    /// <summary>
    /// The actual rolling log file path with timestamp suffix, e.g. Logs/AppName_yyyy-MM-dd_HH-mm-ss_0.log.
    /// </summary>
    public static string CurrentRollingLogFile { get; private set; } = "";

    private static void TryUpdateCurrentLogAlias(string aliasPath, string targetPath)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                // Retry until the rolling file is created by ZLogger.
                for (var i = 0; i < 50; i++)
                {
                    if (File.Exists(targetPath))
                    {
                        lock (AliasUpdateLock)
                        {
                            TryDeleteFileBestEffort(aliasPath);
                            if (CreateHardLink(aliasPath, targetPath, IntPtr.Zero))
                                return;
                        }
                    }

                    await Task.Delay(100).ConfigureAwait(false);
                }
            });
        }
        catch
        {
            // Never let alias maintenance break logging.
        }
    }

    private static void TryDeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes
    );
}
