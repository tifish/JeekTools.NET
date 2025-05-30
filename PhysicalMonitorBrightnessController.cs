﻿using System.Runtime.InteropServices;

namespace JeekTools;

public class PhysicalMonitorBrightnessController : IDisposable
{
    #region DllImport

    [DllImport("dxva2.dll", EntryPoint = "GetNumberOfPhysicalMonitorsFromHMONITOR")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", EntryPoint = "GetPhysicalMonitorsFromHMONITOR")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", EntryPoint = "GetMonitorBrightness")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr handle, ref uint minimumBrightness, ref uint currentBrightness, ref uint maxBrightness);

    [DllImport("dxva2.dll", EntryPoint = "SetMonitorBrightness")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr handle, uint newBrightness);

    [DllImport("dxva2.dll", EntryPoint = "DestroyPhysicalMonitor")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [DllImport("dxva2.dll", EntryPoint = "DestroyPhysicalMonitors")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsDelegate lpfnEnum, IntPtr dwData);

    private delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    #endregion

    private IReadOnlyCollection<MonitorInfo> Monitors { get; set; } = null!;

    public PhysicalMonitorBrightnessController()
    {
        UpdateMonitors();
    }

    #region Get & Set

    /// <summary>
    /// </summary>
    /// <param name="brightness">0-100</param>
    public void Set(int brightness)
    {
        Set((uint)brightness, true);
    }

    private void Set(uint brightness, bool refreshMonitorsIfNeeded)
    {
        var isSomeFail = false;
        foreach (var monitor in Monitors)
        {
            var realNewValue = (monitor.MaxValue - monitor.MinValue) * brightness / 100 + monitor.MinValue;
            if (SetMonitorBrightness(monitor.Handle, realNewValue))
            {
                monitor.CurrentValue = realNewValue;
            }
            else if (refreshMonitorsIfNeeded)
            {
                isSomeFail = true;
                break;
            }
        }

        if (refreshMonitorsIfNeeded && (isSomeFail || !Monitors.Any()))
        {
            UpdateMonitors();
            Set(brightness, false);
        }
    }

    public int Get()
    {
        if (!Monitors.Any())
            return -1;
        return (int)Monitors.Average(d => d.CurrentValue);
    }

    #endregion

    public void UpdateMonitors()
    {
        DisposeMonitors(Monitors);

        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData) =>
        {
            uint physicalMonitorsCount = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref physicalMonitorsCount))
                // Cannot get monitor count
                return true;

            var physicalMonitors = new PHYSICAL_MONITOR[physicalMonitorsCount];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, physicalMonitorsCount, physicalMonitors))
                // Cannot get physical monitor handle
                return true;

            foreach (var physicalMonitor in physicalMonitors)
            {
                uint minValue = 0, currentValue = 0, maxValue = 0;
                if (!GetMonitorBrightness(physicalMonitor.hPhysicalMonitor, ref minValue, ref currentValue, ref maxValue))
                {
                    DestroyPhysicalMonitor(physicalMonitor.hPhysicalMonitor);
                    continue;
                }

                var info = new MonitorInfo
                {
                    Handle = physicalMonitor.hPhysicalMonitor,
                    MinValue = minValue,
                    CurrentValue = currentValue,
                    MaxValue = maxValue,
                };
                monitors.Add(info);
            }

            return true;
        }, IntPtr.Zero);

        Monitors = monitors;
    }

    public void Dispose()
    {
        DisposeMonitors(Monitors);
        GC.SuppressFinalize(this);
    }

    private static void DisposeMonitors(IEnumerable<MonitorInfo> monitors)
    {
        if (monitors?.Any() == true)
        {
            var monitorArray = monitors.Select(m => new PHYSICAL_MONITOR { hPhysicalMonitor = m.Handle }).ToArray();
            DestroyPhysicalMonitors((uint)monitorArray.Length, monitorArray);
        }
    }

    #region Classes

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    public class MonitorInfo
    {
        public uint MinValue { get; set; }
        public uint MaxValue { get; set; }
        public IntPtr Handle { get; set; }
        public uint CurrentValue { get; set; }
    }

    #endregion
}
