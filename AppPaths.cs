namespace JeekTools;

/// <summary>
/// Resolves the application's root directory for both flat layouts and nested runtime layouts
/// (where an AppHost at <c>&lt;AppDir&gt;\&lt;App&gt;.exe</c> launches <c>&lt;AppDir&gt;\Runtime\&lt;App&gt;.dll</c>).
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Directory containing the launcher executable when the managed assemblies live in a direct
    /// child folder (e.g. <c>Runtime\</c>); otherwise <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public static string ProgramDir { get; } = ResolveProgramDir(AppContext.BaseDirectory, Environment.ProcessPath);

    internal static string ResolveProgramDir(string baseDirectory, string? processPath)
    {
        var baseDir = Normalize(baseDirectory);
        if (!string.IsNullOrEmpty(processPath))
        {
            var exeDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(exeDir))
            {
                var normalizedExeDir = Normalize(exeDir);
                var baseParent = Path.GetDirectoryName(baseDir);
                if (!string.IsNullOrEmpty(baseParent)
                    && string.Equals(Normalize(baseParent), normalizedExeDir, StringComparison.OrdinalIgnoreCase))
                {
                    return normalizedExeDir;
                }
            }
        }

        return baseDir;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
