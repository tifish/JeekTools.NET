using Microsoft.Win32;

namespace JeekTools;

public struct RegistryValue(string keyName, string? valueName)
{
    public string KeyName { get; set; } = keyName;
    public string? ValueName { get; set; } = valueName;

    public readonly string? GetValue(string? defaultValue)
    {
        return RegistryHelper.GetValue(KeyName, ValueName, defaultValue);
    }

    public readonly int GetValue(int defaultValue)
    {
        return RegistryHelper.GetValue(KeyName, ValueName, defaultValue);
    }

    public readonly void SetValue(string value)
    {
        RegistryHelper.SetValue(KeyName, ValueName, value);
    }

    public readonly void SetValue(object value, RegistryValueKind valueKind)
    {
        RegistryHelper.SetValue(KeyName, ValueName, value, valueKind);
    }

    public readonly void SetValue(int value)
    {
        RegistryHelper.SetValue(KeyName, ValueName, value);
    }

    public readonly void DeleteValue()
    {
        RegistryHelper.DeleteValue(KeyName, ValueName!);
    }

    public readonly void DeleteKey()
    {
        RegistryHelper.DeleteKey(KeyName);
    }
}
