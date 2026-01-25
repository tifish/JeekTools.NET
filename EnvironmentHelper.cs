using System.Runtime.InteropServices;

namespace JeekTools;

public static class EnvironmentHelper
{
    internal const int SM_CLEANBOOT = 67;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int smIndex);

    public static bool IsSafeMode()
    {
        return GetSystemMetrics(SM_CLEANBOOT) != 0;
    }

    public static void SetBootToSafeMode()
    {
        Admin.StartElevated("bcdedit.exe", "/set {current} safeboot Minimal");
    }

    public static void SetBootToNormalMode()
    {
        Admin.StartElevated("bcdedit.exe", "/deletevalue {current} safeboot");
    }

    public static void ShutdownSystem()
    {
        Admin.StartElevated("shutdown.exe", "/s /t 0");
    }

    public static void RebootSystem()
    {
        Admin.StartElevated("shutdown.exe", $"/r /t 0");
    }

    public static void HibernateSystem()
    {
        Admin.StartElevated("shutdown.exe", "/h");
    }
}
