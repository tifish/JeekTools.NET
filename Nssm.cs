using System.Diagnostics;
using System.Text;

namespace JeekTools;

public static class Nssm
{
    public static string NssmPath { get; set; } = Path.Join(AppContext.BaseDirectory, "Nssm", "nssm.exe");

    private static Process? CallNssm(string command)
    {
        return Process.Start(new ProcessStartInfo(NssmPath, command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public static string LastOutput { get; private set; } = "";
    public static string LastError { get; private set; } = "";

    private static async Task<bool> CallNssmAndWait(string command)
    {
        var process = CallNssm(command);
        if (process is null)
            return false;
        await process.WaitForExitAsync();
        LastOutput = await process.StandardOutput.ReadToEndAsync();
        LastError = await process.StandardError.ReadToEndAsync();
        return process.ExitCode == 0;
    }

    public static async Task<bool> InstallService(string serviceName, string fileName, string arguments)
    {
        return await CallNssmAndWait($"""install "{serviceName}" "{fileName}" {arguments}""");
    }

    public static async Task<bool> UninstallService(string serviceName)
    {
        return await CallNssmAndWait($"""remove "{serviceName}" confirm""");
    }

    public static async Task<bool> StartService(string serviceName)
    {
        return await CallNssmAndWait($"""start "{serviceName}""");
    }

    public static async Task<bool> StopService(string serviceName)
    {
        return await CallNssmAndWait($"""stop "{serviceName}""");
    }

    public static async Task<bool> RestartService(string serviceName)
    {
        return await CallNssmAndWait($"""restart "{serviceName}""");
    }

    public static async Task<ServiceStatus> GetServiceStatus(string serviceName)
    {
        var statusString = (await Executor.RunWithOutput(NssmPath, $"""status "{serviceName}" """)).Trim();
        return statusString switch
        {
            "SERVICE_STOPPED" => ServiceStatus.Stopped,
            "SERVICE_START_PENDING" => ServiceStatus.StartPending,
            "SERVICE_STOP_PENDING" => ServiceStatus.StopPending,
            "SERVICE_RUNNING" => ServiceStatus.Running,
            "SERVICE_CONTINUE_PENDING" => ServiceStatus.ContinuePending,
            "SERVICE_PAUSE_PENDING" => ServiceStatus.PausePending,
            "SERVICE_PAUSED" => ServiceStatus.Paused,
            _ => ServiceStatus.None,
        };
    }

}

public enum ServiceStatus
{
    None = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

