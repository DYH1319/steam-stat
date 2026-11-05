using ElectronNET.API;

namespace ElectronNet.Hosting;

/// <summary>Mica / 亚克力窗口材质。系统不支持时一律按 none 处理，渲染层据此回退到不透明背景。</summary>
internal static class WindowMaterials
{
    public const string None = "none";
    public const string TransparentColor = "#00000000";
    private const string OpaqueColor = "#FFFFFF";

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE 从 Windows 11 22H2（build 22621）开始提供</summary>
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    public static string Effective(string? material) => Normalize(material, Supported);

    internal static string Normalize(string? material, bool supported)
        => supported && material is "mica" or "acrylic" ? material : None;

    public static void Apply(BrowserWindow window, string? material)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return;
        var effective = Effective(material);
        // 窗口底色不透明时系统材质会被盖住
        window.SetBackgroundColor(effective == None ? OpaqueColor : TransparentColor);
        window.SetBackgroundMaterial(effective);
    }
}
