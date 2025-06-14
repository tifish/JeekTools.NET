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

    public static async Task<bool> StartElevatedAndWait(string fileName, string arguments = "")
    {
        var process = StartElevated(fileName, arguments);

        if (process == null)
            return false;

        await process.WaitForExitAsync();

        return process.ExitCode == 0;
    }
}
