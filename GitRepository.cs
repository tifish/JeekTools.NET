using System.Diagnostics;

namespace JeekTools;

public class GitRepository
{
    private const string ExePath = "git.exe";

    public string RootPath { get; }

    public GitRepository(string rootPath)
    {
        RootPath = rootPath;
    }

    public string LastOutput { get; private set; } = "";
    public string LastError { get; private set; } = "";

    public async Task<bool> RunGitCommand(string arguments)
    {
        using var process = Process.Start(
            new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = arguments,
                WorkingDirectory = RootPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                EnvironmentVariables = { ["GIT_TERMINAL_PROMPT"] = "0" },
            }
        );

        if (process == null)
            return false;

        await process.WaitForExitAsync();

        LastOutput = await process.StandardOutput.ReadToEndAsync();

        if (process.ExitCode == 0)
            return true;

        LastError = await process.StandardError.ReadToEndAsync();
        return false;
    }

    public async Task<bool> Fetch()
    {
        return await RunGitCommand("fetch");
    }

    public async Task<bool> Rebase()
    {
        return await RunGitCommand("rebase");
    }

    public async Task<bool> PullRebaseWithSubmodules()
    {
        return await RunGitCommand("pull --rebase --recurse-submodules");
    }

    public async Task<bool> StashSave()
    {
        return await RunGitCommand("stash save");
    }

    public async Task<bool> StashPop()
    {
        return await RunGitCommand("stash pop");
    }

    public async Task<string> GetCurrentBranch()
    {
        var result = await RunGitCommand("branch --show-current");
        return result ? LastOutput.Trim() : "";
    }

    public struct GitFileStatus
    {
        public char IndexStatus = ' ';
        public char WorkingTreeStatus = ' ';
        public string Path = "";

        public GitFileStatus() { }
    }

    public async Task<GitFileStatus[]> GetFilesStatus(params string[] paths)
    {
        var success = await RunGitCommand("status --porcelain");
        if (!success)
            return Array.Empty<GitFileStatus>();

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
            status.Path = line[3..];
        }

        return statusList;
    }

    public async Task<bool> Push()
    {
        return await RunGitCommand("push");
    }
}
