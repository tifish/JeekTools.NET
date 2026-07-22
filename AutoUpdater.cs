using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekTools;

public enum UpdateCheckOutcome
{
    Available,
    UpToDate,
    Failed,
}

public sealed record UpdateDownloadProgress(
    int MirrorIndex,
    int MirrorCount,
    long ReceivedBytes,
    long? TotalBytes,
    double BytesPerSecond);

public sealed class AutoUpdaterOptions
{
    /// <summary>File name of the app executable, e.g. "MyApp.exe". Used to verify
    /// the staged package and to derive temp file names.</summary>
    public required string AppExeName { get; init; }

    /// <summary>Fixed download address of the latest release zip, e.g.
    /// "https://github.com/user/Repo/releases/download/latest_release/Repo.zip".</summary>
    public required string ReleaseZipUrl { get; init; }

    /// <summary>Address of version.txt next to the release zip. It contains the
    /// commit count baked into the release build's assembly major version.</summary>
    public required string VersionTxtUrl { get; init; }

    /// <summary>Name of the PowerShell script (next to the app executable) that
    /// swaps the staged files in and restarts the app.</summary>
    public string UpdateScriptName { get; init; } = "AutoUpdate.ps1";

    public string UserAgent { get; init; } = "AutoUpdater/1.0";

    /// <summary>Disables checking and installing entirely, e.g. for Debug builds.</summary>
    public bool Disabled { get; init; }

    /// <summary>Root directory for staging downloads; defaults to <see cref="Path.GetTempPath"/>.
    /// Point parallel debug instances at isolated roots so they never fight over temp files.</summary>
    public string? TempRoot { get; init; }

    /// <summary>Returns the local build number; defaults to the entry assembly's
    /// major version (CI bakes the commit count in as the major version).</summary>
    public Func<int>? GetLocalVersion { get; init; }

    /// <summary>Local versions below this are treated as dev builds and never updated.
    /// CI commit counts are always well above it.</summary>
    public int MinimumValidLocalVersion { get; init; } = 10;
}

/// <summary>
/// Checks GitHub Releases for a newer build, downloads and stages the update
/// package in-app (so a failed download never leaves the user without a running
/// app), and finally hands the verified staged folder to the PowerShell updater
/// that swaps the files on disk and restarts the app.
/// The build number is the commit count, baked in by CI as the assembly's
/// major version.
///
/// Downloads are routed through the fastest reachable GitHub mirror (see
/// <see cref="GitHubMirrors"/>) so updates keep working where github.com is blocked.
/// </summary>
public sealed class AutoUpdater
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(AutoUpdater));

    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(5);

    // Mirror-switching policy: abandon a mirror that stalls completely for
    // DownloadIdleTimeout, and — as long as another mirror remains — one that
    // stays below MinimumDownloadBytesPerSecond for a full SlowDownloadWindow.
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SlowDownloadWindow = TimeSpan.FromSeconds(10);
    private const long MinimumDownloadBytesPerSecond = 512 * 1024;

    private readonly AutoUpdaterOptions _options;
    private readonly string _appName;

    public AutoUpdater(AutoUpdaterOptions options)
    {
        _options = options;
        _appName = Path.GetFileNameWithoutExtension(options.AppExeName);
        DownloadUrl = options.ReleaseZipUrl;
        DownloadUrls = [options.ReleaseZipUrl];
    }

    public string DownloadUrl { get; private set; }
    public IReadOnlyList<string> DownloadUrls { get; private set; }
    public int LocalVersion { get; private set; }
    public int RemoteVersion { get; private set; }
    public string FailureReason { get; private set; } = "";

    public IReadOnlyList<string> GetDefaultDownloadUrls() => GitHubMirrors.GetMirrors(_options.ReleaseZipUrl);

    public int GetLocalVersion()
    {
        try
        {
            if (_options.GetLocalVersion is not null)
                return _options.GetLocalVersion();

            return Assembly.GetEntryAssembly()?.GetName().Version?.Major ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    public async Task<UpdateCheckOutcome> HasUpdateAsync()
    {
        DownloadUrl = _options.ReleaseZipUrl;
        DownloadUrls = [_options.ReleaseZipUrl];
        RemoteVersion = 0;
        FailureReason = "";
        LocalVersion = GetLocalVersion();

        if (_options.Disabled)
            return Fail("updates are disabled for this build");

        try
        {
            // Race version.txt mirrors directly. The first successful response
            // gives us both the remote version and the preferred release mirror,
            // avoiding a separate probe and a second version.txt request.
            var versionCheck = await DownloadFirstVersionTextAsync().ConfigureAwait(false);
            if (versionCheck is null)
                return Fail("version.txt unavailable or invalid from all mirrors");

            RemoteVersion = versionCheck.RemoteVersion;

            DownloadUrl = versionCheck.DownloadUrl;
            DownloadUrls = BuildDownloadUrls(DownloadUrl);

            if (LocalVersion < _options.MinimumValidLocalVersion)
                return Fail("local version unavailable (dev build?)");

            if (RemoteVersion > LocalVersion)
            {
                Log.ZLogInformation($"Update available: local={LocalVersion}, remote={RemoteVersion}, url={DownloadUrl}");
                return UpdateCheckOutcome.Available;
            }

            Log.ZLogInformation($"Already up to date: local={LocalVersion}, remote={RemoteVersion}");
            return UpdateCheckOutcome.UpToDate;
        }
        catch (Exception ex)
        {
            return Fail($"exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads the update package (trying each mirror in order), extracts it
    /// to a staging folder, and verifies it contains the app executable.
    /// Returns the staged package directory, or null with
    /// <see cref="FailureReason"/> set. Safe to call while the app keeps running.
    /// </summary>
    public async Task<string?> DownloadAndStageAsync(
        IReadOnlyList<string>? urls = null,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var mirrors = (urls ?? DownloadUrls)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct()
            .ToArray();
        if (mirrors.Length == 0)
        {
            FailureReason = "no download URLs";
            return null;
        }

        var tempRoot = string.IsNullOrEmpty(_options.TempRoot) ? Path.GetTempPath() : _options.TempRoot;
        var zipPath = Path.Combine(tempRoot, $"{_appName}-update.zip");
        var stageRoot = Path.Combine(tempRoot, $"{_appName}-update");
        var stageDir = Path.Combine(stageRoot, "package");

        try
        {
            TryDelete(() => Directory.Delete(stageRoot, recursive: true));
            TryDelete(() => File.Delete(zipPath));
            Directory.CreateDirectory(stageDir);

            var downloaded = false;
            var lastError = "";
            for (var i = 0; i < mirrors.Length; i++)
            {
                TryDelete(() => File.Delete(zipPath));

                // Give up early on a slow mirror only while a faster fallback
                // is still available; the last mirror may crawl to the finish.
                var minimumSpeed = i < mirrors.Length - 1 ? MinimumDownloadBytesPerSecond : 0;
                try
                {
                    await DownloadFileAsync(mirrors[i], zipPath, minimumSpeed, i, mirrors.Length, progress, cancellationToken)
                        .ConfigureAwait(false);
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Log.ZLogWarning($"Update download failed from {mirrors[i]}: {ex.Message}");
                }
            }

            if (!downloaded)
            {
                FailureReason = $"download failed from all mirrors: {lastError}";
                Log.ZLogWarning($"Update download failed: {FailureReason}");
                Cleanup();
                return null;
            }

            ZipFile.ExtractToDirectory(zipPath, stageDir, overwriteFiles: true);
            TryDelete(() => File.Delete(zipPath));

            if (!File.Exists(Path.Combine(stageDir, _options.AppExeName)))
            {
                FailureReason = $"update package is missing {_options.AppExeName}";
                Log.ZLogWarning($"Update download failed: {FailureReason}");
                Cleanup();
                return null;
            }

            Log.ZLogInformation($"Update staged at {stageDir}");
            return stageDir;
        }
        catch (OperationCanceledException)
        {
            FailureReason = "download cancelled";
            Cleanup();
            return null;
        }
        catch (Exception ex)
        {
            FailureReason = ex.Message;
            Log.ZLogError(ex, $"Failed to download and stage update");
            Cleanup();
            return null;
        }

        void Cleanup()
        {
            TryDelete(() => File.Delete(zipPath));
            TryDelete(() => Directory.Delete(stageRoot, recursive: true));
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        long minimumBytesPerSecond,
        int mirrorIndex,
        int mirrorCount,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Stalls are policed per-read below, so the client itself never times out.
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);

        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerCts.CancelAfter(DownloadIdleTimeout);
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headerCts.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        var totalBytes = response.Content.Headers.ContentLength;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Open(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[1024 * 1024];
        long received = 0;
        var stopwatch = Stopwatch.StartNew();
        long windowReceived = 0;
        var speedWindow = Stopwatch.StartNew();
        // Slightly negative so the very first chunk reports immediately.
        var lastReport = TimeSpan.FromMilliseconds(-200);

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .AsTask()
                    .WaitAsync(DownloadIdleTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"No download data received for {DownloadIdleTimeout.TotalSeconds:0} seconds.");
            }

            if (read <= 0)
                break;

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            windowReceived += read;

            if (minimumBytesPerSecond > 0 && speedWindow.Elapsed >= SlowDownloadWindow)
            {
                var windowBytesPerSecond = windowReceived / speedWindow.Elapsed.TotalSeconds;
                if (windowBytesPerSecond < minimumBytesPerSecond)
                {
                    throw new InvalidOperationException(
                        $"Download speed stayed below {minimumBytesPerSecond / 1024} KB/s " +
                        $"for {SlowDownloadWindow.TotalSeconds:0} seconds " +
                        $"(current: {windowBytesPerSecond / 1024:0} KB/s).");
                }

                windowReceived = 0;
                speedWindow.Restart();
            }

            if (progress != null && (stopwatch.Elapsed - lastReport).TotalMilliseconds >= 200)
            {
                lastReport = stopwatch.Elapsed;
                var speed = received / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.1);
                progress.Report(new UpdateDownloadProgress(mirrorIndex, mirrorCount, received, totalBytes, speed));
            }
        }

        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (totalBytes is > 0 && received < totalBytes)
            throw new InvalidOperationException($"Download ended early: {received} of {totalBytes} bytes.");

        var finalSpeed = received / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.1);
        progress?.Report(new UpdateDownloadProgress(mirrorIndex, mirrorCount, received, totalBytes, finalSpeed));
        Log.ZLogInformation($"Downloaded {received} bytes from {url} in {stopwatch.Elapsed.TotalSeconds:0}s");
    }

    /// <summary>
    /// Launches the PowerShell updater with a staged package produced by
    /// <see cref="DownloadAndStageAsync"/>. The script waits for the app to
    /// exit, swaps the files, and restarts the app.
    /// </summary>
    public bool LaunchInstall(string stagedPackageDir)
    {
        if (_options.Disabled)
            return false;

        try
        {
            if (!File.Exists(Path.Combine(stagedPackageDir, _options.AppExeName)))
            {
                Log.ZLogWarning($"Staged package is missing {_options.AppExeName}: {stagedPackageDir}");
                return false;
            }

            var exePath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(exePath))
                return false;

            var workDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(workDir))
                return false;

            var scriptPath = Path.Combine(workDir, _options.UpdateScriptName);
            if (!File.Exists(scriptPath))
            {
                Log.ZLogWarning($"Updater script not found: {scriptPath}");
                return false;
            }

            Log.ZLogInformation($"Launching updater for staged package {stagedPackageDir}");
            var updateArguments = new[]
                {
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    scriptPath,
                    stagedPackageDir,
                }
                .Select(QuoteProcessArgument);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = string.Join(" ", updateArguments),
                WorkingDirectory = workDir,
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to launch updater");
            return false;
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch
        {
            // Best-effort cleanup of temp files.
        }
    }

    private string[] BuildDownloadUrls(string preferredUrl)
    {
        return GitHubMirrors.GetMirrors(_options.ReleaseZipUrl)
            .OrderBy(url => string.Equals(url, preferredUrl, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();
    }

    private sealed record VersionCheckResult(string DownloadUrl, int RemoteVersion);

    private async Task<VersionCheckResult?> DownloadFirstVersionTextAsync()
    {
        var versionUrls = GitHubMirrors.GetMirrors(_options.VersionTxtUrl);
        var downloadUrls = GitHubMirrors.GetMirrors(_options.ReleaseZipUrl);
        using var cts = new CancellationTokenSource();
        var tasks = versionUrls
            .Select((url, index) => DownloadVersionTextAsync(url, downloadUrls[index], cts.Token))
            .ToList();

        try
        {
            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(finished);
                var result = await finished.ConfigureAwait(false);
                if (result is null)
                    continue;

                cts.Cancel();
                return result;
            }
        }
        finally
        {
            cts.Cancel();
        }

        return null;
    }

    private async Task<VersionCheckResult?> DownloadVersionTextAsync(
        string versionUrl,
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        var text = await DownloadTextAsync(versionUrl, VersionCheckTimeout, cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(text?.Trim(), out var remoteVersion) || remoteVersion <= 0)
            return null;

        return new VersionCheckResult(downloadUrl, remoteVersion);
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private UpdateCheckOutcome Fail(string reason)
    {
        FailureReason = reason;
        Log.ZLogWarning($"Update check failed: {reason}");
        return UpdateCheckOutcome.Failed;
    }

    private async Task<string?> DownloadTextAsync(
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
