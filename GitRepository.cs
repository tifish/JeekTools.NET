using System.Diagnostics;

namespace JeekTools;

public class GitRepository
{
    private const string ExePath = "git.exe";
    private static readonly TimeSpan QuickCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LocalCommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NetworkCommandTimeout = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    public string RootPath { get; }

    public GitRepository(string rootPath)
    {
        RootPath = rootPath;
    }

    public string LastOutput { get; private set; } = "";
    public string LastError { get; private set; } = "";

    public async Task<bool> RunGitCommand(
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        int retryCount = 0
    )
    {
        try
        {
            await _commandLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LastError = "Git 操作已取消。";
            return false;
        }

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var success = await RunGitCommandOnce(
                    arguments,
                    timeout ?? LocalCommandTimeout,
                    cancellationToken
                );
                if (success || attempt >= retryCount || !IsTransientNetworkError(LastError))
                    return success;

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            LastError = "Git 操作已取消。";
            return false;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<bool> RunGitCommandOnce(
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        LastOutput = "";
        LastError = "";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = arguments,
                WorkingDirectory = RootPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                EnvironmentVariables =
                {
                    ["GIT_TERMINAL_PROMPT"] = "0",
                    ["GCM_INTERACTIVE"] = "Never",
                    ["SSH_ASKPASS_REQUIRE"] = "never",
                },
            },
        };

        try
        {
            if (!process.Start())
            {
                LastError = $"无法启动 {ExePath}。";
                return false;
            }
        }
        catch (Exception ex)
        {
            LastError = $"无法启动 {ExePath}：{ex.Message}";
            return false;
        }

        // 必须在等待进程退出前持续读取两个重定向管道，否则大量输出会填满管道并造成死锁。
        var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token
        );

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await WaitForExitAfterKill(process);
            LastOutput = await ReadCompletedOutput(outputTask);
            var commandError = await ReadCompletedOutput(errorTask);

            if (cancellationToken.IsCancellationRequested)
                LastError = JoinError("Git 操作已取消。", commandError);
            else
                LastError = JoinError(
                    $"Git 命令执行超过 {FormatTimeout(timeout)}，已终止进程。",
                    commandError
                );

            return false;
        }

        LastOutput = await outputTask;
        var standardError = await errorTask;

        if (process.ExitCode == 0)
            return true;

        LastError = string.IsNullOrWhiteSpace(standardError)
            ? LastOutput.Trim()
            : standardError.Trim();
        if (string.IsNullOrWhiteSpace(LastError))
            LastError = $"Git 命令失败，退出代码：{process.ExitCode}。";
        return false;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch
        {
            // 进程可能恰好已经退出，后续仍会尝试回收输出。
        }
    }

    private static async Task WaitForExitAfterKill(Process process)
    {
        try
        {
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(waitCts.Token);
        }
        catch
        {
            // 终止失败时不能让清理流程再次无限等待。
        }
    }

    private static async Task<string> ReadCompletedOutput(Task<string> outputTask)
    {
        try
        {
            return await outputTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            return "";
        }
    }

    private static string JoinError(string message, string commandError)
    {
        commandError = commandError.Trim();
        return string.IsNullOrEmpty(commandError) ? message : $"{message}{Environment.NewLine}{commandError}";
    }

    private static string FormatTimeout(TimeSpan timeout)
    {
        return timeout.TotalMinutes >= 1
            ? $"{timeout.TotalMinutes:0.#} 分钟"
            : $"{timeout.TotalSeconds:0.#} 秒";
    }

    private static bool IsTransientNetworkError(string error)
    {
        string[] transientErrors =
        [
            "connection closed",
            "connection reset",
            "connection timed out",
            "could not resolve host",
            "early eof",
            "failed to connect",
            "network is unreachable",
            "remote end hung up",
            "rpc failed",
            "the requested url returned error: 5",
            "tls connection",
            "unable to access",
        ];

        return transientErrors.Any(item => error.Contains(item, StringComparison.OrdinalIgnoreCase));
    }

    public Task<bool> Fetch(CancellationToken cancellationToken = default)
    {
        return RunGitCommand(
            "-c http.lowSpeedLimit=1 -c http.lowSpeedTime=60 fetch --prune --recurse-submodules=on-demand --progress",
            NetworkCommandTimeout,
            cancellationToken,
            retryCount: 1
        );
    }

    public Task<bool> Rebase(CancellationToken cancellationToken = default)
    {
        return RunGitCommand("rebase @{upstream}", LocalCommandTimeout, cancellationToken);
    }

    public Task<bool> UpdateSubmodules(CancellationToken cancellationToken = default)
    {
        return RunGitCommand(
            "-c http.lowSpeedLimit=1 -c http.lowSpeedTime=60 submodule update --init --recursive --progress",
            NetworkCommandTimeout,
            cancellationToken,
            retryCount: 1
        );
    }

    public async Task<bool> PullRebaseWithSubmodules(
        CancellationToken cancellationToken = default
    )
    {
        return await Fetch(cancellationToken)
            && await Rebase(cancellationToken)
            && await UpdateSubmodules(cancellationToken);
    }

    public async Task<bool> AbortRebaseIfNeeded()
    {
        var gitDirectory = await GetGitPath("rebase-merge");
        var applyDirectory = await GetGitPath("rebase-apply");
        if (
            (string.IsNullOrEmpty(gitDirectory) || !Directory.Exists(gitDirectory))
            && (string.IsNullOrEmpty(applyDirectory) || !Directory.Exists(applyDirectory))
        )
            return true;

        return await RunGitCommand("rebase --abort", LocalCommandTimeout);
    }

    public async Task<bool> HasOngoingOperation(CancellationToken cancellationToken = default)
    {
        string[] operationPaths =
        [
            "rebase-merge",
            "rebase-apply",
            "MERGE_HEAD",
            "CHERRY_PICK_HEAD",
            "REVERT_HEAD",
            "BISECT_LOG",
        ];

        foreach (var operationPath in operationPaths)
        {
            var path = await GetGitPath(operationPath, cancellationToken);
            if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
            {
                LastError = "仓库中存在未完成的合并、变基或其他 Git 操作，请先手动处理后再更新。";
                return true;
            }
        }

        LastError = "";
        return false;
    }

    private async Task<string> GetGitPath(
        string pathName,
        CancellationToken cancellationToken = default
    )
    {
        var success = await RunGitCommand(
            $"rev-parse --git-path {pathName}",
            QuickCommandTimeout,
            cancellationToken
        );
        if (!success)
            return "";

        var path = LastOutput.Trim();
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(path, RootPath);
    }

    public Task<bool> StashSave(CancellationToken cancellationToken = default)
    {
        return RunGitCommand(
            $"stash push --include-untracked --message \"Jx3mHelperTray 自动更新 {DateTime.Now:yyyy-MM-dd HH:mm:ss}\"",
            LocalCommandTimeout,
            cancellationToken
        );
    }

    public async Task<string> GetStashId(CancellationToken cancellationToken = default)
    {
        var success = await RunGitCommand(
            "rev-parse --verify refs/stash",
            QuickCommandTimeout,
            cancellationToken
        );
        var stashId = success ? LastOutput.Trim() : "";

        // 没有 stash 是正常状态，不把 rev-parse 的退出代码 1 暴露成更新错误。
        if (!success)
            LastError = "";
        return stashId;
    }

    public async Task<bool> StashPop(
        string expectedStashId,
        CancellationToken cancellationToken = default
    )
    {
        var currentStashId = await GetStashId(cancellationToken);
        if (!string.Equals(currentStashId, expectedStashId, StringComparison.OrdinalIgnoreCase))
        {
            LastError =
                "自动更新创建的 stash 已不是最新一项。为避免恢复错误内容，已保留该 stash，请手动恢复。";
            return false;
        }

        return await RunGitCommand("stash pop --index", LocalCommandTimeout, cancellationToken);
    }

    public Task<bool> StashPop(CancellationToken cancellationToken = default)
    {
        return RunGitCommand("stash pop --index", LocalCommandTimeout, cancellationToken);
    }

    public async Task<string> GetCurrentBranch()
    {
        var result = await RunGitCommand("branch --show-current", QuickCommandTimeout);
        return result ? LastOutput.Trim() : "";
    }

    public struct GitFileStatus
    {
        public char IndexStatus = ' ';
        public char WorkingTreeStatus = ' ';
        public string Path = "";

        public GitFileStatus() { }
    }

    public Task<GitFileStatus[]> GetFilesStatus(params string[] paths)
    {
        return GetFilesStatus(CancellationToken.None, paths);
    }

    public async Task<GitFileStatus[]> GetFilesStatus(
        CancellationToken cancellationToken,
        params string[] paths
    )
    {
        var success = await RunGitCommand(
            "status --porcelain --untracked-files=all",
            QuickCommandTimeout,
            cancellationToken
        );
        if (!success)
            return [];

        return GetFilesStatusFromLastOutput();
    }

    private GitFileStatus[] GetFilesStatusFromLastOutput()
    {
        var lines = LastOutput.Split("\n").Where(line => !string.IsNullOrEmpty(line)).ToList();
        var statusList = new GitFileStatus[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            ref var status = ref statusList[i];
            status.IndexStatus = line[0];
            status.WorkingTreeStatus = line[1];
            status.Path = line[3..].TrimEnd('\r');
        }

        return statusList;
    }

    public Task<bool> Push(CancellationToken cancellationToken = default)
    {
        return RunGitCommand(
            "-c http.lowSpeedLimit=1 -c http.lowSpeedTime=60 push --progress",
            NetworkCommandTimeout,
            cancellationToken
        );
    }

    public async Task<string> GetRemoteUrl()
    {
        var result = await RunGitCommand("remote get-url origin", QuickCommandTimeout);
        return result ? LastOutput.Trim() : "";
    }

    public string GetRemoteUrlSync()
    {
        return AsyncHelper.RunSync(() => GetRemoteUrl());
    }
}
