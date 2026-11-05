using ElectronNet.Hosting;
using ElectronNET.API.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElectronNet.Tests.Hosting;

[TestFixture]
public sealed class WindowStateTests
{
    private static readonly Rectangle Primary = new() { X = 0, Y = 0, Width = 1920, Height = 1040 };
    private static readonly Rectangle Secondary = new() { X = 1920, Y = 0, Width = 1280, Height = 800 };

    [Test]
    public void Fit_KeepsAWindowThatIsStillOnScreen()
    {
        var bounds = WindowBoundsRules.Fit(new SavedWindowState(100, 80, 1400, 900, false), [Primary, Secondary], 960, 600);

        bounds.Should().BeEquivalentTo(new Rectangle { X = 100, Y = 80, Width = 1400, Height = 900 });
    }

    [Test]
    public void Fit_ReturnsNullWhenTheDisplayIsGone()
    {
        WindowBoundsRules.Fit(new SavedWindowState(2200, 100, 1000, 700, false), [Primary], 960, 600).Should().BeNull();
    }

    [Test]
    public void Fit_ShrinksAndMovesAWindowOntoTheDisplayItMostlyCovers()
    {
        var bounds = WindowBoundsRules.Fit(new SavedWindowState(1900, 200, 1800, 1000, false), [Primary, Secondary], 960, 600);

        bounds.Should().BeEquivalentTo(new Rectangle { X = 1920, Y = 0, Width = 1280, Height = 800 });
    }

    [Test]
    public void Fit_NeverGoesBelowTheMinimumSize()
    {
        var small = new Rectangle { X = 0, Y = 0, Width = 800, Height = 500 };

        var bounds = WindowBoundsRules.Fit(new SavedWindowState(0, 0, 700, 400, false), [small], 960, 600);

        bounds.Should().BeEquivalentTo(new Rectangle { X = 0, Y = 0, Width = 960, Height = 600 });
    }

    [Test]
    public void Fit_RejectsAnEmptySavedSize()
    {
        WindowBoundsRules.Fit(new SavedWindowState(0, 0, 0, 0, false), [Primary], 960, 600).Should().BeNull();
    }

    [Test]
    public void FitSize_DefaultWindowFitsASteamDeckScreen()
    {
        WindowBoundsRules.FitSize(1820, 1080, new Rectangle { X = 0, Y = 0, Width = 1280, Height = 800 }, 960, 600)
            .Should().Be((1280, 800));
    }

    [Test]
    public void Store_RoundTripsMainAndPopoutStates()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WindowStateStore(directory, NullLogger<WindowStateStore>.Instance);
            store.SaveMain(new SavedWindowState(1, 2, 1000, 700, true));
            store.SavePopout("game", new SavedWindowState(3, 4, 800, 600, false));

            var loaded = new WindowStateStore(directory, NullLogger<WindowStateStore>.Instance).Load();

            loaded.Main.Should().Be(new SavedWindowState(1, 2, 1000, 700, true));
            loaded.Popouts["game"].Should().Be(new SavedWindowState(3, 4, 800, 600, false));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Store_IgnoresACorruptFile()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "window-state.json"), "{ not json");

            new WindowStateStore(directory, NullLogger<WindowStateStore>.Instance).Load().Main.Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestCase("mica", true, "mica")]
    [TestCase("acrylic", true, "acrylic")]
    [TestCase("mica", false, "none")]
    [TestCase("tabbed", true, "none")]
    [TestCase(null, true, "none")]
    public void WindowMaterial_FallsBackToNone(string? material, bool supported, string expected)
    {
        WindowMaterials.Normalize(material, supported).Should().Be(expected);
    }
}
