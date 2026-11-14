using System;
using System.IO;
using System.Linq;
using System.Windows;
using MenuItem = System.Windows.Controls.MenuItem;
using System.Windows.Controls.Primitives;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class UnifiedDockTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void UnifiedDockPresentsInArrangeModeWithCreationToolsVisible()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-arrange-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                var desktops = new FakeVirtualDesktops();
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                Assert.NotNull(overview.UnifiedDock);
                Assert.True(overview.UnifiedDock.IsDockVisible);

                var panel = overview.UnifiedDock.Panel;

                // Arrange is active by default
                Assert.False(overview.InteractMode);
                Assert.False(panel.IsInteractMode);
                Assert.Equal(Visibility.Visible, panel.CreationToolsPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, panel.InteractHudPanel.Visibility);
                Assert.Equal(AnnotationMode.Select, panel.CurrentAnnotationMode);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void UnifiedDockCollapsesCreationToolsInInteractModeAndRestoresOnArrangeOrEscape()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-collapse-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                var desktops = new FakeVirtualDesktops();
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                var panel = overview.UnifiedDock!.Panel;

                // 1. Switch to Interact mode via dock pill button
                panel.InteractModeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(overview.InteractMode);
                Assert.True(panel.IsInteractMode);

                // Creation tools must FULLY COLLAPSE in Interact mode
                Assert.Equal(Visibility.Collapsed, panel.CreationToolsPanel.Visibility);
                Assert.Equal(Visibility.Visible, panel.InteractHudPanel.Visibility);

                // 2. Escape returns to Arrange mode
                overview.HandleEscape();
                Assert.False(overview.InteractMode);
                Assert.False(panel.IsInteractMode);

                // Creation tools must be restored
                Assert.Equal(Visibility.Visible, panel.CreationToolsPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, panel.InteractHudPanel.Visibility);

                // 3. Switch back to Interact via programmatic method
                overview.SetInteractMode(true);
                Assert.True(panel.IsInteractMode);
                Assert.Equal(Visibility.Collapsed, panel.CreationToolsPanel.Visibility);
                Assert.Equal(Visibility.Visible, panel.InteractHudPanel.Visibility);

                // 4. Clicking Arrange mode button returns to Arrange mode
                panel.ArrangeModeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.False(overview.InteractMode);
                Assert.False(panel.IsInteractMode);
                Assert.Equal(Visibility.Visible, panel.CreationToolsPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, panel.InteractHudPanel.Visibility);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ContextualPropertiesShelfAppearsForShapesAndCollapsesInInteractMode()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-properties-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                var desktops = new FakeVirtualDesktops();
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                var panel = overview.UnifiedDock!.Panel;

                // Properties shelf is hidden when Select mode has no selection
                Assert.Equal(Visibility.Collapsed, panel.PropertiesShelf.Visibility);

                // Selecting a drawing mode shows contextual properties shelf
                overview.SelectAnnotationMode(AnnotationMode.Rectangle);
                Assert.Equal(Visibility.Visible, panel.PropertiesShelf.Visibility);

                // Switching to Interact mode collapses properties shelf even if drawing mode was selected
                panel.InteractModeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(Visibility.Collapsed, panel.PropertiesShelf.Visibility);

                // Returning to Arrange mode in Select without selection keeps properties shelf hidden
                overview.HandleEscape();
                Assert.Equal(Visibility.Collapsed, panel.PropertiesShelf.Visibility);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    public void StandaloneUnifiedDockControlStateAndEvents()
        => WorkspaceViewTests.OnSta(() =>
        {
            var dock = new UnifiedDockControl();
            bool? modeResult = null;
            dock.InteractionModeRequested += mode => modeResult = mode;

            // Click Interact mode pill
            dock.InteractModeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(modeResult);
            Assert.True(dock.IsInteractMode);
            Assert.Equal(Visibility.Collapsed, dock.CreationToolsPanel.Visibility);
            Assert.Equal(Visibility.Visible, dock.InteractHudPanel.Visibility);

            // Click Arrange mode pill
            dock.ArrangeModeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(modeResult);
            Assert.False(dock.IsInteractMode);
            Assert.Equal(Visibility.Visible, dock.CreationToolsPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, dock.InteractHudPanel.Visibility);

            // Tool state updates
            dock.UpdateState(new AnnotationToolbarState(AnnotationMode.Select, CanUndo: true, CanRedo: true));
            bool undoFired = false;
            dock.UndoRequested += () => undoFired = true;
            dock.UpdateState(new AnnotationToolbarState(AnnotationMode.Select, CanUndo: true));
            Assert.False(undoFired);
        });

    [Fact]
    public void StandaloneUnifiedDockControl_AiTeamsToggledAndState()
        => WorkspaceViewTests.OnSta(() =>
        {
            var dock = new UnifiedDockControl();
            bool toggled = false;
            dock.AiTeamsToggled += () => toggled = true;

            Assert.False(dock.IsAiTeamsActive);
            dock.SetAiTeamsActive(true);
            Assert.True(dock.IsAiTeamsActive);

            var menu = dock.OpenMoreMenu();
            Assert.NotNull(menu);
            var aiTeamsItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => (string)i.Header == "AI Teams Mission Control");
            Assert.NotNull(aiTeamsItem);
            Assert.True(aiTeamsItem.IsCheckable);
            Assert.True(aiTeamsItem.IsChecked);

            aiTeamsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(toggled);

            dock.SetAiTeamsActive(false);
            Assert.False(dock.IsAiTeamsActive);
            var menu2 = dock.OpenMoreMenu();
            var aiTeamsItem2 = menu2!.Items.OfType<MenuItem>().First(i => (string)i.Header == "AI Teams Mission Control");
            Assert.False(aiTeamsItem2.IsChecked);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void UnifiedDock_AiTeamsToggle_SpawnsSectorPod()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-aiteams-" + Guid.NewGuid().ToString("N"));
            try
            {
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var rig = new WorkspaceHarness(config: config); rig.Workspace.Start();
                using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config);
                overview.AttachZones(rig.Workspace.Zones);
                overview.AttachPlaceholderWorkspace(rig.Workspace);
                overview.AttachAnnotations(rig.Desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                Assert.NotNull(overview.UnifiedDock);
                var panel = overview.UnifiedDock.Panel;
                Assert.False(panel.IsAiTeamsActive);
                Assert.False(overview.IsAiTeamsActive);

                var menu = panel.OpenMoreMenu();
                Assert.NotNull(menu);
                var aiTeamsItem = menu.Items.OfType<MenuItem>().First(i => (string)i.Header == "AI Teams Mission Control");
                aiTeamsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.True(overview.IsAiTeamsActive);
                Assert.True(panel.IsAiTeamsActive);
                Assert.NotNull(overview.SectorManager);
                Assert.NotEmpty(overview.SectorManager.SpawnedPods);

                var menu2 = panel.OpenMoreMenu();
                var aiTeamsItem2 = menu2!.Items.OfType<MenuItem>().First(i => (string)i.Header == "AI Teams Mission Control");
                aiTeamsItem2.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.False(overview.IsAiTeamsActive);
                Assert.False(panel.IsAiTeamsActive);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
}