using SteamStat.Platform.Linux;

namespace SteamStat.Platform.Linux.Tests;

/// <summary>在临时目录中搭建假的家目录、Steam 安装与 /proc。</summary>
internal sealed class LinuxFixture : IDisposable
{
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public LinuxFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "steam-stat-linux-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        Proc = Path.Combine(Root, "proc");
        Bin = Path.Combine(Root, "bin");
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Proc);
        Directory.CreateDirectory(Bin);
    }

    public string Root { get; }
    public string Home { get; }
    public string Proc { get; }
    public string Bin { get; }
    public string SteamRoot => Path.Combine(Home, ".local", "share", "Steam");
    public string RegistryFile => Path.Combine(Home, ".steam", "registry.vdf");

    public LinuxSystemContext Context => new()
    {
        HomeDirectory = Home,
        ProcRoot = Proc,
        ExecutableSearchPaths = [Bin],
        GetEnvironmentVariable = name => _environment.GetValueOrDefault(name)
    };

    public LinuxFixture WithEnvironment(string name, string value)
    {
        _environment[name] = value;
        return this;
    }

    public LinuxFixture WithNativeSteam(string registry)
    {
        Directory.CreateDirectory(Path.Combine(SteamRoot, "steamapps"));
        Directory.CreateDirectory(Path.Combine(SteamRoot, "config"));
        Directory.CreateDirectory(Path.Combine(Home, ".steam"));
        File.WriteAllText(RegistryFile, registry);
        return this;
    }

    public LinuxFixture WithExecutable(string name)
    {
        File.WriteAllText(Path.Combine(Bin, name), "#!/bin/sh\n");
        return this;
    }

    public LinuxFixture WithProcess(int pid, string comm, params string[] commandLine)
    {
        var directory = Path.Combine(Proc, pid.ToString());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "comm"), comm + "\n");
        File.WriteAllText(Path.Combine(directory, "cmdline"), string.Join('\0', commandLine) + "\0");
        return this;
    }

    public LinuxFixture WithProcessLinks(int pid, string exe, string cwd)
    {
        var directory = Path.Combine(Proc, pid.ToString());
        File.WriteAllText(Path.Combine(directory, "exe"), exe);
        File.WriteAllText(Path.Combine(directory, "cwd"), cwd);
        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static string Registry(string steamBody, string machineBody = "") => $$"""
        "Registry"
        {
        	"HKLM"
        	{
        		"Software"
        		{
        			"Valve"
        			{
        				"Steam"
        				{
        {{machineBody}}
        				}
        			}
        		}
        	}
        	"HKCU"
        	{
        		"Software"
        		{
        			"Valve"
        			{
        				"Steam"
        				{
        {{steamBody}}
        				}
        			}
        		}
        	}
        }
        """;
}
