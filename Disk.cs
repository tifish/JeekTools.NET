using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JeekTools;

public static class Disk
{
    [StructLayout(LayoutKind.Sequential)]
    private struct VOLUME_DISK_EXTENTS
    {
        public int NumberOfExtents;
        public DISK_EXTENT Extents; // Only take the first Extent here
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISK_EXTENT
    {
        public int DiskNumber;
        public long StartingOffset;
        public long ExtentLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;

        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        out VOLUME_DISK_EXTENTS lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize,
        ref DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped
    );

    /// <summary>
    /// Detect whether the specified logical drive is an SSD
    /// </summary>
    /// <param name="driveLetter">Drive letter, such as "C:\" or "D:\"</param>
    /// <returns>Returns true if it's an SSD, otherwise false</returns>
    public static bool IsSSD(string driveLetter, bool defaultWhenError)
    {
        if (driveLetter.Length == 0)
            throw new ArgumentException("Drive letter cannot be empty");

        try
        {
            int diskNumber = GetDiskNumber(driveLetter[0]);
            return CheckDiskSSD(diskNumber);
        }
        catch
        {
            return defaultWhenError;
        }
    }

    // Get the physical disk number corresponding to the logical drive
    private static int GetDiskNumber(char driveLetter)
    {
        string path = $"\\\\.\\{driveLetter}:";
        using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new InvalidOperationException($"Cannot open logical drive {driveLetter}");

        if (
            !DeviceIoControl(
                handle,
                IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                IntPtr.Zero,
                0,
                out var extents,
                Marshal.SizeOf(typeof(VOLUME_DISK_EXTENTS)),
                out _,
                IntPtr.Zero
            )
        )
        {
            throw new InvalidOperationException("Cannot get physical disk number");
        }

        return extents.Extents.DiskNumber;
    }

    // Check if physical disk is SSD
    private static bool CheckDiskSSD(int diskNumber)
    {
        string path = $"\\\\.\\PhysicalDrive{diskNumber}";
        using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new InvalidOperationException($"Cannot open physical disk {diskNumber}");

        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = StorageDeviceSeekPenaltyProperty,
            QueryType = 0,
            AdditionalParameters = new byte[1],
        };
        var desc = new DEVICE_SEEK_PENALTY_DESCRIPTOR();

        if (
            !DeviceIoControl(
                handle,
                IOCTL_STORAGE_QUERY_PROPERTY,
                ref query,
                Marshal.SizeOf(typeof(STORAGE_PROPERTY_QUERY)),
                ref desc,
                Marshal.SizeOf(typeof(DEVICE_SEEK_PENALTY_DESCRIPTOR)),
                out _,
                IntPtr.Zero
            )
        )
        {
            throw new InvalidOperationException("Cannot detect disk properties");
        }

        return !desc.IncursSeekPenalty; // No seek penalty → SSD
    }
}
