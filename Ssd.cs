using System.Management;

namespace JeekTools;

public static class Ssd
{
    private static readonly Dictionary<char, bool> _isSsdCache = [];

    public static bool IsSsd(char driveLetter)
    {
        driveLetter = char.ToUpper(driveLetter);

        if (_isSsdCache.TryGetValue(driveLetter, out var isSsd))
            return isSsd;

        isSsd = new DriveInfo(driveLetter.ToString() + ":\\").IsSsd();
        _isSsdCache[driveLetter] = isSsd;

        return isSsd;
    }
}