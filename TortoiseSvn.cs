using System.Diagnostics;
using System.Text;

public static class TortoiseSvn
{
    private static async Task<bool> ExecuteTortoiseProc(string arguments)
    {
        var process = Process.Start(
            new ProcessStartInfo("TortoiseProc.exe", arguments) { UseShellExecute = true }
        );
        if (process == null)
            return false;
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }

    private static readonly Encoding UnicodeWithoutBom = new UnicodeEncoding(false, false);

    private static async Task<string> ToPathArguments(string[] paths)
    {
        var listFile = Path.GetTempFileName();
        await using var writer = new StreamWriter(listFile, false, UnicodeWithoutBom);

        foreach (var path in paths)
        {
            await writer.WriteAsync(path);
            await writer.WriteAsync('\n');
        }

        return $"/pathfile:\"{listFile}\" /deletepathfile";
    }

    public static async Task<bool> Commit(string[] paths, string arguments = "")
    {
        return await ExecuteTortoiseProc(
            $"/command:commit {await ToPathArguments(paths)} {arguments}"
        );
    }

    public static async Task<bool> Update(string[] paths, string arguments = "")
    {
        return await ExecuteTortoiseProc(
            $"/command:update {await ToPathArguments(paths)} {arguments}"
        );
    }

    public static async Task<bool> Add(string[] paths, string arguments = "")
    {
        return await ExecuteTortoiseProc(
            $"/command:add {await ToPathArguments(paths)} {arguments}"
        );
    }

    public static async Task<bool> Remove(string[] paths, string arguments = "")
    {
        return await ExecuteTortoiseProc(
            $"/command:remove {await ToPathArguments(paths)} {arguments}"
        );
    }

    public static async Task<bool> Revert(string[] paths, string arguments = "")
    {
        return await ExecuteTortoiseProc(
            $"/command:revert {await ToPathArguments(paths)} {arguments}"
        );
    }

    public static async Task<bool> ShowLog(string path, string arguments = "")
    {
        return await ExecuteTortoiseProc($"/command:log /path:\"{path}\" {arguments}");
    }

    public static async Task<bool> Cleanup(string path, string arguments = "")
    {
        return await ExecuteTortoiseProc($"/command:cleanup /path:\"{path}\" {arguments}");
    }

    public static async Task<bool> Checkout(string url, string path, string arguments = "")
    {
        return await ExecuteTortoiseProc(
            $"/command:checkout /url:{url} /path:\"{path}\" {arguments}"
        );
    }

    public static async Task<bool> Resolve(string path, string arguments = "")
    {
        return await ExecuteTortoiseProc($"/command:resolve /path:\"{path}\" {arguments}");
    }
}
