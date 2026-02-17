#if WINDOWS

using System.Runtime.Versioning;

// ReSharper disable once CheckNamespace
namespace Microsoft.Win32;

public static class Win32RegistryKeyExtension
{
    extension(RegistryKey rk)
    {
        /// <summary>
        /// 读取注册表值
        /// </summary>
        [SupportedOSPlatform("Windows")]
        public T? Read<T>(string name)
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

        /// <summary>
        /// 写入注册表值
        /// </summary>
        [SupportedOSPlatform("Windows")]
        public void Write(string path, string name, object value, RegistryValueKind valueKind)
        {
            var openedRk = rk.OpenSubKey(path, true);
            if (openedRk != null) // 该项必须已存在
            {
                openedRk.SetValue(name, value, valueKind);
                openedRk.Close();
            }
        }
    }
}

#endif