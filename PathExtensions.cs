namespace JeekTools;

public static class PathExtensions
{
    public static string GetExecutableFullPath(string executableFileName)
    {
        var exe = Environment.ExpandEnvironmentVariables(executableFileName);
        if (File.Exists(exe))
            return Path.GetFullPath(exe);

        if (Path.GetDirectoryName(exe) == "")
            foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                var validPath = path.Trim();
                if (string.IsNullOrEmpty(validPath))
                    continue;

                var fullPath = Path.Combine(validPath, exe);
                if (File.Exists(fullPath))
                    return fullPath;
            }

        return "";
    }
}
