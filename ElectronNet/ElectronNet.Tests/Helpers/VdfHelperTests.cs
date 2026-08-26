using ElectronNet.Helpers;
using FluentAssertions;
using ValveKeyValue;

namespace ElectronNet.Tests.Helpers;

[TestFixture]
public class VdfHelperTests
{
    private string _tempDir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响测试结果
        }
    }

    [Test]
    public void Read_ParsesNestedObjects()
    {
        var path = WriteRaw("nested.vdf",
            """
            "root"
            {
            	"key"		"value"
            	"child"
            	{
            		"inner"		"42"
            	}
            }
            """);

        var doc = VdfHelper.Read(path);

        ((string)doc["key"]!).Should().Be("value");
        ((int)doc["child"]!["inner"]!).Should().Be(42);
    }

    [Test]
    public void Read_DecodesEscapeSequencesByDefault()
    {
        // 默认 HasEscapeSequences = true，\\ 应解码为单个反斜杠
        var path = WriteRaw("escaped.vdf",
            """
            "root"
            {
            	"path"		"C:\\Program Files (x86)\\Steam"
            	"quoted"		"say \"hi\""
            }
            """);

        var doc = VdfHelper.Read(path);

        ((string)doc["path"]!).Should().Be(@"C:\Program Files (x86)\Steam");
        ((string)doc["quoted"]!).Should().Be("say \"hi\"");
    }

    [Test]
    public void WriteThenRead_RoundTripsValues()
    {
        var path = Path.Combine(_tempDir, "roundtrip.vdf");
        var original = VdfHelper.Read(WriteRaw("source.vdf",
            """
            "users"
            {
            	"76561198000000001"
            	{
            		"AccountName"		"someone"
            		"Timestamp"		"1700000000"
            	}
            }
            """));

        // 与 LocalFileService.WriteLoginUsersVdf 相同的写法：遍历得到 KVObject 后修改其子键
        foreach (var user in original)
        {
            user["AccountName"] = "changed";
        }
        VdfHelper.Write(path, original);

        var reloaded = VdfHelper.Read(path);
        ((string)reloaded["76561198000000001"]!["AccountName"]!).Should().Be("changed");
        ((int)reloaded["76561198000000001"]!["Timestamp"]!).Should().Be(1700000000);
    }

    [Test]
    public void Write_TruncatesExistingLongerFile()
    {
        // 回归测试：曾用 FileMode.OpenOrCreate 写入，新内容比旧内容短时会残留旧文件尾部
        var path = Path.Combine(_tempDir, "truncate.vdf");
        File.WriteAllText(path, new string('x', 4096));

        var doc = VdfHelper.Read(WriteRaw("short.vdf",
            """
            "root"
            {
            	"a"		"1"
            }
            """));
        VdfHelper.Write(path, doc);

        var written = File.ReadAllText(path);
        written.Should().NotContain("xxxx");
        ((int)VdfHelper.Read(path)["a"]!).Should().Be(1);
    }

    [Test]
    public void Read_WhenFileMissing_Throws()
    {
        var missing = Path.Combine(_tempDir, "nope.vdf");

        // Read 不做兜底，由调用方（LocalFileService）负责先判断文件是否存在
        var act = () => VdfHelper.Read(missing);
        act.Should().Throw<FileNotFoundException>();
    }

    private string WriteRaw(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
