using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekTools;

public static class Svn
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(Svn));

    public const string ExePath = "svn.exe";

    private class FileListArgument : IDisposable
    {
        public string Argument { get; }

        public override string ToString()
        {
            return Argument;
        }

        private readonly string _tempListFilePath = "";

        public FileListArgument(string[] paths, bool forceJoin)
        {
            // Windows 命令行参数长度限制为 8191，所以超过 8000 时，使用文件列表参数。
            if (forceJoin || paths.Sum(path => path.Length) < 8000)
            {
                Argument = string.Join(" ", paths.Select(NormalizePathArgument));
            }
            else
            {
                _tempListFilePath = Path.GetTempFileName();
                File.WriteAllLines(_tempListFilePath, paths);
                Argument = $"--targets \"{_tempListFilePath}\"";
            }
        }

        public void Dispose()
        {
            if (!string.IsNullOrEmpty(_tempListFilePath))
                File.Delete(_tempListFilePath);
        }
    }

    private static async Task<bool> Run(ProcessStartInfo startInfo)
    {
        NormalizeStartInfo(startInfo);

        using (var proc = Process.Start(startInfo))
        {
            if (proc == null)
                return false;

            await proc.WaitForExitAsync();

            return proc.ExitCode == 0;
        }
    }

    private static void NormalizeStartInfo(ProcessStartInfo startInfo)
    {
        // svn 输出语言会随系统变化，需要指定英文，才能正确解析。
        startInfo.Environment.Add("LANG", "en_US");
        // 因为修改环境变量，UseShellExecute 必须为 false。在 .NET Framework 上默认值是 true。
        startInfo.UseShellExecute = false;

        // 保证不会出现交互式提示
        if (string.IsNullOrEmpty(startInfo.Arguments))
            startInfo.Arguments = "--non-interactive";
        else
            startInfo.Arguments += " --non-interactive";
    }

    private static async Task<(bool, string)> RunWithProcessOutput(
        string arguments,
        string workingDirectory
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        NormalizeStartInfo(startInfo);

        using var proc = Process.Start(startInfo);
        if (proc == null)
        {
            Log.ZLogError(
                $"process is null: {startInfo.FileName} {startInfo.Arguments} in {startInfo.WorkingDirectory}"
            );
            return (false, "");
        }

        // 在某些电脑上会超时，ReadToEnd() 可以解决这个问题。
        var output = await proc.StandardOutput.ReadToEndAsync();

        // 超时杀进程，1s 出过问题，延长到 2s。
        if (!proc.WaitForExit(2000))
        {
            proc.Kill();
            Log.ZLogError(
                $"process timeout: {startInfo.FileName} status {startInfo.Arguments} in {startInfo.WorkingDirectory}"
            );
            return (false, output);
        }

        if (proc.ExitCode > 0)
        {
            Log.ZLogError(
                $"process exit code {proc.ExitCode}: {startInfo.FileName} status {startInfo.Arguments} in {startInfo.WorkingDirectory}"
            );
            return (false, output);
        }

        return (true, output);
    }

    private static async Task<bool> RunWithProcessOutput(
        string arguments,
        string workingDirectory,
        Func<string, bool> outputLineProcessor
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        NormalizeStartInfo(startInfo);

        using var proc = Process.Start(startInfo);
        if (proc == null)
        {
            Log.ZLogError(
                $"process is null: {startInfo.FileName} {startInfo.Arguments} in {startInfo.WorkingDirectory}"
            );
            return false;
        }

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
        {
            if (!outputLineProcessor(line ?? ""))
                break;
        }

        // 在某些电脑上会超时，ReadToEnd() 可以解决这个问题。
        await proc.StandardOutput.ReadToEndAsync();

        // 超时杀进程，1s 出过问题，延长到 2s。
        if (!proc.WaitForExit(2000))
        {
            proc.Kill();
            Log.ZLogError(
                $"process timeout: {startInfo.FileName} status {startInfo.Arguments} in {startInfo.WorkingDirectory}"
            );
            return false;
        }

        if (proc.ExitCode > 0)
        {
            Log.ZLogError(
                $"process exit code {proc.ExitCode}: {startInfo.FileName} status {startInfo.Arguments} in {startInfo.WorkingDirectory}"
            );
            return false;
        }

        return true;
    }

    public static bool RevertSync(string path, string arguments = "", string workingDirectory = "")
    {
        return AsyncHelper.RunSync(() => Revert(path, arguments, workingDirectory));
    }

    public static bool RevertSync(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() => Revert(paths, arguments, workingDirectory));
    }

    public static async Task<bool> Revert(
        string path,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return await Revert(new[] { path }, arguments, workingDirectory);
    }

    public static async Task<bool> Revert(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        if (paths.Length == 0)
            return false;

        using var fileListArg = new FileListArgument(paths, false);
        return await Run(
            new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = $"revert -R {fileListArg} {arguments}",
                WorkingDirectory = workingDirectory,
            }
        );
    }

    public static bool RevertInfinitySync(
        string path,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() => RevertInfinity(path, arguments, workingDirectory));
    }

    public static bool RevertInfinitySync(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() => RevertInfinity(paths, arguments, workingDirectory));
    }

    public static async Task<bool> RevertInfinity(
        string path,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return await RevertInfinity(new[] { path }, arguments, workingDirectory);
    }

    public static async Task<bool> RevertInfinity(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return await Revert(paths, arguments + " --depth infinity", workingDirectory);
    }

    public static bool RemoveUnversionedSync(
        string path,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() => RemoveUnversioned(path, arguments, workingDirectory));
    }

    public static bool RemoveUnversionedSync(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() => RemoveUnversioned(paths, arguments, workingDirectory));
    }

    public static async Task<bool> RemoveUnversioned(
        string path,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return await RemoveUnversioned(new[] { path }, arguments, workingDirectory);
    }

    public static async Task<bool> RemoveUnversioned(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return await CleanUp(paths, arguments + " --remove-unversioned", workingDirectory);
    }

    public static bool RevertAndRemoveUnversionedSync(
        string path,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() =>
            RevertAndRemoveUnversioned(path, arguments, workingDirectory)
        );
    }

    public static bool RevertAndRemoveUnversionedSync(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() =>
            RevertAndRemoveUnversioned(paths, arguments, workingDirectory)
        );
    }

    public static async Task<bool> RevertAndRemoveUnversioned(
        string path,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return await RevertAndRemoveUnversioned(new[] { path }, arguments, workingDirectory);
    }

    public static async Task<bool> RevertAndRemoveUnversioned(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return await Revert(paths, arguments, workingDirectory)
            && await RemoveUnversioned(paths, arguments, workingDirectory);
    }

    public static Dictionary<string, string>? GetFilesStatusSync(
        string path,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() => GetFilesStatus(path, arguments, workingDirectory));
    }

    public static Dictionary<string, string>? GetFilesStatusSync(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() => GetFilesStatus(paths, arguments, workingDirectory));
    }

    public static async Task<Dictionary<string, string>?> GetFilesStatus(
        string path,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return await GetFilesStatus(new[] { path }, arguments, workingDirectory);
    }

    public static async Task<Dictionary<string, string>?> GetFilesStatus(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        using var fileListArg = new FileListArgument(paths, true);
        var fileStatusDict = new Dictionary<string, string>();

        var runResult = await RunWithProcessOutput(
            $"status {fileListArg} {arguments}",
            workingDirectory,
            line =>
            {
                if (string.IsNullOrEmpty(line))
                    return false;

                if (line.Length < 8 || line[7] != ' ' || line[8] == ' ')
                    return true;

                fileStatusDict.Add(line.Substring(8).Replace('\\', '/'), line.Substring(0, 7));
                return true;
            }
        );

        return runResult ? fileStatusDict : null;
    }

    public static bool HasConflictStatus(string status)
    {
        return status[0] == 'C' || status[1] == 'C' || status[6] == 'C';
    }

    public static string GetBranchSync(string path)
    {
        return AsyncHelper.RunSync(() => GetBranch(path));
    }

    public static async Task<string> GetBranch(string path)
    {
        var svnInfo = await GetInfo(path);
        if (svnInfo == null)
            return "";
        var relativeUrl = svnInfo.RelativeUrl;

        if (relativeUrl.StartsWith("^/trunk"))
            return "trunk";

        if (relativeUrl.StartsWith("^/branches"))
        {
            var sepIndexBeforeBranchName = relativeUrl.IndexOf('/', 2);
            if (sepIndexBeforeBranchName != -1)
            {
                var beginOfBranchName = sepIndexBeforeBranchName + 1;
                var endOfBranchName = relativeUrl.IndexOf('/', beginOfBranchName);
                return endOfBranchName == -1
                    ? relativeUrl.Substring(beginOfBranchName)
                    : relativeUrl.Substring(beginOfBranchName, endOfBranchName - beginOfBranchName);
            }
        }

        return "";
    }

    public class SvnInfo
    {
        public string Path = "";
        public string WorkingCopyRootPath = "";
        public string Url = "";
        public string RelativeUrl = "";
        public string RepositoryRoot = "";
        public string RepositoryUuid = "";
        public string Revision = "";
        public string NodeKind = "";
        public string Schedule = "";
        public string LastChangedAuthor = "";
        public string LastChangedRev = "";
        public string LastChangedDate = "";
    }

    public static SvnInfo? GetInfoSync(string path)
    {
        return AsyncHelper.RunSync(() => GetInfo(path));
    }

    private static string NormalizePathArgument(string path)
    {
        // For root path like D:\, svn report error with double quotes.
        // So we need to normalize the path.
        if (path.EndsWith('\\'))
            return $"""
                "{path}\"
                """;
        return $"""
            "{path}"
            """;
    }

    public static async Task<SvnInfo?> GetInfo(string path)
    {
        var svnInfo = new SvnInfo();

        var runResult = await RunWithProcessOutput(
            $"info {NormalizePathArgument(path)}",
            "",
            line =>
            {
                if (string.IsNullOrEmpty(line))
                    return false;

                var sepIndex = line.IndexOf(":", StringComparison.Ordinal);
                if (sepIndex == -1)
                    return true;

                var key = line.Substring(0, sepIndex);
                var value = line.Substring(sepIndex + 1).Trim();
                switch (key)
                {
                    case "Path":
                        svnInfo.Path = value;
                        break;
                    case "Working Copy Root Path":
                        svnInfo.WorkingCopyRootPath = value;
                        break;
                    case "URL":
                        svnInfo.Url = value;
                        break;
                    case "Relative URL":
                        svnInfo.RelativeUrl = value;
                        break;
                    case "Repository Root":
                        svnInfo.RepositoryRoot = value;
                        break;
                    case "Repository UUID":
                        svnInfo.RepositoryUuid = value;
                        break;
                    case "Revision":
                        svnInfo.Revision = value;
                        break;
                    case "Node Kind":
                        svnInfo.NodeKind = value;
                        break;
                    case "Schedule":
                        svnInfo.Schedule = value;
                        break;
                    case "Last Changed Author":
                        svnInfo.LastChangedAuthor = value;
                        break;
                    case "Last Changed Rev":
                        svnInfo.LastChangedRev = value;
                        break;
                    case "Last Changed Date":
                        svnInfo.LastChangedDate = value;
                        break;
                }

                return true;
            }
        );

        return runResult ? svnInfo : null;
    }

    public static bool SetDepthSync(
        string path,
        string depth,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() => SetDepth(path, depth, arguments, workingDirectory));
    }

    public static bool SetDepthSync(
        string[] paths,
        string depth,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() => SetDepth(paths, depth, arguments, workingDirectory));
    }

    public static async Task<bool> SetDepth(
        string path,
        string depth,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return await SetDepth(new[] { path }, depth, arguments, workingDirectory);
    }

    public static async Task<bool> SetDepth(
        string[] paths,
        string depth,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        if (paths.Length == 0)
            return false;

        using (var fileListArg = new FileListArgument(paths, true))
        {
            return await Run(
                new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = $"update --set-depth {depth} {fileListArg} {arguments}",
                    WorkingDirectory = workingDirectory,
                }
            );
        }
    }

    public static void AddToPath(string svnExecutableDirectory)
    {
        var pathEnvVar = Environment.GetEnvironmentVariable("path");
        pathEnvVar += $";{svnExecutableDirectory}";
        Environment.SetEnvironmentVariable("path", pathEnvVar);
    }

    public static bool[] HasUpdateSync(string[] paths, string workingDirectory)
    {
        return AsyncHelper.RunSync(() => HasUpdate(paths, workingDirectory));
    }

    public static bool HasUpdateSync(string path, string workingDirectory)
    {
        return AsyncHelper.RunSync(() => HasUpdate(path, workingDirectory));
    }

    public static async Task<bool[]> HasUpdate(string[] paths, string workingDirectory)
    {
        using var fileListArg = new FileListArgument(paths, true);

        var (success, output) = await RunWithProcessOutput(
            $"status --show-updates --xml {fileListArg}",
            workingDirectory
        );
        if (!success)
            return new bool[paths.Length];

        var xml = XDocument.Parse(output);
        return xml.Element("status")
                ?.Elements("target")
                .Select(target =>
                    target.Element("entry")?.Element("repos-status")?.Attribute("item")?.Value
                    == "modified"
                )
                .ToArray() ?? new bool[paths.Length];
    }

    public static async Task<bool> HasUpdate(string path, string workingDirectory)
    {
        var result = await HasUpdate(new[] { path }, workingDirectory);
        return result[0];
    }

    private static bool ParseStatusLine(string line)
    {
        if (line.StartsWith("Status against revision:"))
            return false;
        if (line.Length < 9)
            return false;

        return line[8] == '*';
    }

    public static bool CleanUpSync(string path, string arguments = "", string workingDirectory = "")
    {
        return AsyncHelper.RunSync(() => CleanUp(path, arguments, workingDirectory));
    }

    public static bool CleanUpSync(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return AsyncHelper.RunSync(() => CleanUp(paths, arguments, workingDirectory));
    }

    public static async Task<bool> CleanUp(
        string path,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return await CleanUp(new[] { path }, arguments, workingDirectory);
    }

    public static async Task<bool> CleanUp(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        if (paths.Length == 0)
            return false;

        using (var fileListArg = new FileListArgument(paths, true))
        {
            return await Run(
                new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = $"cleanup {arguments} {fileListArg}",
                    WorkingDirectory = workingDirectory,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                }
            );
        }
    }

    public static int GetCurrentRevisionSync(string path)
    {
        return AsyncHelper.RunSync(() => GetCurrentRevision(path));
    }

    public static async Task<int> GetCurrentRevision(string path)
    {
        var result = -1;
        await RunWithProcessOutput(
            $"info --show-item last-changed-revision {NormalizePathArgument(path)}",
            "",
            line =>
            {
                if (int.TryParse(line, out var value))
                    result = value;
                return false;
            }
        );
        return result;
    }

    public static int GetLatestRevisionSync(string path)
    {
        return AsyncHelper.RunSync(() => GetLatestRevision(path));
    }

    public static async Task<int> GetLatestRevision(string path)
    {
        var result = -1;
        await RunWithProcessOutput(
            $"info -r HEAD --show-item last-changed-revision {NormalizePathArgument(path)}",
            "",
            line =>
            {
                if (int.TryParse(line, out var value))
                    result = value;
                return false;
            }
        );
        return result;
    }

    public static bool RelocateSync(string svnRootDirectory, string oldUrl, string newUrl)
    {
        return AsyncHelper.RunSync(() => Relocate(svnRootDirectory, oldUrl, newUrl));
    }

    public static async Task<bool> Relocate(string svnRootDirectory, string oldUrl, string newUrl)
    {
        using var proc = Process.Start(
            new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = $"switch --relocate {oldUrl} {newUrl}",
                WorkingDirectory = svnRootDirectory,
                UseShellExecute = true,
            }
        );

        if (proc == null)
            return false;

        await proc.WaitForExitAsync();

        return proc.ExitCode == 0;
    }

    public static bool AddSync(string path, string arguments = "", string workingDirectory = "")
    {
        return AsyncHelper.RunSync(() => Add(path, arguments, workingDirectory));
    }

    public static bool AddSync(string[] paths, string arguments = "", string workingDirectory = "")
    {
        return AsyncHelper.RunSync(() => Add(paths, arguments, workingDirectory));
    }

    public static async Task<bool> Add(
        string path,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        return await Revert(new[] { path }, arguments, workingDirectory);
    }

    public static async Task<bool> Add(
        string[] paths,
        string arguments = "",
        string workingDirectory = ""
    )
    {
        if (paths.Length == 0)
            return false;

        using var fileListArg = new FileListArgument(paths, false);
        return await Run(
            new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = $"add {fileListArg} {arguments} --parents",
                WorkingDirectory = workingDirectory,
            }
        );
    }
}
