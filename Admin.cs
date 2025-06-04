using System.Diagnostics;
using System.Security.Principal;

namespace JeekTools;

public static class Admin
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static Process? StartElevated(string fileName, string arguments = "")
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        });
    }

    public static async Task<string> StartElevatedAndWait(string fileName, string arguments = "")
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            Verb = "runas",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        });

        if (process == null)
            return "";

        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
            return "";

        return process.StandardError.ReadToEnd();
    }
}
