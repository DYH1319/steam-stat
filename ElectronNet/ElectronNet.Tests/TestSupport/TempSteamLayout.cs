namespace ElectronNet.Tests.TestSupport;

/// <summary>
/// 在临时目录里搭出一个仿真的 Steam 目录结构，让解析类测试无需依赖本机真实 Steam 安装。
///
/// 结构：
/// <code>
/// {Root}/            ← 相当于 SteamPath
///   config/loginusers.vdf
///   config/libraryfolders.vdf
///   steamapps/appmanifest_*.acf
/// {Root}/../Library2/
///   steamapps/appmanifest_*.acf
/// </code>
/// </summary>
public sealed class TempSteamLayout : IDisposable
{
    private static readonly string FixturesDir =
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

    public string BaseDir { get; }

    /// <summary>主 Steam 安装目录（等价于 GlobalStatus.SteamPath）</summary>
    public string SteamPath { get; }

    public TempSteamLayout()
    {
        BaseDir = Path.Combine(Path.GetTempPath(), "steam-stat-tests", Guid.NewGuid().ToString("N"));
        SteamPath = Path.Combine(BaseDir, "Steam");
        Directory.CreateDirectory(SteamPath);
    }

    /// <summary>把某个 fixture 复制到 {SteamPath}/config/ 下</summary>
    public TempSteamLayout WithConfigFile(string fixtureName)
    {
        var configDir = Path.Combine(SteamPath, "config");
        Directory.CreateDirectory(configDir);
        File.Copy(FixturePath(fixtureName), Path.Combine(configDir, fixtureName));
        return this;
    }

    /// <summary>
    /// 创建一个库目录并把指定的 appmanifest fixture 复制进 steamapps/。
    /// </summary>
    /// <param name="libraryName">库目录名；传 "Steam" 即复用主安装目录</param>
    /// <returns>该库的绝对路径，可直接传给 ReadAllAppManifestAcfs</returns>
    public string WithLibrary(string libraryName, params string[] manifestFixtures)
    {
        var libraryPath = Path.Combine(BaseDir, libraryName);
        var steamAppsDir = Path.Combine(libraryPath, "steamapps");
        Directory.CreateDirectory(steamAppsDir);

        foreach (var fixture in manifestFixtures)
        {
            File.Copy(FixturePath(fixture), Path.Combine(steamAppsDir, fixture));
        }

        return libraryPath;
    }

    private static string FixturePath(string fixtureName)
    {
        var path = Path.Combine(FixturesDir, fixtureName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"测试 fixture 不存在：{path}。请确认 ElectronNet.Tests.csproj 已把 Fixtures\\ 复制到输出目录。", path);
        }
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(BaseDir))
            {
                Directory.Delete(BaseDir, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不应影响测试结果
        }
    }
}
