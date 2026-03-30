using ElectronNet.Models.Settings;
using FluentAssertions;

namespace ElectronNet.Tests.Models;

[TestFixture]
public class AppSettingsTests
{
    [Test]
    public void DefaultSettings_ShouldHaveValidValues()
    {
        // Arrange & Act
        var defaultSettings = AppSettings.DefaultSettings;

        // Assert
        defaultSettings.Should().NotBeNull();
        defaultSettings.AutoStart.Should().BeFalse();
        defaultSettings.SilentStart.Should().BeFalse();
        defaultSettings.AutoUpdate.Should().BeTrue();
        defaultSettings.Language.Should().BeNullOrEmpty();
        defaultSettings.CloseAction.Should().Be("ask");
        defaultSettings.HomePage.Should().Be("/status");
        defaultSettings.ColorScheme.Should().Be("system");
        defaultSettings.ThemeColor.Should().Be("blue");
        defaultSettings.Radius.Should().Be(0.5);
        defaultSettings.UpdateAppRunningStatusJob.Should().NotBeNull();
        defaultSettings.UpdateAppRunningStatusJob!.Enabled.Should().BeTrue();
        defaultSettings.UpdateAppRunningStatusJob.IntervalSeconds.Should().Be(5);
    }

    [Test]
    public void AppSettings_ShouldBeSerializable()
    {
        // Arrange
        var settings = new AppSettings
        {
            AutoStart = true,
            SilentStart = false,
            AutoUpdate = true,
            Language = "zh-CN",
            CloseAction = "exit",
            HomePage = "/user",
            ColorScheme = "dark",
            ThemeColor = "orange",
            Radius = 0.75,
            UpdateAppRunningStatusJob = new UpdateAppRunningStatusJob
            {
                Enabled = true,
                IntervalSeconds = 10
            }
        };

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.AutoStart.Should().Be(settings.AutoStart);
        deserialized.Language.Should().Be(settings.Language);
        deserialized.ColorScheme.Should().Be(settings.ColorScheme);
        deserialized.ThemeColor.Should().Be(settings.ThemeColor);
        deserialized.Radius.Should().Be(settings.Radius);
        deserialized.UpdateAppRunningStatusJob.Should().NotBeNull();
        deserialized.UpdateAppRunningStatusJob!.Enabled.Should().Be(settings.UpdateAppRunningStatusJob!.Enabled);
    }

    [Test]
    [TestCase("light")]
    [TestCase("dark")]
    [TestCase("system")]
    public void ColorScheme_ShouldAcceptValidValues(string colorScheme)
    {
        // Arrange & Act
        var settings = new AppSettings
        {
            ColorScheme = colorScheme
        };

        // Assert
        settings.ColorScheme.Should().Be(colorScheme);
    }

    [Test]
    [TestCase("/status")]
    [TestCase("/user")]
    [TestCase("/app")]
    [TestCase("/useRecord")]
    public void HomePage_ShouldAcceptValidValues(string homePage)
    {
        // Arrange & Act
        var settings = new AppSettings
        {
            HomePage = homePage
        };

        // Assert
        settings.HomePage.Should().Be(homePage);
    }

    [Test]
    [TestCase(0)]
    [TestCase(0.5)]
    [TestCase(1)]
    public void Radius_ShouldAcceptValidValues(double radius)
    {
        // Arrange & Act
        var settings = new AppSettings
        {
            Radius = radius
        };

        // Assert
        settings.Radius.Should().Be(radius);
    }
}
