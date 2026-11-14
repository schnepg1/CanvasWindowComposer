using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class ShortcutFlightOverviewTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void AltQFromDesktopLiftsOffWithCameraFlight()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas();
            canvas.SetCamera(500, -200);
            var api = new FakeWindowApi();
            var input = new FakeInputRouter();
            var config = new FakeAppConfig
            {
                EnableStartupIntro = false,
                EnableShortcutZoomOut = true,
                ShortcutZoomOutAmount = 0.20,
                EnableShortcutFlight = true,
                ShortcutFlightDuration = 1.0,
                AnimateWindowNavigation = true
            };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);

            // Alt+Q from Desktop
            input.RaiseOverviewHotkey();

            // Mode is Zooming and flight is active
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Assert.True(overview.WindowNavigationActive);

            // Started at Zoom 1.0
            Assert.Equal(1.0, overview.ViewCamera.Zoom, 2);

            // Advance halfway
            overview.AdvanceWindowNavigation(0.5);
            Assert.True(overview.WindowNavigationActive);
            Assert.True(overview.ViewCamera.Zoom < 1.0);
            Assert.True(overview.ViewCamera.Zoom > 0.80);

            // Advance to completion
            overview.AdvanceWindowNavigation(1.0);
            Assert.False(overview.WindowNavigationActive);
            Assert.Equal(0.80, overview.ViewCamera.Zoom, 3);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void AltQFromCanvasLandsWithCameraFlightAndClosesToDesktop()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas();
            canvas.SetCamera(1000, 2000);
            var api = new FakeWindowApi();
            var input = new FakeInputRouter();
            var config = new FakeAppConfig
            {
                EnableStartupIntro = false,
                EnableShortcutZoomOut = true,
                ShortcutZoomOutAmount = 0.30,
                EnableShortcutFlight = true,
                ShortcutFlightDuration = 1.2,
                AnimateWindowNavigation = true
            };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);

            // Open overview and complete liftoff
            input.RaiseOverviewHotkey();
            overview.AdvanceWindowNavigation(1.2);
            Assert.False(overview.WindowNavigationActive);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);

            // Pan canvas view somewhere else
            overview.Camera.PanByVirtual(500, 300);
            var (expectedDesktopX, expectedDesktopY) = overview.Camera.ViewportCamera;
            double canvasZoomBeforeLanding = overview.ViewCamera.Zoom;

            // Alt+Q from Canvas view should start landing
            input.RaiseOverviewHotkey();
            Assert.True(overview.WindowNavigationActive);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);

            // Advance halfway through landing
            overview.AdvanceWindowNavigation(0.6);
            Assert.True(overview.WindowNavigationActive);
            Assert.True(overview.ViewCamera.Zoom > canvasZoomBeforeLanding);
            Assert.True(overview.ViewCamera.Zoom < 1.0);

            // Complete landing
            overview.AdvanceWindowNavigation(1.2);
            Assert.False(overview.WindowNavigationActive);
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);

            // Verify desktop camera matches the landing focal position
            Assert.Equal(expectedDesktopX, canvas.Camera.X, 3);
            Assert.Equal(expectedDesktopY, canvas.Camera.Y, 3);
            Assert.Equal(1.0, canvas.Camera.Zoom);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void AltQDuringLandingReversesFlightBackToLiftoff()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas();
            var api = new FakeWindowApi();
            var input = new FakeInputRouter();
            var config = new FakeAppConfig
            {
                EnableStartupIntro = false,
                EnableShortcutZoomOut = true,
                ShortcutZoomOutAmount = 0.25,
                EnableShortcutFlight = true,
                ShortcutFlightDuration = 1.0,
                AnimateWindowNavigation = true
            };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);

            // Open and settle
            input.RaiseOverviewHotkey();
            overview.AdvanceWindowNavigation(1.0);
            Assert.Equal(0.75, overview.ViewCamera.Zoom, 3);

            // Press Alt+Q to start landing
            input.RaiseOverviewHotkey();
            overview.AdvanceWindowNavigation(0.5);
            Assert.True(overview.WindowNavigationActive);
            Assert.True(overview.ViewCamera.Zoom > 0.75);

            // Press Alt+Q again to reverse landing
            input.RaiseOverviewHotkey();
            Assert.True(overview.WindowNavigationActive);

            // Advance reversed flight to completion
            overview.AdvanceWindowNavigation(1.0);
            Assert.False(overview.WindowNavigationActive);
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Assert.Equal(0.75, overview.ViewCamera.Zoom, 3);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void DisabledShortcutFlightJumpsImmediately()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas();
            var api = new FakeWindowApi();
            var input = new FakeInputRouter();
            var config = new FakeAppConfig
            {
                EnableStartupIntro = false,
                EnableShortcutZoomOut = true,
                ShortcutZoomOutAmount = 0.20,
                EnableShortcutFlight = false, // Disabled!
                AnimateWindowNavigation = true
            };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);

            // Press Alt+Q: should jump immediately into zooming
            input.RaiseOverviewHotkey();
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Assert.False(overview.WindowNavigationActive);
            Assert.Equal(0.80, overview.ViewCamera.Zoom, 3);

            // Press Alt+Q again: should jump immediately to hidden
            input.RaiseOverviewHotkey();
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
            Assert.False(overview.WindowNavigationActive);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ReducedMotionOrDisabledNavigationCompletesFlightImmediately()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas();
            var api = new FakeWindowApi();
            var input = new FakeInputRouter();
            var config = new FakeAppConfig
            {
                EnableStartupIntro = false,
                EnableShortcutZoomOut = true,
                ShortcutZoomOutAmount = 0.20,
                EnableShortcutFlight = true,
                AnimateWindowNavigation = false // Animation disabled
            };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);

            // Press Alt+Q: immediate liftoff without timer
            input.RaiseOverviewHotkey();
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Assert.False(overview.WindowNavigationActive);
            Assert.Equal(0.80, overview.ViewCamera.Zoom, 3);

            // Press Alt+Q: immediate landing without timer
            input.RaiseOverviewHotkey();
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
            Assert.False(overview.WindowNavigationActive);
        });

    [Fact]
    public void ZoomSettingsControlConfiguresAndSavesShortcutFlightPreferences()
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "canvas-shortcut-flight-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var config = new AppConfig(directory: directory);
                config.Load();
                var original = config.Preferences;

                var window = new SettingsWindow(config, () => { }, zoomOnly: true);
                var toggle = WorkspaceViewTests.Descendants(window).OfType<CheckBox>()
                    .Single(b => Equals(b.Content, "Camera flight on Alt+Q"));
                var durationSlider = WorkspaceViewTests.Descendants(window).OfType<Slider>()
                    .Single(s => System.Windows.Automation.AutomationProperties.GetName(s) == "Camera flight duration (seconds)");

                Assert.True(toggle.IsChecked);
                Assert.True(durationSlider.IsEnabled);

                // Change flight toggle and duration
                toggle.IsChecked = false;
                Assert.False(durationSlider.IsEnabled);
                toggle.IsChecked = true;
                Assert.True(durationSlider.IsEnabled);
                durationSlider.Value = 2.4;

                // Save
                WorkspaceViewTests.Descendants(window).OfType<Button>()
                    .Single(b => Equals(b.Content, "Save settings"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                using var reloaded = new AppConfig(directory: directory);
                reloaded.Load();
                Assert.True(reloaded.EnableShortcutFlight);
                Assert.Equal(2.4, reloaded.ShortcutFlightDuration, 2);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });
}
