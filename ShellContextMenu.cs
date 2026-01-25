using DotNetRun;

namespace JeekTools;

public static class ShellContextMenu
{
    private const string MachineClasses = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes";
    private const string UserClasses = @"HKEY_CURRENT_USER\SOFTWARE\Classes";
    private const string DirShell = @"Directory\shell";
    private const string DirBgShell = @"Directory\Background\shell";

    public static void RegisterDirectory(string keyName, string hint, string command)
    {
        var key = $@"{UserClasses}\{DirShell}\{keyName}";
        var bgKey = $@"{UserClasses}\{DirBgShell}\{keyName}";

        Reg.SetValue(key, null, hint);
        Reg.SetValue($@"{key}\command", null, command);
        Reg.SetValue(bgKey, null, hint);
        Reg.SetValue($@"{bgKey}\command", null, command.Replace("%1", "%V"));
    }

    public static void UnregisterDirectory(params string[] keyNames)
    {
        foreach (
            var parentKeyPath in new[]
            {
                $@"{MachineClasses}\{DirShell}",
                $@"{MachineClasses}\{DirBgShell}",
                $@"{UserClasses}\{DirShell}",
                $@"{UserClasses}\{DirBgShell}",
            }
        )
        foreach (var subKeyName in keyNames)
            Reg.DeleteKey($@"{parentKeyPath}\{subKeyName}");
    }
}
