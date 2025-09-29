using Microsoft.Win32;

namespace JeekTools;

public static class RegistryHelper
{
    public static string? GetValue(string keyName, string? valueName, string? defaultValue)
    {
        var value = Registry.GetValue(keyName, valueName, defaultValue);
        return (string?)(value ?? defaultValue);
    }

    public static int GetValue(string keyName, string? valueName, int defaultValue)
    {
        var value = Registry.GetValue(keyName, valueName, defaultValue);
        return (int)(value ?? defaultValue);
    }

    public static byte[]? GetBinaryValue(string keyName, string? valueName, byte[]? defaultValue)
    {
        var value = Registry.GetValue(keyName, valueName, null);
        return (byte[]?)(value ?? defaultValue);
    }

    public static void SetValue(string keyName, string? valueName, string value)
    {
        var currentValue = GetValue(keyName, valueName, null);
        if (currentValue != null && currentValue == value)
            return;

        Registry.SetValue(keyName, valueName, value);
    }

    public static void SetValue(string keyName, string? valueName, int value)
    {
        var currentValue = Registry.GetValue(keyName, valueName, null);
        if (currentValue != null && (int)currentValue == value)
            return;

        Registry.SetValue(keyName, valueName, value);
    }

    public static void SetBinaryValue(string keyName, string? valueName, byte[] value)
    {
        Registry.SetValue(keyName, valueName, value, RegistryValueKind.Binary);
    }

    public static void SetValue(
        string keyName,
        string? valueName,
        object value,
        RegistryValueKind valueKind
    )
    {
        Registry.SetValue(keyName, valueName, value, valueKind);
    }

    public static RegistryKey GetBaseKeyFromKeyName(string keyName, out string subKeyName)
    {
        var num1 = keyName.IndexOf('\\');
        var num2 = num1 != -1 ? num1 : keyName.Length;
        var baseKeyFromKeyName = num2 switch
        {
            10 => Registry.Users,
            17 => char.ToUpperInvariant(keyName[6]) == 'L'
                ? Registry.ClassesRoot
                : Registry.CurrentUser,
            18 => Registry.LocalMachine,
            19 => Registry.CurrentConfig,
            21 => Registry.PerformanceData,
            _ => null,
        };

        if (
            baseKeyFromKeyName == null
            || !keyName.StartsWith(baseKeyFromKeyName.Name, StringComparison.OrdinalIgnoreCase)
        )
            throw new ArgumentException($"Invalid key name: {keyName}");
        subKeyName =
            num1 == -1 || num1 == keyName.Length
                ? string.Empty
                : keyName.Substring(num1 + 1, keyName.Length - num1 - 1);

        return baseKeyFromKeyName;
    }

    public static void DeleteKey(string keyName)
    {
        var rootKey = GetBaseKeyFromKeyName(keyName, out var subKeyPath);
        var subKey = rootKey.OpenSubKey(subKeyPath);
        if (subKey == null)
            return;

        subKey.Close();
        rootKey.DeleteSubKeyTree(subKeyPath, false);
    }

    public static void DeleteValue(string keyName, string valueName)
    {
        using var key = OpenKey(keyName, true);
        key?.DeleteValue(valueName, false);
    }

    public static RegistryKey? OpenKey(string keyName, bool writable = false)
    {
        var rootKey = GetBaseKeyFromKeyName(keyName, out var keyPath);
        return rootKey.OpenSubKey(keyPath, writable);
    }
}
