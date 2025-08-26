namespace JeekTools;

public static class DriveInfoExtension
{
    public static bool IsSSD(this DriveInfo driveInfo)
    {
        return Disk.IsSSD(driveInfo.Name);
    }
}
