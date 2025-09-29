using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekTools;

public static class Executor
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(Executor));

    public static Process? Run(
        string fileName,
        string arguments = "",
        bool useShellExecute = true,
        bool createNoWindow = false
    )
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = useShellExecute,
            CreateNoWindow = createNoWindow,
        };
        return Run(startInfo);
    }

    public static event UnhandledExceptionEventHandler? OnRunException;

    public static Process? Run(ProcessStartInfo processStartInfo)
    {
        try
        {
            if (
                string.IsNullOrEmpty(processStartInfo.WorkingDirectory)
                && File.Exists(processStartInfo.FileName)
            )
                processStartInfo.WorkingDirectory = Path.GetDirectoryName(
                    processStartInfo.FileName
                );
            return Process.Start(processStartInfo);
        }
        catch (Exception ex)
        {
            Log.ZLogError(
                ex,
                $"Failed to start external program：{processStartInfo.FileName} {processStartInfo.Arguments}"
            );
            OnRunException?.Invoke(processStartInfo, new UnhandledExceptionEventArgs(ex, false));
        }

        return null;
    }

    public static async Task<bool> RunAndWait(
        string fileName,
        string arguments = "",
        bool useShellExecute = true,
        bool createNoWindow = false
    )
    {
        return await RunAndWait(
            new ProcessStartInfo(fileName, arguments ?? "")
            {
                UseShellExecute = useShellExecute,
                CreateNoWindow = createNoWindow,
            }
        );
    }

    public static async Task<bool> RunAndWait(ProcessStartInfo processStartInfo)
    {
        var process = Run(processStartInfo);
        if (process == null)
            return false;

        await process.WaitForExitAsync();

        return process.ExitCode == 0;
    }

    public static async Task<string> RunWithOutput(
        string fileName,
        string arguments,
        Encoding? encoding = null
    )
    {
        var process = Process.Start(
            new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = encoding ?? Encoding.Default,
            }
        );
        if (process is null)
            return "";
        return await process.StandardOutput.ReadToEndAsync();
    }

    public static void Open(string fileOrUrl)
    {
        try
        {
            Process.Start("explorer.exe", $"\"{fileOrUrl}\"");
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to open file or url：{fileOrUrl}");
        }
    }

    [DllImport("shell32.dll", EntryPoint = "SHOpenWithDialog", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hWndParent, ref tagOPENASINFO oOAI);

    // http://msdn.microsoft.com/en-us/library/windows/desktop/bb773363(v=vs.85).aspx
    private struct tagOPENASINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string cszFile;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string cszClass;

        [MarshalAs(UnmanagedType.I4)]
        public tagOPEN_AS_INFO_FLAGS oaifInFlags;
    }

    [Flags]
    private enum tagOPEN_AS_INFO_FLAGS
    {
        OAIF_ALLOW_REGISTRATION = 0x00000001, // Show "Always" checkbox
        OAIF_REGISTER_EXT = 0x00000002, // Perform registration when user hits OK
        OAIF_EXEC = 0x00000004, // Exec file after registering
        OAIF_FORCE_REGISTRATION = 0x00000008, // Force the checkbox to be registration
        OAIF_HIDE_REGISTRATION = 0x00000020, // Vista+: Hide the "always use this file" checkbox
        OAIF_URL_PROTOCOL = 0x00000040, // Vista+: cszFile is actually a URI scheme; show handlers for that scheme
        OAIF_FILE_IS_URI = 0x00000080, // Win8+: The location pointed to by the pcszFile parameter is given as a URI
    }

    public static bool OpenWith(string filePath)
    {
        var openAsInfo = new tagOPENASINFO
        {
            cszFile = filePath,
            cszClass = "",
            oaifInFlags =
                tagOPEN_AS_INFO_FLAGS.OAIF_ALLOW_REGISTRATION | tagOPEN_AS_INFO_FLAGS.OAIF_EXEC,
        };
        var result = SHOpenWithDialog(IntPtr.Zero, ref openAsInfo) == 0;
        if (!result)
            Log.ZLogError($"Failed to open with dialog：{filePath}");
        return result;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl,
        [In] [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
        uint dwFlags
    );

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        IntPtr bindingContext,
        [Out] out IntPtr pidl,
        uint sfgaoIn,
        [Out] out uint psfgaoOut
    );

    public static void OpenExplorerAndSelectItem(string path)
    {
        var folderPath = Path.GetDirectoryName(path);
        if (folderPath == null)
        {
            Process.Start("explorer.exe", $"/select, \"{path}\"");
            return;
        }

        var file = Path.GetFileName(path);

        SHParseDisplayName(folderPath, IntPtr.Zero, out var nativeFolder, 0, out _);

        if (nativeFolder == IntPtr.Zero)
            // Log error, can't find folder
            return;

        SHParseDisplayName(
            Path.Combine(folderPath, file),
            IntPtr.Zero,
            out var nativeFile,
            0,
            out _
        );

        IntPtr[] fileArray;
        if (nativeFile == IntPtr.Zero)
            // Open the folder without the file selected if we can't find the file
            fileArray = Array.Empty<IntPtr>();
        else
            fileArray = [nativeFile];

        SHOpenFolderAndSelectItems(nativeFolder, (uint)fileArray.Length, fileArray, 0);

        Marshal.FreeCoTaskMem(nativeFolder);
        if (nativeFile != IntPtr.Zero)
            Marshal.FreeCoTaskMem(nativeFile);
    }
}
