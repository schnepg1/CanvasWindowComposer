using System.Windows;
using System.Windows.Controls;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

[CollectionDefinition("ShellViews", DisableParallelization = true)]
public class ShellViewCollection;

[Collection("ShellViews")]
public class SettingsWindowTests
{
    [Theory]
    [InlineData(true, "Save settings")]
    [InlineData(false, "Save settings")]
    [InlineData(false, "Cancel")]
    public void ShortcutZoomOutToggleKeepsItsAmountAndOnlySavesOnRequest(bool enabled, string action)
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "canvas-shortcut-zoom-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var config = new AppConfig(directory: directory); config.Load();
                var original = config.Preferences;
                var window = new SettingsWindow(config, () => { }, zoomOnly: true);
                var amount = WorkspaceViewTests.Descendants(window).OfType<Slider>().First();
                var toggle = WorkspaceViewTests.Descendants(window).OfType<CheckBox>()
                    .Single(b => Equals(b.Content, "Zoom out on Alt+Q"));
                amount.Value = 37;
                toggle.IsChecked = false; Assert.False(amount.IsEnabled); Assert.Equal(37, amount.Value);
                toggle.IsChecked = true; Assert.True(amount.IsEnabled); Assert.Equal(37, amount.Value);
                toggle.IsChecked = enabled;
                Assert.Equal(original, config.Preferences);
                WorkspaceViewTests.Descendants(window).OfType<Button>().Single(b => Equals(b.Content, action))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var expected = action == "Save settings"
                    ? original with { ShortcutZoomOut = enabled, ShortcutZoomOutAmount = .37 } : original;
                using var reopened = new AppConfig(directory: directory); reopened.Load();
                Assert.Equal(expected, reopened.Preferences);
                Assert.False(reopened.DisableZoomHotkey);
            }
            finally { Directory.Delete(directory, true); }
        });

    [Theory]
    [InlineData(true, "Save settings")]
    [InlineData(false, "Save settings")]
    [InlineData(true, "Cancel")]
    [InlineData(false, "Cancel")]
    [InlineData(true, "Escape")]
    [InlineData(false, "Escape")]
    [Trait("Category", "NativeRendering")]
    public void ClosingSettingsDuringDeactivationDoesNotReenterClose(bool zoomOnly, string action)
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "canvas-settings-close-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var config = new AppConfig(directory: directory); config.Load();
                var original = config.Preferences;
                var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                overview.TransitionTo(OverviewMode.Zooming);
                overview.ShowSettings(zoomOnly);
                var popup = overview.SettingsPopup!;
                var sliders = WorkspaceViewTests.Descendants(popup).OfType<Slider>().Skip(1).ToArray();
                sliders[0].Value = 35; sliders[1].Value = 43;
                sliders[2].Value = 8; sliders[3].Value = 12;
                int closings = 0;
                popup.Closing += (_, _) =>
                {
                    closings++;
                    // Force the native close/deactivate ordering even without desktop focus.
                    typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .Invoke(popup, [EventArgs.Empty]);
                };
                if (action == "Escape") overview.HandleEscape();
                else WorkspaceViewTests.Descendants(popup).OfType<Button>().Single(b => Equals(b.Content, action))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, closings);
                Assert.Null(overview.SettingsPopup);
                Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
                Assert.False(overview.CanvasInputSuspended);
                if (action == "Save settings")
                {
                    using var reopened = new AppConfig(directory: directory); reopened.Load();
                    Assert.Equal(.35, reopened.OverviewCardEnterZoom);
                    Assert.Equal(.43, reopened.OverviewCardExitZoom);
                    Assert.Equal(.08, reopened.OverviewSimpleCardEnterZoom);
                    Assert.Equal(.12, reopened.OverviewSimpleCardExitZoom);
                }
                else Assert.Equal(original, config.Preferences);
            }
            finally { Directory.Delete(directory, true); }
        });

    [Fact]
    public void ZoomPopupSavesPercentagesAndPreservesOtherSettingsChangedWhileOpen()
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "canvas-zoom-save-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var config = new AppConfig(directory: directory); config.Load();
                int applied = 0;
                var window = new SettingsWindow(config, () => applied++, zoomOnly: true);
                WorkspaceViewTests.Render(window, "zoom-settings-popup-preview.png");
                window.Width = 360; window.Height = 420;
                WorkspaceViewTests.Render(window, "zoom-settings-popup-small-150-preview.png", 1.5);
                var sliders = WorkspaceViewTests.Descendants(window).OfType<Slider>().Skip(1).ToArray();
                Assert.Equal(4, sliders.Length);
                sliders[0].Value = 55; sliders[1].Value = 65;
                sliders[2].Value = 15; sliders[3].Value = 22;
                config.SavePreferences(config.Preferences with { LowQualityMode = true, Search = false,
                    DesktopPanMomentum = false, CanvasPanMomentum = false });
                WorkspaceViewTests.Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Save settings"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, applied);
                using var reloaded = new AppConfig(directory: directory); reloaded.Load();
                Assert.Equal(0.55, reloaded.OverviewCardEnterZoom);
                Assert.Equal(0.65, reloaded.OverviewCardExitZoom);
                Assert.Equal(0.15, reloaded.OverviewSimpleCardEnterZoom);
                Assert.Equal(0.22, reloaded.OverviewSimpleCardExitZoom);
                Assert.True(reloaded.LowQualityMode);
                Assert.True(reloaded.DisableSearch);
                Assert.False(reloaded.DesktopPanMomentum);
                Assert.False(reloaded.CanvasPanMomentum);
                Assert.False(reloaded.EnableAutomaticMinimization);
            }
            finally { Directory.Delete(directory, true); }
        });

    [Fact]
    public void InvalidThresholdOrderDoesNotSaveAndResetRemainsDetachedUntilSave()
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "canvas-zoom-validation-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var config = new AppConfig(directory: directory); config.Load();
                var before = File.ReadAllText(Path.Combine(directory, "config.ini"));
                int applied = 0;
                var window = new SettingsWindow(config, () => applied++, zoomOnly: true);
                var sliders = WorkspaceViewTests.Descendants(window).OfType<Slider>().Skip(1).ToArray();
                sliders[0].Value = 80;
                WorkspaceViewTests.Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Save settings"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, applied);
                Assert.Contains(WorkspaceViewTests.Descendants(window).OfType<TextBlock>(), b => b.Text.Contains("must be higher"));
                WorkspaceViewTests.Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Reset zoom defaults"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(new double[] { 40, 45, 10, 13 }, sliders.Select(s => s.Value));
                Assert.Equal(15, WorkspaceViewTests.Descendants(window).OfType<Slider>().First().Value);
                window.Close();
                Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "config.ini")));
            }
            finally { Directory.Delete(directory, true); }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void SettingsPopupIsOwnedAboveCanvasAndEscapeAndHideCloseOnlyThePopup()
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "canvas-settings-owner-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var config = new AppConfig(directory: directory); config.Load();
                var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                overview.TransitionTo(OverviewMode.Zooming);
                var pass = Assert.IsType<OverviewOverlay>(System.Windows.Forms.Control.FromHandle(overview.MonitorHandles[0]));
                var controls = pass.ZoomControls!;
                Assert.True(controls.Visible);
                var zoomButton = controls.Controls.OfType<System.Windows.Forms.Button>().Single(b => b.AccessibleName == "Zoom settings");
                var settingsButton = controls.Controls.OfType<System.Windows.Forms.Button>().Single(b => b.AccessibleName == "Canvas settings");
                Assert.True(settingsButton.Left < zoomButton.Left);
                zoomButton.PerformClick();
                var popup = overview.SettingsPopup!;
                Assert.True(popup.IsVisible); Assert.True(popup.Topmost);
                Assert.Contains(new System.Windows.Interop.WindowInteropHelper(popup).Owner, overview.MonitorHandles);
                Assert.True(overview.CanvasInputSuspended);
                overview.HandleEscape();
                Assert.Null(overview.SettingsPopup);
                Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
                Assert.False(overview.CanvasInputSuspended);
                settingsButton.PerformClick();
                Assert.False(overview.SettingsPopup!.ZoomOnly);
                overview.TransitionTo(OverviewMode.Hidden);
                Assert.Null(overview.SettingsPopup);
            }
            finally { Directory.Delete(directory, true); }
        });

    [Theory]
    [InlineData("Save settings", false, true)]
    [InlineData("Save settings", true, false)]
    [InlineData("Cancel", false, false)]
    public void PanMomentumCheckboxesAreIndependentAndCancelKeepsRunningPreferences(string action, bool desktop, bool canvas)
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "canvas-momentum-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var config = new AppConfig(directory: directory); config.Load();
                int applied = 0;
                var original = config.Preferences;
                var window = new SettingsWindow(config, () => applied++);
                var boxes = WorkspaceViewTests.Descendants(window).OfType<CheckBox>().ToArray();
                boxes.Single(b => (b.Content as TextBlock)?.Text == "Desktop pan momentum").IsChecked = desktop;
                boxes.Single(b => (b.Content as TextBlock)?.Text == "Canvas pan momentum").IsChecked = canvas;
                Assert.Equal(original, config.Preferences);
                WorkspaceViewTests.Descendants(window).OfType<Button>().Single(b => Equals(b.Content, action))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var expected = action == "Save settings"
                    ? original with { DesktopPanMomentum = desktop, CanvasPanMomentum = canvas } : original;
                Assert.Equal(action == "Save settings" ? 1 : 0, applied);
                Assert.Equal(expected, config.Preferences);
                using var reopened = new AppConfig(directory: directory); reopened.Load();
                Assert.Equal(expected, reopened.Preferences);
                window.Close();
            }
            finally { Directory.Delete(directory, true); }
        });

    [Fact]
    public void OpeningEditingAndClosingSettingsDoesNotEnableExperimentalOptions()
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "canvas-settings-view-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var config = new AppConfig(directory: directory); config.Load();
                var before = File.ReadAllText(Path.Combine(directory, "config.ini"));
                var window = new SettingsWindow(config, () => throw new Exception("Cancel must not apply"));
                foreach (var box in WorkspaceViewTests.Descendants(window).OfType<CheckBox>()) box.IsChecked = true;
                window.Close();
                Assert.False(config.EnableAutomaticMinimization); Assert.False(config.EnableFlipRendering);
                Assert.False(config.LowQualityMode);
                Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "config.ini")));
            }
            finally { Directory.Delete(directory, true); }
        });

    [Fact]
    public void SavingSettingsUsesTheExistingConfigAndRendersAtDifferentSizes()
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "canvas-settings-save-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var config = new AppConfig(directory: directory); config.Load();
                int applied = 0;
                var window = new SettingsWindow(config, () => applied++);
                WorkspaceViewTests.Render(window, "settings-shell-preview.png");
                window.Width = 440; window.Height = 420;
                WorkspaceViewTests.Render(window, "settings-shell-small-150-preview.png", 1.5);
                // Retain these owned-window renders with the CI results for visual review.
                string renders = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../..", "TestResults", "PanMomentum"));
                Directory.CreateDirectory(renders);
                foreach (var name in new[] { "settings-shell-preview.png", "settings-shell-small-150-preview.png" })
                    File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(renders, name), true);
                var boxes = WorkspaceViewTests.Descendants(window).OfType<CheckBox>().ToArray();
                Assert.Equal(19, boxes.Length);
                boxes[0].IsChecked = false;
                boxes.Single(b => (b.Content as TextBlock)?.Text == "Animate navigation to apps selected with Alt+Tab or the taskbar").IsChecked = false;
                boxes.Single(b => (b.Content as TextBlock)?.Text == "Show Windows taskbar in canvas view").IsChecked = false;
                boxes.Single(b => (b.Content as TextBlock)?.Text == "Low quality mode").IsChecked = true;
                WorkspaceViewTests.Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Save settings"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, applied); Assert.True(config.DisableSearch);
                Assert.True(config.LowQualityMode);
                Assert.False(config.ShowTaskbarInCanvas);
                Assert.False(config.AnimateWindowNavigation);
                Assert.False(config.EnableAutomaticMinimization); Assert.False(config.EnableFlipRendering);
                window.Close();
            }
            finally { Directory.Delete(directory, true); }
        });
}
