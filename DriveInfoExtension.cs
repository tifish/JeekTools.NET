namespace JeekTools;

public static class DriveInfoExtension
{
    public static Disk.MediaType GetDiskType(this DriveInfo driveInfo)
    {
        return Disk.GetType(driveInfo.Name[0]);
    }
}
