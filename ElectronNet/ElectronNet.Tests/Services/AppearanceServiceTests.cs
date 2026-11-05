using ElectronNet.Services;
using FluentAssertions;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class AppearanceServiceTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task Import_CopiesTheImageAndReplacesThePreviousBackground()
    {
        var first = Path.Combine(_root, "first.PNG");
        var second = Path.Combine(_root, "second.jpg");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var directory = Path.Combine(_root, "Backgrounds");

        var firstResult = await AppearanceService.ImportAsync(first, directory);
        var secondResult = await AppearanceService.ImportAsync(second, directory);

        firstResult.Path.Should().EndWith(".png");
        secondResult.Error.Should().BeNull();
        File.ReadAllText(secondResult.Path!).Should().Be("second");
        Directory.GetFiles(directory).Should().ContainSingle().Which.Should().Be(secondResult.Path);
    }

    [Test]
    public async Task Import_KeepsTheBackgroundWhenTheSameImageIsPickedAgain()
    {
        var source = Path.Combine(_root, "image.png");
        File.WriteAllText(source, "image");
        var directory = Path.Combine(_root, "Backgrounds");

        var first = await AppearanceService.ImportAsync(source, directory);
        var second = await AppearanceService.ImportAsync(source, directory);

        second.Path.Should().Be(first.Path);
        File.Exists(second.Path).Should().BeTrue();
    }

    [Test]
    public async Task Import_RejectsOtherFileTypes()
    {
        var source = Path.Combine(_root, "notes.svg");
        File.WriteAllText(source, "<svg/>");

        var result = await AppearanceService.ImportAsync(source, Path.Combine(_root, "Backgrounds"));

        result.Error.Should().Be("unsupported");
        result.Path.Should().BeNull();
    }

    [Test]
    public async Task Import_RejectsOversizedImages()
    {
        var source = Path.Combine(_root, "huge.png");
        await using (var stream = File.Create(source))
        {
            stream.SetLength(AppearanceService.MaxBackgroundBytes + 1);
        }

        var result = await AppearanceService.ImportAsync(source, Path.Combine(_root, "Backgrounds"));

        result.Error.Should().Be("tooLarge");
    }
}
