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

    public readonly byte[]? GetBinaryValue(byte[]? defaultValue)
    {
        return RegistryHelper.GetBinaryValue(KeyName, ValueName, defaultValue);
    }

    public readonly bool GetBitFromBinary(int bitIndex, bool defaultValue)
    {
        var binaryValue = GetBinaryValue(null);
        if (binaryValue == null)
            return defaultValue;

        var byteIndex = bitIndex / 8;
        if (byteIndex >= binaryValue.Length)
            return defaultValue;

        return (binaryValue[byteIndex] & (1 << (bitIndex % 8))) != 0;
    }

    public readonly void SetBitToBinary(int bitIndex, bool value)
    {
        var binaryValue = GetBinaryValue(null);
        if (binaryValue == null)
            binaryValue = new byte[1];

        var byteIndex = bitIndex / 8;
        if (byteIndex >= binaryValue.Length)
        {
            var newBinaryValue = new byte[byteIndex + 1];
            binaryValue.CopyTo(newBinaryValue, 0);
            binaryValue = newBinaryValue;
        }

        if (value)
            binaryValue[byteIndex] |= (byte)(1 << (bitIndex % 8));
        else
            binaryValue[byteIndex] &= (byte)~(1 << (bitIndex % 8));

        SetBinaryValue(binaryValue);
    }

    public readonly void SetValue(string value)
    {
        RegistryHelper.SetValue(KeyName, ValueName, value);
    }

    public readonly void SetValue(int value)
    {
        RegistryHelper.SetValue(KeyName, ValueName, value);
    }

    public readonly void SetBinaryValue(byte[] value)
    {
        RegistryHelper.SetBinaryValue(KeyName, ValueName, value);
    }

    public readonly void SetValue(object value, RegistryValueKind valueKind)
    {
        RegistryHelper.SetValue(KeyName, ValueName, value, valueKind);
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
