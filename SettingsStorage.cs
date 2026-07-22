namespace JeekTools;

/// <summary>Where the roaming Config folder is stored.</summary>
public enum StorageLocation
{
    /// <summary>Under %APPDATA%\&lt;AppName&gt;\Config (roaming, per-user).</summary>
    UserDirectory,

    /// <summary>Under a "Config" folder next to the executable (portable).</summary>
    ProgramDirectory,

    /// <summary>Under a "Config" folder beneath a user-chosen base directory.</summary>
    CustomDirectory,
}

/// <summary>
/// Path scheme for app settings and user data, split by roaming behavior:
/// machine-local state always lives under %LOCALAPPDATA%\&lt;AppName&gt;\Config,
/// while roaming data lives in the Config folder of the active storage
/// location (AppData / portable / custom). When a Config folder exists next to
/// the executable, portable mode is forced regardless of the saved location.
/// </summary>
public sealed class SettingsStorage(string appName)
{
    private const string ConfigFolderName = "Config";
    private const string SettingsFileName = "settings.json";

    public string AppName { get; } = appName;

    public string ProgramDir => AppContext.BaseDirectory;

    public string LocalDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName);

    public string LocalConfigDir => Path.Combine(LocalDir, ConfigFolderName);

    public string RoamingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppName);

    public string RoamingConfigDir => Path.Combine(RoamingDir, ConfigFolderName);

    public string ProgramConfigDir => Path.Combine(ProgramDir, ConfigFolderName);

    /// <summary>The machine-local settings file.</summary>
    public string MachineSettingsPath => Path.Combine(LocalConfigDir, SettingsFileName);

    /// <summary>True when startup will use the executable directory for roaming data.</summary>
    public bool IsPortable => ProgramConfigRootExists();

    public bool ProgramConfigRootExists()
    {
        try
        {
            return Directory.Exists(ProgramConfigDir);
        }
        catch
        {
            return false;
        }
    }

    public StorageLocation NormalizeLocation(StorageLocation location) =>
        Enum.IsDefined(location) ? location : StorageLocation.UserDirectory;

    /// <summary>Resolves the location actually used at runtime: an executable-side
    /// Config folder forces portable mode; a saved portable location without that
    /// folder falls back to the user directory.</summary>
    public StorageLocation ResolveEffectiveLocation(StorageLocation location)
    {
        if (ProgramConfigRootExists())
            return StorageLocation.ProgramDirectory;

        var normalized = NormalizeLocation(location);
        return normalized == StorageLocation.ProgramDirectory
            ? StorageLocation.UserDirectory
            : normalized;
    }

    /// <summary>Resolves the Config folder for a given storage location.
    /// <paramref name="customPath"/> is the base directory used when
    /// <paramref name="location"/> is <see cref="StorageLocation.CustomDirectory"/>.</summary>
    public string ResolveConfigRoot(StorageLocation location, string? customPath = null) => location switch
    {
        StorageLocation.ProgramDirectory => ProgramConfigDir,
        StorageLocation.CustomDirectory when !string.IsNullOrWhiteSpace(customPath) =>
            Path.Combine(customPath!, ConfigFolderName),
        _ => RoamingConfigDir,
    };

    /// <summary>Resolves settings.json under the Config folder for a given storage location.</summary>
    public string ResolveSettingsPath(StorageLocation location, string? customPath = null) =>
        Path.Combine(ResolveConfigRoot(location, customPath), SettingsFileName);

    /// <summary>Deletes the executable-side Config folder after leaving portable mode.</summary>
    public bool TryDeleteProgramConfig(out string? error)
    {
        error = null;
        try
        {
            var configRoot = Path.GetFullPath(ProgramConfigDir).TrimEnd(Path.DirectorySeparatorChar);
            var programRoot = Path.GetFullPath(ProgramDir).TrimEnd(Path.DirectorySeparatorChar);
            if (!configRoot.StartsWith(programRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = "Program Config path is outside the executable directory.";
                return false;
            }

            if (Directory.Exists(configRoot))
                Directory.Delete(configRoot, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Moves the whole roaming Config folder to a new root. Existing destination
    /// files with the same relative paths are replaced by the current Config.
    /// </summary>
    public static void MoveConfigRoot(string sourceRoot, string destRoot)
    {
        var source = NormalizeDirectoryPath(sourceRoot);
        var dest = NormalizeDirectoryPath(destRoot);
        using var lease = SharedDataFile.AcquireMany(source, dest);
        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
            return;

        if (IsSameOrInside(source, dest) || IsSameOrInside(dest, source))
            throw new InvalidOperationException("Config cannot be moved into itself or a nested folder.");

        if (!Directory.Exists(source))
        {
            Directory.CreateDirectory(dest);
            return;
        }

        var destParent = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destParent))
            Directory.CreateDirectory(destParent);

        if (!Directory.Exists(dest) && TryRenameDirectory(source, dest))
            return;

        MoveDirectoryContents(source, dest);
        Directory.Delete(source, recursive: true);
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSameOrInside(string folder, string candidate)
    {
        if (string.Equals(folder, candidate, StringComparison.OrdinalIgnoreCase))
            return true;

        return candidate.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var target = Path.Combine(destDir, Path.GetFileName(file));
            if (File.Exists(target))
                File.Delete(target);
            File.Move(file, target);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var target = Path.Combine(destDir, Path.GetFileName(dir.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)));
            if (!Directory.Exists(target) && TryRenameDirectory(dir, target))
                continue;

            MoveDirectoryContents(dir, target);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Attempts a fast same-volume directory rename. Returns <c>false</c> when the
    /// move crosses drives (<see cref="Directory.Move"/> cannot move across
    /// volumes), so the caller falls back to a recursive copy+delete.
    /// </summary>
    private static bool TryRenameDirectory(string source, string dest)
    {
        try
        {
            Directory.Move(source, dest);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
