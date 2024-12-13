using System.Runtime.InteropServices;

namespace JeekTools;

public static class Power
{
    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, [MarshalAs(UnmanagedType.LPStruct)] Guid schemeGuid);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    private static readonly Guid PowerSaverPlan = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformancePlan = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public static bool SetPowerPlan(PowerPlanType powerPlanType)
    {
        var result = PowerSetActiveScheme(IntPtr.Zero,
            powerPlanType switch
            {
                PowerPlanType.PowerSaver => PowerSaverPlan,
                PowerPlanType.Balanced => BalancedPlan,
                PowerPlanType.HighPerformance => HighPerformancePlan,
                _ => throw new ArgumentOutOfRangeException(nameof(powerPlanType), powerPlanType, null),
            });

        return result == 0;
    }

    public static PowerPlanType GetPowerPlan()
    {
        var result = PowerGetActiveScheme(IntPtr.Zero, out var activePolicyGuid);
        if (result != 0)
            return PowerPlanType.Balanced;

        var activeGuid = (Guid)(Marshal.PtrToStructure(activePolicyGuid, typeof(Guid)) ?? Guid.Empty);
        Marshal.FreeHGlobal(activePolicyGuid);

        if (activeGuid == PowerSaverPlan)
            return PowerPlanType.PowerSaver;
        if (activeGuid == BalancedPlan)
            return PowerPlanType.Balanced;
        if (activeGuid == HighPerformancePlan)
            return PowerPlanType.HighPerformance;
        return PowerPlanType.Unknown;
    }

    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImportAttribute("powrprof.dll", EntryPoint = "PowerGetActualOverlayScheme")]
    private static extern uint PowerGetActualOverlayScheme(out Guid actualOverlayGuid);

    private static readonly Guid BestPowerEfficiencyMode = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BalancedMode = new("00000000-0000-0000-0000-000000000000");
    private static readonly Guid BestPerformanceMode = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static bool SetPowerMode(PowerModeType powerModeType)
    {
        var result = PowerSetActiveOverlayScheme(
            powerModeType switch
            {
                PowerModeType.BestPowerEfficiency => BestPowerEfficiencyMode,
                PowerModeType.Balanced => BalancedMode,
                PowerModeType.BestPerformance => BestPerformanceMode,
                _ => throw new ArgumentOutOfRangeException(nameof(powerModeType), powerModeType, null),
            });

        return result == 0;
    }

    public static PowerModeType GetPowerMode()
    {
        var result = PowerGetActualOverlayScheme(out var actualOverlayGuid);
        if (result != 0)
            return PowerModeType.Unknown;

        if (actualOverlayGuid == BestPowerEfficiencyMode)
            return PowerModeType.BestPowerEfficiency;
        if (actualOverlayGuid == BalancedMode)
            return PowerModeType.Balanced;
        if (actualOverlayGuid == BestPerformanceMode)
            return PowerModeType.BestPerformance;
        return PowerModeType.Unknown;
    }
}

public enum PowerPlanType
{
    Unknown,
    HighPerformance,
    Balanced,
    PowerSaver,
}

public enum PowerModeType
{
    Unknown,
    BestPowerEfficiency,
    Balanced,
    BestPerformance,
}
