namespace JeekTools;

public static class DriveInfoExtension
{
    public static bool IsSSD(this DriveInfo driveInfo, bool defaultWhenError = true)
    {
        return Disk.IsSSD(driveInfo.Name, defaultWhenError);
    }
}
