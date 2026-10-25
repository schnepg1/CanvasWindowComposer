using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class CanvasPreferencesTests : IDisposable
{
    [Fact]
    public void AppNavigationAnimationDefaultsOnAndCanBeDisabledWithoutChangingStartupMotion()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "config.ini"), "EnableStartupIntro=false\nReduceStartupMotion=true\n");
        using var config = new AppConfig(directory: _directory); config.Load();
        Assert.True(config.AnimateWindowNavigation);
        foreach (bool enabled in new[] { false, true })
        {
            config.SavePreferences(config.Preferences with { AnimateWindowNavigation = enabled });
            using var reopened = new AppConfig(directory: _directory); reopened.Load();
            Assert.Equal(enabled, reopened.AnimateWindowNavigation);
            Assert.False(reopened.EnableStartupIntro);
            Assert.True(reopened.ReduceStartupMotion);
        }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "canvas-preferences-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultsKeepExperimentalResourceAndRenderingOptionsDisabled()
    {
        using var config = new AppConfig(directory: _directory); config.Load();
        Assert.Equal(new(true, true, true, true, true, false, false, false), config.Preferences);
        Assert.Contains(";EnableFlipRendering=false", File.ReadAllText(Path.Combine(_directory, "config.ini")));
        Assert.False(config.LowQualityMode);
        Assert.Equal(.40, config.OverviewCardEnterZoom);
        Assert.Equal(.45, config.OverviewCardExitZoom);
        Assert.Equal(.10, config.OverviewSimpleCardEnterZoom);
        Assert.Equal(.13, config.OverviewSimpleCardExitZoom);
    }

    [Fact]
    public void SaveRoundTripsAllSettingsAndPreservesCommentsUnknownKeysAndDuplicates()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "config.ini"),
            "; My comment\n[CanvasWindowComposer]\nFutureOption=keep\nDisableSearch=false\ndisablesearch=false\n");
        using var config = new AppConfig(directory: _directory); config.Load();
        var wanted = new CanvasPreferences(false, false, false, false, false, true, true, true, false, true, true);
        config.SavePreferences(wanted);
        Assert.Equal(wanted, config.Preferences);
        using var reopened = new AppConfig(directory: _directory); reopened.Load();
        Assert.Equal(wanted, reopened.Preferences);
        string contents = File.ReadAllText(Path.Combine(_directory, "config.ini"));
        Assert.Contains("; My comment", contents); Assert.Contains("FutureOption=keep", contents);
        Assert.Contains("DisableSearch=true", contents); Assert.Contains("disablesearch=true", contents);
    }

    [Fact]
    public void LegacyConfigEnablesIntroByDefaultAndMotionPreferencesRoundTrip()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "config.ini"), "DisableSearch=false\n");
        using var config = new AppConfig(directory: _directory); config.Load();
        Assert.True(config.EnableStartupIntro);
        Assert.False(config.ReduceStartupMotion);
        config.SavePreferences(config.Preferences with { StartupIntro = false, ReducedIntroMotion = true });
        using var reopened = new AppConfig(directory: _directory); reopened.Load();
        Assert.False(reopened.EnableStartupIntro);
        Assert.True(reopened.ReduceStartupMotion);
    }

    [Fact]
    public void EditingADetachedSnapshotDoesNotChangeRunningPolicy()
    {
        using var config = new AppConfig(directory: _directory); config.Load();
        var edits = config.Preferences with { AutomaticMinimization = true, FlipRendering = true };
        Assert.False(config.EnableAutomaticMinimization); Assert.False(config.EnableFlipRendering);
        config.SavePreferences(edits);
        Assert.True(config.EnableAutomaticMinimization); Assert.True(config.EnableFlipRendering);
    }

    [Fact]
    public void LowQualityIsOptInAndCanBeDisabledAgainWithoutChangingOtherSettings()
    {
        using var config = new AppConfig(directory: _directory); config.Load();
        var original = config.Preferences;
        config.SavePreferences(original with { LowQualityMode = true });
        Assert.True(config.LowQualityMode);
        using var reopened = new AppConfig(directory: _directory); reopened.Load();
        Assert.Equal(original with { LowQualityMode = true }, reopened.Preferences);
        config.SavePreferences(original);
        reopened.Load();
        Assert.False(reopened.LowQualityMode);
        Assert.Equal(original, reopened.Preferences);
    }

    [Fact]
    public void FailedSaveKeepsTheRunningPreferences()
    {
        using var config = new AppConfig(directory: _directory); config.Load();
        var original = config.Preferences;
        using var locked = new FileStream(Path.Combine(_directory, "config.ini"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => config.SavePreferences(original with { AutomaticMinimization = true }));
        Assert.Equal(original, config.Preferences);
    }

    [Fact]
    public void SelectionRadiusRoundTripsWithoutChangingBooleanPreferences()
    {
        using var config=new AppConfig(directory:_directory);config.Load();var preferences=config.Preferences;
        config.SetSelectionRadius(2400);
        config.SavePreferences(preferences with { StartupIntro=false });
        using var reopened=new AppConfig(directory:_directory);reopened.Load();
        Assert.Equal(2400,reopened.SelectionRadius);Assert.Equal(preferences with { StartupIntro=false },reopened.Preferences);
        Assert.Throws<ArgumentOutOfRangeException>(()=>config.SetSelectionRadius(double.NaN));
        Assert.Equal(2400,config.SelectionRadius);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("-50")]
    [InlineData("100001")]
    public void InvalidSelectionRadiusUsesTheDefault(string radius)
    {
        Directory.CreateDirectory(_directory);File.WriteAllText(Path.Combine(_directory,"config.ini"),"SelectionRadius="+radius);
        using var config=new AppConfig(directory:_directory);config.Load();Assert.Equal(1200,config.SelectionRadius);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
