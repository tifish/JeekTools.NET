namespace JeekTools;

public static class DriveInfoExtension
{
    public static bool IsSSD(this DriveInfo driveInfo, bool defaultWhenError = true)
    {
        return Disk.IsSSD(driveInfo.Name, defaultWhenError);
    }

    public static bool TryIsSSD(this DriveInfo driveInfo, out bool isSsd)
    {
        return Disk.TryIsSSD(driveInfo.Name, out isSsd);
    }
}
