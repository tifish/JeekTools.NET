using System.Runtime.InteropServices;

namespace JeekTools;

public static class UserActivityDetector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    /// <summary>
    /// Gets the time of the user's last input (keyboard or mouse)
    /// </summary>
    /// <returns>The UTC time of the last input</returns>
    public static DateTime GetLastInputTime()
    {
        var lastInputInfo = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)),
        };

        if (GetLastInputInfo(ref lastInputInfo))
        {
            var currentTickCount = GetTickCount();
            var idleTickCount = currentTickCount - lastInputInfo.dwTime;
            var lastInputTime = DateTime.Now.AddMilliseconds(-idleTickCount);
            return lastInputTime;
        }

        // If API call fails, return current time
        return DateTime.Now;
    }

    /// <summary>
    /// Gets the user idle time in milliseconds
    /// </summary>
    /// <returns>Idle time in milliseconds</returns>
    public static uint GetIdleTimeMs()
    {
        var lastInputInfo = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)),
        };

        if (GetLastInputInfo(ref lastInputInfo))
        {
            var currentTickCount = GetTickCount();
            return currentTickCount - lastInputInfo.dwTime;
        }

        return 0;
    }

    /// <summary>
    /// Gets the user idle time
    /// </summary>
    /// <returns>Idle time</returns>
    public static TimeSpan GetIdleTime()
    {
        return TimeSpan.FromMilliseconds(GetIdleTimeMs());
    }

    /// <summary>
    /// Checks if the user has been idle for longer than the specified time
    /// </summary>
    /// <param name="idleThreshold">Idle threshold</param>
    /// <returns>Whether idle time exceeds the threshold</returns>
    public static bool IsIdleFor(TimeSpan idleThreshold)
    {
        return GetIdleTime() >= idleThreshold;
    }

    /// <summary>
    /// Checks if the user has been idle for longer than the specified number of seconds
    /// </summary>
    /// <param name="seconds">Number of seconds</param>
    /// <returns>Whether idle time exceeds the specified seconds</returns>
    public static bool IsIdleForSeconds(int seconds)
    {
        return GetIdleTimeMs() >= seconds * 1000;
    }

    /// <summary>
    /// Checks if the user has been idle for longer than the specified number of minutes
    /// </summary>
    /// <param name="minutes">Number of minutes</param>
    /// <returns>Whether idle time exceeds the specified minutes</returns>
    public static bool IsIdleForMinutes(int minutes)
    {
        return GetIdleTimeMs() >= minutes * 60 * 1000;
    }
}
