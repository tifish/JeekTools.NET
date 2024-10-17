using System.Diagnostics;
using System.Text;

namespace JeekTools;

public static class PlasticScm
{
    public static async Task<string> Run(string arguments, string workingDirectory)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // Output encoding of cm.exe varies with system locale, so we need to set it to UTF-8 manually
            // Even more: In Windows 10 GBK the output is UTF-8, as in Windows 11 is GBK.
            Arguments = $"/s /c \"chcp 65001 & cm.exe {arguments}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        });

        if (process == null)
            return "";

        await process.StandardOutput.ReadLineAsync(); // skip chcp output
        return await process.StandardOutput.ReadToEndAsync();
    }

    public static async Task<(string, string)> RunWithError(string arguments, string workingDirectory)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // Output encoding of cm.exe varies with system locale, so we need to set it to UTF-8 manually
            // Even more: In Windows 10 GBK the output is UTF-8, as in Windows 11 is GBK.
            Arguments = $"/s /c \"chcp 65001 & cm.exe {arguments}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        });

        if (process == null)
            return ("", "");

        await process.StandardOutput.ReadLineAsync(); // skip chcp output
        return (await process.StandardOutput.ReadToEndAsync(), await process.StandardError.ReadToEndAsync());
    }

    public static async Task<string> GetCurrentBranch(string workingDirectory)
    {
        var output = await Run("showselector", workingDirectory);
        if (output == "")
            return "";

        using var reader = new StringReader(output);
        var line = await reader.ReadLineAsync();
        while (line != null)
        {
            line = line.Trim();
            if (line.StartsWith("br "))
                return line[3..].Trim('"');
            if (line.StartsWith("smartbranch "))
                return line[11..].Trim('"');

            line = await reader.ReadLineAsync();
        }

        return "";
    }

    public static async Task<bool> IsPartial(string workingDirectory)
    {
        var output = await Run("status --header --machinereadable", workingDirectory);
        var items = output.Split(' ');
        if (items.Length < 2)
            return false;
        if (!int.TryParse(items[1], out var changeSet))
            return false;

        return changeSet == -1;
    }

    public class LockFileInfo
    {
        public string ID = "";
        public string Owner = "";
        public string Workspace = "";
        public string Path = "";
    }

    public static async Task<List<LockFileInfo>> GetLockFiles(string server, string workingDirectory)
    {
        var serverArg = server == "" ? "" : $" --server={server}";
        var output = await Run($"lock list{serverArg} --machinereadable", workingDirectory);
        var lines = output.Split("\r\n");
        var result = new List<LockFileInfo>(lines.Length);

        foreach (var line in lines)
        {
            if (line == "")
                continue;

            var sep1 = line.IndexOf(' ', 0);
            var sep2 = line.IndexOf(' ', sep1 + 1);
            var sep3 = line.IndexOf(' ', sep2 + 1);

            result.Add(new LockFileInfo
            {
                ID = line[..sep1],
                Owner = line[(sep1 + 1)..sep2],
                Workspace = line[(sep2 + 1)..sep3],
                Path = line[(sep3 + 1)..],
            });
        }

        return result;
    }

    public static async Task UnlockFile(string id, string server, string workingDirectory)
    {
        await Run($"unlock {id}", workingDirectory);
    }

    public static async Task<string> GetUserName(string workingDirectory)
    {
        return (await Run("whoami --machinereadable", workingDirectory)).Trim();
    }

    /// <summary>
    ///     Update the workspace to the latest version.
    /// </summary>
    /// <param name="workingDirectory"></param>
    /// <returns>error message if any</returns>
    public static async Task<string> Update(string workingDirectory)
    {
        var cmd = await IsPartial(workingDirectory) ? "partial update --machinereadable" : "update --machinereadable";
        var (_, error) = await RunWithError(cmd, workingDirectory);

        return error;
    }

    public static async Task OpenClient(string workspace)
    {
        var guiClient = await IsPartial(workspace) ? "gluon.exe" : "plastic.exe";
        Process.Start(guiClient, $@"--wk=""{workspace}""");
    }
}
