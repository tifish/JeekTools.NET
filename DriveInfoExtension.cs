using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JeekTools;

public static class DriveInfoExtension
{
    public static bool IsSsd(this DriveInfo driveInfo)
    {
        var diskNumber = DriveLetterToDiskNumber.GetDiskExtents(driveInfo.Name[0]);
        return GetDiskType.IsSsd(diskNumber);
    }

    // ReSharper disable InconsistentNaming
    private static class DriveLetterToDiskNumber
    {
        // For CreateFile to get handle to drive
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        // CreateFile to get handle to drive
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            [MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        // For control codes
        private const uint IOCTL_VOLUME_BASE = 0x00000056;
        private const uint METHOD_BUFFERED = 0;
        private const uint FILE_ANY_ACCESS = 0;

        private static uint CTL_CODE(uint DeviceType, uint Function,
            uint Method, uint Access)
        {
            return (DeviceType << 16) | (Access << 14) |
                   (Function << 2) | Method;
        }

        // For DeviceIoControl to get disk extents
        [StructLayout(LayoutKind.Sequential)]
        private struct DISK_EXTENT
        {
            public readonly uint DiskNumber;
            public readonly long StartingOffset;
            public readonly long ExtentLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VOLUME_DISK_EXTENTS
        {
            public readonly uint NumberOfDiskExtents;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public readonly DISK_EXTENT[] Extents;
        }

        // DeviceIoControl to get disk extents
        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            ref VOLUME_DISK_EXTENTS lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        // For error message
        private const uint FORMAT_MESSAGE_FROM_SYSTEM = 0x00001000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint FormatMessage(
            uint dwFlags,
            IntPtr lpSource,
            uint dwMessageId,
            uint dwLanguageId,
            StringBuilder lpBuffer,
            uint nSize,
            IntPtr Arguments);

        // Method for disk extents
        public static uint GetDiskExtents(char cDrive)
        {
            var di = new DriveInfo(cDrive.ToString());
            if (di.DriveType != DriveType.Fixed)
                throw new IOException("This drive is not fixed drive.");

            var sDrive = "\\\\.\\" + cDrive + ":";

            var hDrive = CreateFileW(
                sDrive,
                0, // No access to drive
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (hDrive.IsInvalid)
            {
                var message = GetErrorMessage(Marshal.GetLastWin32Error());
                throw new IOException("CreateFile failed. " + message);
            }

            var IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = CTL_CODE(
                IOCTL_VOLUME_BASE, 0,
                METHOD_BUFFERED, FILE_ANY_ACCESS); // From winioctl.h

            var query_disk_extents = new VOLUME_DISK_EXTENTS();

            var query_disk_extents_result = DeviceIoControl(
                hDrive,
                IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                IntPtr.Zero,
                0,
                ref query_disk_extents,
                (uint)Marshal.SizeOf(query_disk_extents),
                out _,
                IntPtr.Zero);

            hDrive.Close();

            if (query_disk_extents_result == false ||
                query_disk_extents.Extents.Length != 1)
            {
                var message = GetErrorMessage(Marshal.GetLastWin32Error());
                throw new IOException("DeviceIoControl failed. " + message);
            }

            return query_disk_extents.Extents[0].DiskNumber;
        }

        // Method for error message
        private static string GetErrorMessage(int code)
        {
            var message = new StringBuilder(255);

            FormatMessage(
                FORMAT_MESSAGE_FROM_SYSTEM,
                IntPtr.Zero,
                (uint)code,
                0,
                message,
                (uint)message.Capacity,
                IntPtr.Zero);

            return message.ToString();
        }
    }

    private static class GetDiskType
    {
        // For CreateFile to get handle to drive
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        // CreateFile to get handle to drive
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            [MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        // For control codes
        private const uint FILE_DEVICE_MASS_STORAGE = 0x0000002d;
        private const uint IOCTL_STORAGE_BASE = FILE_DEVICE_MASS_STORAGE;
        private const uint FILE_DEVICE_CONTROLLER = 0x00000004;
        private const uint IOCTL_SCSI_BASE = FILE_DEVICE_CONTROLLER;
        private const uint METHOD_BUFFERED = 0;
        private const uint FILE_ANY_ACCESS = 0;
        private const uint FILE_READ_ACCESS = 0x00000001;
        private const uint FILE_WRITE_ACCESS = 0x00000002;

        private static uint CTL_CODE(uint DeviceType, uint Function,
            uint Method, uint Access)
        {
            return (DeviceType << 16) | (Access << 14) |
                   (Function << 2) | Method;
        }

        // For DeviceIoControl to check no seek penalty
        private const uint StorageDeviceSeekPenaltyProperty = 7;
        private const uint PropertyStandardQuery = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;
            public uint QueryType;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public readonly byte[] AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
        {
            public readonly uint Version;
            public readonly uint Size;

            [MarshalAs(UnmanagedType.U1)]
            public readonly bool IncursSeekPenalty;
        }

        // DeviceIoControl to check no seek penalty
        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref STORAGE_PROPERTY_QUERY lpInBuffer,
            uint nInBufferSize,
            ref DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        // For DeviceIoControl to check nominal media rotation rate
        private const uint ATA_FLAGS_DATA_IN = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        private struct ATA_PASS_THROUGH_EX
        {
            public ushort Length;
            public ushort AtaFlags;
            public readonly byte PathId;
            public readonly byte TargetId;
            public readonly byte Lun;
            public readonly byte ReservedAsUchar;
            public uint DataTransferLength;
            public uint TimeOutValue;
            public readonly uint ReservedAsUlong;
            public IntPtr DataBufferOffset;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] PreviousTaskFile;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] CurrentTaskFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ATAIdentifyDeviceQuery
        {
            public ATA_PASS_THROUGH_EX header;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] data;
        }

        // DeviceIoControl to check nominal media rotation rate
        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref ATAIdentifyDeviceQuery lpInBuffer,
            uint nInBufferSize,
            ref ATAIdentifyDeviceQuery lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        // For error message
        private const uint FORMAT_MESSAGE_FROM_SYSTEM = 0x00001000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint FormatMessage(
            uint dwFlags,
            IntPtr lpSource,
            uint dwMessageId,
            uint dwLanguageId,
            StringBuilder lpBuffer,
            uint nSize,
            IntPtr Arguments);

        public static bool IsSsd(uint diskNumber)
        {
            var drive = "\\\\.\\PhysicalDrive" + diskNumber;
            return !HasSeekPenalty(drive);
        }

        // Method for no seek penalty
        private static bool HasSeekPenalty(string sDrive)
        {
            var hDrive = CreateFileW(
                sDrive,
                0, // No access to drive
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (hDrive.IsInvalid)
            {
                var message = GetErrorMessage(Marshal.GetLastWin32Error());
                throw new IOException("CreateFile failed. " + message);
            }

            var IOCTL_STORAGE_QUERY_PROPERTY = CTL_CODE(
                IOCTL_STORAGE_BASE, 0x500,
                METHOD_BUFFERED, FILE_ANY_ACCESS); // From winioctl.h

            var query_seek_penalty =
                new STORAGE_PROPERTY_QUERY();
            query_seek_penalty.PropertyId = StorageDeviceSeekPenaltyProperty;
            query_seek_penalty.QueryType = PropertyStandardQuery;

            var query_seek_penalty_desc = new DEVICE_SEEK_PENALTY_DESCRIPTOR();

            var query_seek_penalty_result = DeviceIoControl(
                hDrive,
                IOCTL_STORAGE_QUERY_PROPERTY,
                ref query_seek_penalty,
                (uint)Marshal.SizeOf(query_seek_penalty),
                ref query_seek_penalty_desc,
                (uint)Marshal.SizeOf(query_seek_penalty_desc),
                out _,
                IntPtr.Zero);

            hDrive.Close();

            if (query_seek_penalty_result == false)
            {
                var message = GetErrorMessage(Marshal.GetLastWin32Error());
                throw new IOException("DeviceIoControl failed. " + message);
            }

            return query_seek_penalty_desc.IncursSeekPenalty;
        }

        // Method for nominal media rotation rate
        // (Administrative privilege is required)
        private static bool IsRotateDevice(string sDrive)
        {
            var hDrive = CreateFileW(
                sDrive,
                GENERIC_READ | GENERIC_WRITE, // Administrative privilege is required
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (hDrive.IsInvalid)
            {
                var message = GetErrorMessage(Marshal.GetLastWin32Error());
                throw new IOException("CreateFile failed. " + message);
            }

            var IOCTL_ATA_PASS_THROUGH = CTL_CODE(
                IOCTL_SCSI_BASE, 0x040b, METHOD_BUFFERED,
                FILE_READ_ACCESS | FILE_WRITE_ACCESS); // From ntddscsi.h

            var id_query = new ATAIdentifyDeviceQuery();
            id_query.data = new ushort[256];

            id_query.header.Length = (ushort)Marshal.SizeOf(id_query.header);
            id_query.header.AtaFlags = (ushort)ATA_FLAGS_DATA_IN;
            id_query.header.DataTransferLength =
                (uint)(id_query.data.Length * 2); // Size of "data" in bytes
            id_query.header.TimeOutValue = 3; // Sec
            id_query.header.DataBufferOffset = Marshal.OffsetOf(
                typeof(ATAIdentifyDeviceQuery), "data");
            id_query.header.PreviousTaskFile = new byte[8];
            id_query.header.CurrentTaskFile = new byte[8];
            id_query.header.CurrentTaskFile[6] = 0xec; // ATA IDENTIFY DEVICE

            var result = DeviceIoControl(
                hDrive,
                IOCTL_ATA_PASS_THROUGH,
                ref id_query,
                (uint)Marshal.SizeOf(id_query),
                ref id_query,
                (uint)Marshal.SizeOf(id_query),
                out _,
                IntPtr.Zero);

            hDrive.Close();

            if (result == false)
            {
                var message = GetErrorMessage(Marshal.GetLastWin32Error());
                throw new IOException("DeviceIoControl failed. " + message);
            }

            // Word index of nominal media rotation rate
            // (1 means non-rotate device)
            const int kNominalMediaRotRateWordIndex = 217;

            return id_query.data[kNominalMediaRotRateWordIndex] != 1;
        }

        // Method for error message
        private static string GetErrorMessage(int code)
        {
            var message = new StringBuilder(255);

            FormatMessage(
                FORMAT_MESSAGE_FROM_SYSTEM,
                IntPtr.Zero,
                (uint)code,
                0,
                message,
                (uint)message.Capacity,
                IntPtr.Zero);

            return message.ToString();
        }
    }
}
