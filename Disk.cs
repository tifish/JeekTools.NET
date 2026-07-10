using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JeekTools;

public static class Disk
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;

        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_TRIM_DESCRIPTOR
    {
        public uint Version;
        public uint Size;

        [MarshalAs(UnmanagedType.U1)]
        public bool TrimEnabled;
    }

    // STORAGE_DEVICE_DESCRIPTOR 的定长前缀，省略末尾的变长 RawDeviceProperties。
    // 输出缓冲区小于完整数据时，DeviceIoControl 会截断填充并返回成功。
    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_DESCRIPTOR_FIXED
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;

        [MarshalAs(UnmanagedType.U1)]
        public bool RemovableMedia;

        [MarshalAs(UnmanagedType.U1)]
        public bool CommandQueueing;

        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public int BusType;
        public uint RawPropertiesLength;
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

    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int StorageDeviceTrimProperty = 8;

    private const int BusTypeNvme = 17;

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
        IntPtr lpOutBuffer,
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize,
        ref DEVICE_TRIM_DESCRIPTOR lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize,
        ref STORAGE_DEVICE_DESCRIPTOR_FIXED lpOutBuffer,
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
        return TryIsSSD(driveLetter, out var isSsd) ? isSsd : defaultWhenError;
    }

    // 磁盘介质类型在进程生命周期内不会变化，按盘符缓存，避免反复打开设备句柄。
    private static readonly ConcurrentDictionary<char, (bool Determined, bool IsSsd)> _cache =
        new();

    /// <summary>
    /// 尝试检测指定逻辑盘是否为 SSD。
    /// </summary>
    /// <remarks>
    /// 返回 false 表示无法检测磁盘类型，调用方不应把这种情况直接当成机械硬盘。
    /// </remarks>
    public static bool TryIsSSD(string driveLetter, out bool isSsd)
    {
        if (driveLetter.Length == 0)
            throw new ArgumentException("Drive letter cannot be empty");

        var (determined, cachedIsSsd) = _cache.GetOrAdd(
            char.ToUpperInvariant(driveLetter[0]),
            DetectVolume
        );
        isSsd = cachedIsSsd;
        return determined;
    }

    private static (bool Determined, bool IsSsd) DetectVolume(char driveLetter)
    {
        try
        {
            var diskNumbers = GetDiskNumbers(driveLetter);
            if (diskNumbers.Length == 0)
                return (false, false);

            // 卷可能跨多块物理盘（存储空间、跨区卷）。
            // 只要有一块盘是 SSD 就按 SSD 处理，宁可多扫不可漏扫；全部确认是机械盘才判定为机械盘。
            var anyUndetermined = false;

            foreach (var diskNumber in diskNumbers)
            {
                switch (DetectDisk(diskNumber))
                {
                    case true:
                        return (true, true);
                    case null:
                        anyUndetermined = true;
                        break;
                }
            }

            return anyUndetermined ? (false, false) : (true, false);
        }
        catch
        {
            return (false, false);
        }
    }

    // 按可靠性依次尝试多种信号，任一命中即得出结论：
    // 1. NVMe 总线 → 一定是 SSD，查询几乎所有驱动都支持。
    // 2. Seek penalty → SATA SSD/HDD 的主判据，部分 USB 桥接、RAID、虚拟磁盘驱动不支持。
    // 3. TRIM → 支持 TRIM 基本是 SSD；不支持不能反推是机械盘（不做结论）。
    private static bool? DetectDisk(int diskNumber)
    {
        var path = $"\\\\.\\PhysicalDrive{diskNumber}";
        using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

        if (handle.IsInvalid)
            return null;

        if (TryGetBusType(handle, out var busType) && busType == BusTypeNvme)
            return true;

        if (TryGetSeekPenalty(handle, out var incursSeekPenalty))
            return !incursSeekPenalty;

        if (TryGetTrimEnabled(handle, out var trimEnabled) && trimEnabled)
            return true;

        return null;
    }

    private static bool TryGetBusType(SafeFileHandle handle, out int busType)
    {
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = StorageDeviceProperty,
            QueryType = 0,
            AdditionalParameters = new byte[1],
        };
        var desc = new STORAGE_DEVICE_DESCRIPTOR_FIXED();

        var ok = DeviceIoControl(
            handle,
            IOCTL_STORAGE_QUERY_PROPERTY,
            ref query,
            Marshal.SizeOf(typeof(STORAGE_PROPERTY_QUERY)),
            ref desc,
            Marshal.SizeOf(typeof(STORAGE_DEVICE_DESCRIPTOR_FIXED)),
            out _,
            IntPtr.Zero
        );

        busType = desc.BusType;
        return ok;
    }

    private static bool TryGetSeekPenalty(SafeFileHandle handle, out bool incursSeekPenalty)
    {
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = StorageDeviceSeekPenaltyProperty,
            QueryType = 0,
            AdditionalParameters = new byte[1],
        };
        var desc = new DEVICE_SEEK_PENALTY_DESCRIPTOR();

        var ok = DeviceIoControl(
            handle,
            IOCTL_STORAGE_QUERY_PROPERTY,
            ref query,
            Marshal.SizeOf(typeof(STORAGE_PROPERTY_QUERY)),
            ref desc,
            Marshal.SizeOf(typeof(DEVICE_SEEK_PENALTY_DESCRIPTOR)),
            out _,
            IntPtr.Zero
        );

        incursSeekPenalty = desc.IncursSeekPenalty;
        return ok;
    }

    private static bool TryGetTrimEnabled(SafeFileHandle handle, out bool trimEnabled)
    {
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = StorageDeviceTrimProperty,
            QueryType = 0,
            AdditionalParameters = new byte[1],
        };
        var desc = new DEVICE_TRIM_DESCRIPTOR();

        var ok = DeviceIoControl(
            handle,
            IOCTL_STORAGE_QUERY_PROPERTY,
            ref query,
            Marshal.SizeOf(typeof(STORAGE_PROPERTY_QUERY)),
            ref desc,
            Marshal.SizeOf(typeof(DEVICE_TRIM_DESCRIPTOR)),
            out _,
            IntPtr.Zero
        );

        trimEnabled = desc.TrimEnabled;
        return ok;
    }

    // 获取逻辑盘对应的所有物理盘编号。
    // 卷可能跨多块物理盘，IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS 的输出是变长的，
    // 缓冲区不足时调用会失败（ERROR_MORE_DATA），所以按足够多的 extent 分配。
    private static int[] GetDiskNumbers(char driveLetter)
    {
        var path = $"\\\\.\\{driveLetter}:";
        using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new InvalidOperationException($"Cannot open logical drive {driveLetter}");

        // VOLUME_DISK_EXTENTS 布局：NumberOfExtents(4) + 对齐填充(4) + DISK_EXTENT[](每个 24 字节)
        const int headerSize = 8;
        const int extentSize = 24;
        const int maxExtents = 64;
        const int bufferSize = headerSize + maxExtents * extentSize;

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (
                !DeviceIoControl(
                    handle,
                    IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                    IntPtr.Zero,
                    0,
                    buffer,
                    bufferSize,
                    out _,
                    IntPtr.Zero
                )
            )
            {
                throw new InvalidOperationException("Cannot get volume disk extents");
            }

            var extentCount = Marshal.ReadInt32(buffer);
            var diskNumbers = new int[extentCount];
            for (var i = 0; i < extentCount; i++)
                diskNumbers[i] = Marshal.ReadInt32(buffer, headerSize + i * extentSize);

            return diskNumbers;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
