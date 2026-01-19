#if WINDOWS

using System.Runtime.Versioning;

// ReSharper disable once CheckNamespace
namespace Microsoft.Win32;

public static class Win32RegistryKeyExtension
{
    /// <summary>
    /// 读取注册表值
    /// </summary>
    [SupportedOSPlatform("Windows")]
    public static T? Read<T>(this RegistryKey rk, string name)
    {
        var value = rk.GetValue(name)?.ToString();
        if (value == null)
        {
            return default;
        }

        if (typeof(T) == typeof(string))
        {
            return (T)Convert.ChangeType(value, TypeCode.String);
        }
        else if (typeof(T) == typeof(int) || typeof(T) == typeof(int?))
        {
            return (T)Convert.ChangeType(value, TypeCode.Int32);
        }
        else if (typeof(T) == typeof(long) || typeof(T) == typeof(long?))
        {
            return (T)Convert.ChangeType(value, TypeCode.Int64);
        }

        return default;
    }
}

#endif