using System.Windows;
using System.Windows.Controls;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

[CollectionDefinition("ShellViews", DisableParallelization = true)]
public class ShellViewCollection;

[Collection("ShellViews")]
public class SettingsWindowTests
{
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
                var boxes = WorkspaceViewTests.Descendants(window).OfType<CheckBox>().ToArray();
                Assert.Equal(8, boxes.Length);
                boxes[0].IsChecked = false;
                WorkspaceViewTests.Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Save settings"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, applied); Assert.True(config.DisableSearch);
                Assert.False(config.EnableAutomaticMinimization); Assert.False(config.EnableFlipRendering);
                window.Close();
            }
            finally { Directory.Delete(directory, true); }
        });
}
