using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SteamStat.Platform.Linux;

/// <summary>
/// 通过 libsecret（freedesktop Secret Service）读写主密钥。
/// 直接 P/Invoke libsecret-1 与 glib：只用非变参的 *v_sync 函数，属性表用 GHashTable 传递。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class LibSecretKeyring : ICredentialKeyring
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    private const string SchemaName = "asia.dyh1319.steamstat.CredentialKey";
    private const string Label = "Steam Stat credential key";

    // SecretSchema 布局（x86_64 / aarch64）：name(8) flags(4+4) attributes[32]{name(8) type(4+4)} reserved(4+4) reserved1..7(7×8)
    private const int SchemaSize = 8 + 8 + 32 * 16 + 8 + 7 * 8;

    private static readonly (string Name, string Value)[] Attributes =
    [
        ("application", "steam-stat"),
        ("purpose", "credential-key-v1")
    ];

    public byte[]? Load()
    {
        var password = Invoke(static (schema, attributes) =>
        {
            IntPtr error;
            var result = secret_password_lookupv_sync(schema, attributes, IntPtr.Zero, &error);
            ThrowIfError(error, "lookup");
            return result;
        });
        if (password == IntPtr.Zero) return null;
        try
        {
            var value = Marshal.PtrToStringUTF8(password);
            return string.IsNullOrEmpty(value) ? null : Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
        finally
        {
            secret_password_free(password);
        }
    }

    public void Store(byte[] key)
    {
        var encoded = Convert.ToBase64String(key);
        Invoke((schema, attributes) =>
        {
            var label = Marshal.StringToCoTaskMemUTF8(Label);
            var password = Marshal.StringToCoTaskMemUTF8(encoded);
            try
            {
                IntPtr error;
                var stored = secret_password_storev_sync(schema, attributes, IntPtr.Zero, label, password, IntPtr.Zero, &error);
                ThrowIfError(error, "store");
                if (stored == 0) throw new KeyringUnavailableException("Secret Service refused to store the credential key");
                return IntPtr.Zero;
            }
            finally
            {
                Marshal.ZeroFreeCoTaskMemUTF8(password);
                Marshal.FreeCoTaskMem(label);
            }
        });
    }

    private static IntPtr Invoke(Func<IntPtr, IntPtr, IntPtr> call)
    {
        var allocations = new List<IntPtr>();
        var schema = IntPtr.Zero;
        var table = IntPtr.Zero;
        try
        {
            schema = Marshal.AllocHGlobal(SchemaSize);
            new Span<byte>((void*)schema, SchemaSize).Clear();
            var name = Marshal.StringToCoTaskMemUTF8(SchemaName);
            allocations.Add(name);
            Marshal.WriteIntPtr(schema, 0, name);
            // flags = SECRET_SCHEMA_NONE (0)；attributes 从偏移 16 开始，每项 16 字节，类型 SECRET_SCHEMA_ATTRIBUTE_STRING (0)
            for (var i = 0; i < Attributes.Length; i++)
            {
                var attributeName = Marshal.StringToCoTaskMemUTF8(Attributes[i].Name);
                allocations.Add(attributeName);
                Marshal.WriteIntPtr(schema, 16 + i * 16, attributeName);
            }

            var glib = NativeLibrary.Load(LibGLib);
            table = g_hash_table_new(NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"));
            foreach (var (key, value) in Attributes)
            {
                var keyPtr = Marshal.StringToCoTaskMemUTF8(key);
                var valuePtr = Marshal.StringToCoTaskMemUTF8(value);
                allocations.Add(keyPtr);
                allocations.Add(valuePtr);
                g_hash_table_insert(table, keyPtr, valuePtr);
            }

            return call(schema, table);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new KeyringUnavailableException("libsecret is not installed", exception);
        }
        finally
        {
            if (table != IntPtr.Zero) g_hash_table_unref(table);
            if (schema != IntPtr.Zero) Marshal.FreeHGlobal(schema);
            foreach (var allocation in allocations) Marshal.FreeCoTaskMem(allocation);
        }
    }

    private static void ThrowIfError(IntPtr error, string operation)
    {
        if (error == IntPtr.Zero) return;
        // GError { GQuark domain; gint code; gchar *message; }
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unknown error";
        g_error_free(error);
        throw new KeyringUnavailableException($"Secret Service {operation} failed: {message}");
    }

    [DllImport(LibSecret)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, IntPtr* error);

    [DllImport(LibSecret)]
    private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, IntPtr* error);

    [DllImport(LibSecret)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport(LibGLib)]
    private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr keyEqualFunc);

    [DllImport(LibGLib)]
    private static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(LibGLib)]
    private static extern void g_hash_table_unref(IntPtr table);

    [DllImport(LibGLib)]
    private static extern void g_error_free(IntPtr error);
}
