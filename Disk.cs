using System.Management;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekTools;

public static class Disk
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(Disk));

    private static readonly Dictionary<char, MediaType> _diskTypeCache = [];

    public static MediaType GetType(char driveLetter)
    {
        driveLetter = char.ToUpper(driveLetter);

        if (_diskTypeCache.TryGetValue(driveLetter, out var cachedDiskType))
            return cachedDiskType;

        var diskType = GetDiskTypeUsingWmi(driveLetter);

        _diskTypeCache[driveLetter] = diskType;

        return diskType;
    }

    private static MediaType GetDiskTypeUsingWmi(char driveLetter)
    {
        var driveId = driveLetter.ToString().ToUpper() + ":";

        try
        {
            var physicalDeviceId = GetPhysicalDeviceIdFromDriveLetter(driveId);
            if (physicalDeviceId == null)
            {
                Log.ZLogError($"IsSsdUsingWmi: physical device id not found: {driveLetter}");
                return MediaType.Unknown;
            }
            var mediaType = GetMediaTypeFromPhysicalDeviceId(physicalDeviceId);

            return mediaType;
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"IsSsdUsingWmi exception: {driveLetter}");
            return MediaType.Unknown;
        }
    }

    private static string? GetPhysicalDeviceIdFromDriveLetter(string logicalDrive)
    {
        using var searcher1 = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition");
        foreach (ManagementObject rel in searcher1.Get())
        {
            var antecedent = rel["Antecedent"]?.ToString();
            var dependent = rel["Dependent"]?.ToString();

            if (antecedent == null || dependent == null)
                continue;

            if (!dependent.Contains($"Win32_LogicalDisk.DeviceID=\"{logicalDrive}\""))
                continue;

            var partitionMatch = antecedent.Split(["DeviceID=\""], StringSplitOptions.None);
            if (partitionMatch.Length <= 1)
                continue;

            var partitionId = partitionMatch[1].TrimEnd('"');

            // Step 2: Find which physical disk the partition maps to
            using var searcher2 = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDriveToDiskPartition");
            foreach (ManagementObject map in searcher2.Get())
            {
                var diskAntecedent = map["Antecedent"]?.ToString();
                var diskDependent = map["Dependent"]?.ToString();

                if (diskAntecedent == null || diskDependent == null)
                    continue;

                if (!diskDependent.Contains($"DeviceID=\"{partitionId}\""))
                    continue;

                var diskMatch = diskAntecedent.Split(["DeviceID=\""], StringSplitOptions.None);
                if (diskMatch.Length > 1)
                {
                    return diskMatch[1].TrimEnd('"');
                }
            }
        }

        Log.ZLogError($"GetPhysicalDeviceIdFromDriveLetter: physical device id not found: {logicalDrive}");
        return null;
    }

    public enum MediaType
    {
        Unknown = 0,
        HDD = 3,
        SSD = 4,
        SCM = 5
    }

    private static MediaType GetMediaTypeFromPhysicalDeviceId(string physicalDeviceId)
    {
        // physicalDeviceId example: \\.\PHYSICALDRIVE0
        var diskNumberStr = new string([.. physicalDeviceId.Where(char.IsDigit)]);
        if (!int.TryParse(diskNumberStr, out int diskNumber))
            return MediaType.Unknown;

        var scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
        scope.Connect();

        var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));

        foreach (ManagementObject disk in searcher.Get())
        {
            if (disk["DeviceId"] != null && Convert.ToInt32(disk["DeviceId"]) == diskNumber)
            {
                int mediaTypeCode = disk["MediaType"] != null ? Convert.ToInt32(disk["MediaType"]) : 0;
                return mediaTypeCode switch
                {
                    3 => MediaType.HDD,
                    4 => MediaType.SSD,
                    5 => MediaType.SCM,
                    _ => MediaType.Unknown
                };
            }
        }

        return MediaType.Unknown;
    }
}
