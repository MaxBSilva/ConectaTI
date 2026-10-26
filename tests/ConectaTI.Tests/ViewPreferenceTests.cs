using System.IO;
using ConectaTI.App.Models;
using ConectaTI.App.Services;

namespace ConectaTI.Tests;

public sealed class ViewPreferenceTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "ConectaTI_ViewPrefs_" + Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(11, InterfaceDensity.Compact)] [InlineData(11, InterfaceDensity.Standard)] [InlineData(11, InterfaceDensity.Comfortable)]
    [InlineData(12.5, InterfaceDensity.Compact)] [InlineData(12.5, InterfaceDensity.Standard)] [InlineData(12.5, InterfaceDensity.Comfortable)]
    [InlineData(18, InterfaceDensity.Compact)] [InlineData(18, InterfaceDensity.Standard)] [InlineData(18, InterfaceDensity.Comfortable)]
    public void FontDensityAndPaneRatio_RoundTripIndependently(double font, InterfaceDensity density)
    {
        var settings = new SettingsService(_paths, new AppLogger(_paths));
        settings.Current.FontSize = font;
        settings.Current.Density = density;
        settings.Current.LeftPaneRatio = .37;
        settings.Save();
        var reopened = new SettingsService(_paths, new AppLogger(_paths)).Load();
        Assert.Equal(font, reopened.FontSize);
        Assert.Equal(density, reopened.Density);
        Assert.Equal(.37, reopened.LeftPaneRatio);
    }

    [Theory]
    [InlineData(true, InterfaceDensity.Comfortable)]
    [InlineData(false, InterfaceDensity.Compact)]
    public void LegacyDensity_MigratesWithoutChangingCustomFontOrWidths(bool comfortable, InterfaceDensity expected)
    {
        File.WriteAllText(_paths.SettingsPath, $$"""{"ComfortableMode":{{comfortable.ToString().ToLowerInvariant()}},"FontSize":16,"LeftPaneWidth":278} """);
        var settings = new SettingsService(_paths, new AppLogger(_paths));
        var loaded = settings.Load();
        Assert.Equal(expected, loaded.Density);
        Assert.Equal(16, loaded.FontSize);
        Assert.Equal(278, loaded.LeftPaneWidth);
        Assert.True(double.IsNaN(loaded.LeftPaneRatio));
        settings.Save();
        Assert.Contains("\"Density\"", File.ReadAllText(_paths.SettingsPath));
        Assert.DoesNotContain("ComfortableMode", File.ReadAllText(_paths.SettingsPath));
    }

    [Fact]
    public void ExplicitDensity_WinsOverLegacyFlag_AndBadRatioIsIgnored()
    {
        File.WriteAllText(_paths.SettingsPath, "{\"Density\":1,\"ComfortableMode\":true,\"FontSize\":18,\"LeftPaneRatio\":\"Infinity\"}");
        var loaded = new SettingsService(_paths, new AppLogger(_paths)).Load();
        Assert.Equal(InterfaceDensity.Standard, loaded.Density);
        Assert.Equal(18, loaded.FontSize);
        Assert.True(double.IsNaN(loaded.LeftPaneRatio));
    }

    [Fact]
    public void InvalidDensity_IsNormalized_WithoutResettingValidFont()
    {
        File.WriteAllText(_paths.SettingsPath, "{\"Density\":999,\"FontSize\":16,\"LeftPaneRatio\":0.42}");
        var loaded = new SettingsService(_paths, new AppLogger(_paths)).Load();
        Assert.Equal(InterfaceDensity.Standard, loaded.Density);
        Assert.Equal(16, loaded.FontSize);
        Assert.Equal(.42, loaded.LeftPaneRatio);
    }

    public void Dispose() => Directory.Delete(_paths.RootDirectory, recursive: true);
}
