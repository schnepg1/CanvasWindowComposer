using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class FlightSettingsControlTests
{
    [Fact]
    public void FlightSettingsControlDefaultsToThreeSecondsHelicopterAndScaleOne()
        => WorkspaceViewTests.OnSta(() =>
        {
            var preferences = new AppConfig().Preferences;
            var control = new FlightSettingsControl(preferences);

            Assert.Equal(3.0, control.SelectedDuration, precision: 2);
            Assert.Equal(1.0, control.SelectedCruisingScale, precision: 2);
            Assert.Equal("Helicopter", control.SelectedProfile);
        });

    [Fact]
    public void FlightSettingsControlAllowsDurationFromHalfSecondUpToTenSeconds()
        => WorkspaceViewTests.OnSta(() =>
        {
            var preferences = new AppConfig().Preferences;
            var control = new FlightSettingsControl(preferences);

            var durationSlider = WorkspaceViewTests.Descendants(control).OfType<Slider>()
                .Single(s => AutomationProperties.GetName(s) == "Flight duration in seconds");

            Assert.Equal(0.5, durationSlider.Minimum);
            Assert.Equal(10.0, durationSlider.Maximum);

            // Test setting to maximum allowed (10.0s)
            durationSlider.Value = 10.0;
            Assert.Equal(10.0, control.SelectedDuration, precision: 2);

            Assert.True(control.TryApply(preferences, out var updatedMax, out var errorMax));
            Assert.Null(errorMax);
            Assert.Equal(10.0, updatedMax.FlightDurationSeconds, precision: 2);

            // Test setting to 0.5s
            durationSlider.Value = 0.5;
            Assert.Equal(0.5, control.SelectedDuration, precision: 2);
            Assert.True(control.TryApply(preferences, out var updatedMin, out var errorMin));
            Assert.Null(errorMin);
            Assert.Equal(0.5, updatedMin.FlightDurationSeconds, precision: 2);
        });

    [Fact]
    public void ResetDefaultsRestoresThreeSecondsHelicopterProfile()
        => WorkspaceViewTests.OnSta(() =>
        {
            var preferences = new AppConfig().Preferences with
            {
                FlightDurationSeconds = 8.5,
                FlightCruisingScale = 2.2,
                FlightProfile = "Cinematic"
            };
            var control = new FlightSettingsControl(preferences);

            Assert.Equal(8.5, control.SelectedDuration, precision: 2);
            Assert.Equal(2.2, control.SelectedCruisingScale, precision: 2);
            Assert.Equal("Cinematic", control.SelectedProfile);

            control.ResetDefaults();

            Assert.Equal(3.0, control.SelectedDuration, precision: 2);
            Assert.Equal(1.0, control.SelectedCruisingScale, precision: 2);
            Assert.Equal("Helicopter", control.SelectedProfile);
        });

    [Theory]
    [InlineData("Save settings", 5.5, 1.8, true)]
    [InlineData("Cancel", 7.0, 2.0, false)]
    public void SettingsWindowRoundTripsFlightSettingsOnSave(
        string action, double targetDuration, double targetScale, bool expectSaved)
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "canvas-flight-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var config = new AppConfig(directory: directory);
                config.Load();
                var original = config.Preferences;

                int appliedCount = 0;
                var window = new SettingsWindow(config, () => appliedCount++, zoomOnly: false);

                var durationSlider = WorkspaceViewTests.Descendants(window).OfType<Slider>()
                    .Single(s => AutomationProperties.GetName(s) == "Flight duration in seconds");
                var scaleSlider = WorkspaceViewTests.Descendants(window).OfType<Slider>()
                    .Single(s => AutomationProperties.GetName(s) == "Cruising altitude zoom depth multiplier");

                durationSlider.Value = targetDuration;
                scaleSlider.Value = targetScale;

                WorkspaceViewTests.Descendants(window).OfType<Button>()
                    .Single(b => Equals(b.Content, action))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                using var reloaded = new AppConfig(directory: directory);
                reloaded.Load();

                if (expectSaved)
                {
                    Assert.Equal(1, appliedCount);
                    Assert.Equal(targetDuration, reloaded.FlightDurationSeconds, precision: 2);
                    Assert.Equal(targetScale, reloaded.FlightCruisingScale, precision: 2);
                    Assert.Equal(targetDuration, reloaded.Preferences.FlightDurationSeconds, precision: 2);
                    Assert.Equal(targetScale, reloaded.Preferences.FlightCruisingScale, precision: 2);
                }
                else
                {
                    Assert.Equal(0, appliedCount);
                    Assert.Equal(original.FlightDurationSeconds, reloaded.FlightDurationSeconds, precision: 2);
                    Assert.Equal(original.FlightCruisingScale, reloaded.FlightCruisingScale, precision: 2);
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        });

    [Fact]
    public void KinematicProfilesProduceDistinctValidTrajectories()
    {
        var from = new CanvasCamera(0, 0, 1.0);
        var to = new CanvasCamera(5000, 3000, 1.0);

        var heli = new WindowNavigationFlight(from, to, duration: 3.0, cruisingScale: 1.0, profile: "Helicopter");
        var brisk = new WindowNavigationFlight(from, to, duration: 3.0, cruisingScale: 1.0, profile: "Brisk");
        var cinema = new WindowNavigationFlight(from, to, duration: 5.0, cruisingScale: 1.5, profile: "Cinematic");
        var planar = new WindowNavigationFlight(from, to, duration: 3.0, cruisingScale: 1.0, profile: "Planar");

        // Endpoints exact
        Assert.Equal(from, heli.Sample(0));
        Assert.Equal(to, heli.Sample(3.0));
        Assert.Equal(from, brisk.Sample(0));
        Assert.Equal(to, brisk.Sample(3.0));
        Assert.Equal(from, cinema.Sample(0));
        Assert.Equal(to, cinema.Sample(5.0));
        Assert.Equal(from, planar.Sample(0));
        Assert.Equal(to, planar.Sample(3.0));

        // Planar has no altitude climb
        var planarMid = planar.Sample(1.5);
        Assert.Equal(1.0, planarMid.Zoom, precision: 3);

        // Brisk starts lateral flight faster than Helicopter at early time u = 0.15
        var heliEarly = heli.SampleFocalPoint(3.0 * 0.15);
        var briskEarly = brisk.SampleFocalPoint(3.0 * 0.15);
        double heliEarlyFrac = (heliEarly.X - heli.FromFocalPoint.X) / (heli.ToFocalPoint.X - heli.FromFocalPoint.X);
        double briskEarlyFrac = (briskEarly.X - brisk.FromFocalPoint.X) / (brisk.ToFocalPoint.X - brisk.FromFocalPoint.X);
        Assert.True(briskEarlyFrac > heliEarlyFrac, "Brisk profile should advance faster laterally during initial phase");

        // Cinematic cruising zoom reaches higher altitude (lower zoom number) than standard Helicopter
        Assert.True(cinema.CruisingZoom < heli.CruisingZoom, "Cinematic with 1.5x cruising scale should zoom out higher than Helicopter");
    }
}
