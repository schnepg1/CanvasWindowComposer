using System;
using System.IO;
using System.Linq;
using System.Windows;
using MenuItem = System.Windows.Controls.MenuItem;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
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
            dock.UpdateState(new CanvasDockState(AnnotationMode.Select, CanUndo: true, CanRedo: true));
            bool undoFired = false;
            dock.UndoRequested += () => undoFired = true;
            dock.UpdateState(new CanvasDockState(AnnotationMode.Select, CanUndo: true));
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
    public void UnifiedDock_AiTeamsToggle_PreservesSavedMissions()
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
                var savedMission = new TeamPodService(rig.Workspace, overview.Annotations!)
                    .CreateMission(rig.Desktops.CurrentDesktopId, "Saved mission", new(0, 0));
                Assert.NotNull(savedMission);
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
                var missions = rig.Workspace.Document.Zones.Where(z => z.IsMission).Select(z => z.Id).ToArray();
                Assert.Contains(savedMission.Value, missions);

                var menu2 = panel.OpenMoreMenu();
                var aiTeamsItem2 = menu2!.Items.OfType<MenuItem>().First(i => (string)i.Header == "AI Teams Mission Control");
                aiTeamsItem2.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.False(overview.IsAiTeamsActive);
                Assert.False(panel.IsAiTeamsActive);
                Assert.Equal(missions, rig.Workspace.Document.Zones.Where(z => z.IsMission).Select(z => z.Id).ToArray());
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void UnifiedDock_MoreMenuProvidesCutCopyPasteActionsAndRespectsState()
        => WorkspaceViewTests.OnSta(() =>
        {
            var dock = new UnifiedDockControl();
            int cut = 0, copy = 0, paste = 0;
            dock.CutRequested += () => cut++;
            dock.CopyRequested += () => copy++;
            dock.PasteRequested += () => paste++;

            // Without selection
            dock.UpdateState(new CanvasDockState(AnnotationMode.Select, CanCopy: false, CanCut: false, CanPaste: true));
            var menu1 = dock.OpenMoreMenu();
            Assert.NotNull(menu1);

            var cutItem1 = menu1.Items.OfType<MenuItem>().First(i => (string)i.Header == "Cut annotation");
            var copyItem1 = menu1.Items.OfType<MenuItem>().First(i => (string)i.Header == "Copy annotation");
            var pasteItem1 = menu1.Items.OfType<MenuItem>().First(i => (string)i.Header == "Paste annotation");

            Assert.Equal("Ctrl+X", cutItem1.InputGestureText);
            Assert.Equal("Ctrl+C", copyItem1.InputGestureText);
            Assert.Equal("Ctrl+V", pasteItem1.InputGestureText);

            Assert.False(cutItem1.IsEnabled);
            Assert.False(copyItem1.IsEnabled);
            Assert.True(pasteItem1.IsEnabled);

            // With selection
            dock.UpdateState(new CanvasDockState(AnnotationMode.Select, CanCopy: true, CanCut: true, CanPaste: true));
            var menu2 = dock.OpenMoreMenu();
            Assert.NotNull(menu2);

            var cutItem2 = menu2.Items.OfType<MenuItem>().First(i => (string)i.Header == "Cut annotation");
            var copyItem2 = menu2.Items.OfType<MenuItem>().First(i => (string)i.Header == "Copy annotation");
            var pasteItem2 = menu2.Items.OfType<MenuItem>().First(i => (string)i.Header == "Paste annotation");

            Assert.True(cutItem2.IsEnabled);
            Assert.True(copyItem2.IsEnabled);
            Assert.True(pasteItem2.IsEnabled);

            cutItem2.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            copyItem2.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            pasteItem2.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal(1, cut);
            Assert.Equal(1, copy);
            Assert.Equal(1, paste);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void SingleDockLayerReplacesTheLegacyToolbar()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-single-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                overview.AttachAnnotations(new FakeVirtualDesktops(), Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                Assert.True(overview.LayerCoordinator.IsRegistered("UnifiedDock"));
                Assert.False(overview.LayerCoordinator.IsRegistered("AnnotationToolbar"));
                Assert.NotNull(overview.UnifiedDock);
                Assert.True(overview.UnifiedDock!.IsDockVisible);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void DockFollowsTaskbarSettingImmediatelyAndAcrossCanvasModeChanges()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-taskbar-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                overview.AttachAnnotations(new FakeVirtualDesktops(), Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                void Check(bool taskbarVisible)
                {
                    var screen = System.Windows.Forms.Screen.PrimaryScreen!;
                    var area = taskbarVisible ? screen.WorkingArea : screen.Bounds;
                    var dock = overview.UnifiedDock!;
                    Assert.True(dock.IsDockVisible);

                    // Assert against the real HWND rect, not the cached layout, and derive the
                    // floating margin from the monitor DPI instead of hard-coding physical pixels.
                    var handle = new WindowInteropHelper(dock).EnsureHandle();
                    Assert.True(PInvoke.GetWindowRect((HWND)handle, out var rect), "GetWindowRect failed");
                    int margin = (int)Math.Round(10 * VisualTreeHelper.GetDpi(dock).DpiScaleY);
                    Assert.InRange(rect.bottom, area.Bottom - margin - 1, area.Bottom - margin + 1);
                    int dockCenter = (rect.left + rect.right) / 2;
                    int areaCenter = area.Left + area.Width / 2;
                    Assert.InRange(Math.Abs(dockCenter - areaCenter), 0, 4);
                }

                Check(true);
                config.ShowTaskbarInCanvas = false; overview.RefreshConfig(); Check(false);
                overview.TransitionTo(OverviewMode.Panning);
                overview.TransitionTo(OverviewMode.Zooming); Check(false);
                config.ShowTaskbarInCanvas = true; overview.RefreshConfig(); Check(true);
                overview.TransitionTo(OverviewMode.Hidden);
                overview.TransitionTo(OverviewMode.Zooming); Check(true);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void DockStaysOnTopAcrossRaiseCanvasPanelsAndComposerWaitCycle()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-raise-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                overview.AttachAnnotations(new FakeVirtualDesktops(), Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                var dock = overview.UnifiedDock!;
                var dockHandle = new WindowInteropHelper(dock).EnsureHandle();
                Assert.NotEqual(IntPtr.Zero, dockHandle);

                void Invoke(string name, params object?[] arguments) =>
                    typeof(OverviewManager).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(overview, arguments);

                // The legacy strip used to be re-shown from these refresh paths, which is exactly
                // what made it flicker through the dock. Every cycle must leave the dock as the
                // only toolbelt and above every canvas overlay.
                void AssertDockIsTheOnlyToolbeltAndOnTop()
                {
                    Assert.True(dock.IsDockVisible);
                    Assert.True(overview.LayerCoordinator.IsRegistered("UnifiedDock"));
                    Assert.False(overview.LayerCoordinator.IsRegistered("AnnotationToolbar"));
                    foreach (var pass in overview.MonitorHandles) AssertAbove(dockHandle, pass);
                }

                Invoke("RaiseCanvasPanels");
                AssertDockIsTheOnlyToolbeltAndOnTop();

                // The composer browser hand-off flips the overview topmost state on and off.
                Invoke("SetComposerWaitingForBrowser", true);
                Assert.True(dock.IsAlive);
                Invoke("SetComposerWaitingForBrowser", false);
                Invoke("RaiseCanvasPanels");
                AssertDockIsTheOnlyToolbeltAndOnTop();

                // A settings refresh re-runs the annotation UI refresh that used to resurrect it.
                overview.RefreshConfig();
                Invoke("RaiseCanvasPanels");
                AssertDockIsTheOnlyToolbeltAndOnTop();
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void OpenDockMenuBlocksPeekAndClosesOnEscape()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-menu-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                overview.AttachAnnotations(new FakeVirtualDesktops(), Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                var panel = overview.UnifiedDock!.Panel;
                Assert.False(panel.HasOpenDropDown);

                var menu = panel.OpenMoreMenu();
                Assert.NotNull(menu);
                Assert.True(panel.HasOpenDropDown);

                overview.HandleEscape();

                Assert.False(panel.HasOpenDropDown);
                Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void DockForwardsKeyboardShortcutsToTheCanvas()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-keys-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                overview.AttachAnnotations(new FakeVirtualDesktops(), Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                var dock = overview.UnifiedDock!;
                Assert.True(dock.IsDockVisible);
                // The dock window is a live HWND. Resolve its HwndSource directly: without a
                // dispatcher pump the visual tree may not be connected yet, but the class handler
                // for PreviewKeyDown still runs when the event is raised on the window.
                var handle = new WindowInteropHelper(dock).EnsureHandle();
                var source = HwndSource.FromHwnd(handle);
                Assert.NotNull(source);

                var args = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice, source!, 0, System.Windows.Input.Key.Escape)
                {
                    RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
                };
                dock.RaiseEvent(args);

                Assert.True(args.Handled);
                Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);

                // An unbound key must fall through so focused dock buttons keep their own input.
                overview.TransitionTo(OverviewMode.Zooming);
                var unbound = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice, source!, 0, System.Windows.Input.Key.F9)
                {
                    RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
                };
                dock.RaiseEvent(unbound);
                Assert.False(unbound.Handled);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    public void PropertiesShelfShowsTheSelectedAnnotationsLiveStyle()
        => WorkspaceViewTests.OnSta(() =>
        {
            var dock = new UnifiedDockControl();
            var note = new CanvasAnnotation(Guid.NewGuid(), Guid.NewGuid(), AnnotationKind.Text,
                new(0, 0), new(200, 100), Color: "#60A5FA", Stroke: 12, FontSize: 32);

            // The tool's own style is still the stale creation default.
            dock.UpdateState(new CanvasDockState(AnnotationMode.Select, SelectedCount: 1, Selected: note,
                Color: "#FBBF24", Stroke: 3, FontSize: 96));

            Assert.Equal("12 px", dock.StrokeButton.Content);
            Assert.Equal("Size 32", dock.SizeButton.Content);
            Assert.Equal(2, Swatch(dock, "#60A5FA").BorderThickness.Left);
            Assert.Equal(1, Swatch(dock, "#FBBF24").BorderThickness.Left);

            static System.Windows.Controls.Border Swatch(UnifiedDockControl dock, string hex)
                => dock.ColorSwatchesPanel.Children.OfType<System.Windows.Controls.Border>()
                    .Single(border => string.Equals(border.Tag as string, hex, StringComparison.OrdinalIgnoreCase));
        });

    [Fact]
    public void BuildDockStateUsesTheSelectionWhenTheToolHasNoFocusedAnnotation()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-state-" + Guid.NewGuid().ToString("N"));
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

                var note = new CanvasAnnotation(Guid.NewGuid(), desktops.CurrentDesktopId, AnnotationKind.Text,
                    new(10, 10), new(240, 120), Text: "Live style", Color: "#34D399", Stroke: 6, FontSize: 24,
                    TextAlign: AnnotationTextAlign.Right, Emphasis: AnnotationTextEmphasis.Bold, CardFill: AnnotationCardFill.Tinted);
                overview.Annotations!.Put(note);

                // Marquee-style selection does not focus the annotation tool.
                overview.AnnotationTool!.ClearSelection();
                overview.Selection.Replace([new(CanvasItemKind.Annotation, note.Id)]);

                var state = overview.BuildDockState();

                Assert.Equal(note, state.Selected);
                Assert.Equal("#34D399", state.Color);
                Assert.Equal(6, state.Stroke);
                Assert.Equal(24, state.FontSize);
                Assert.Equal(AnnotationTextAlign.Right, state.TextAlign);
                Assert.Equal(AnnotationTextEmphasis.Bold, state.Emphasis);
                Assert.Equal(AnnotationCardFill.Tinted, state.CardFill);
                Assert.True(state.CanCopy);
                Assert.True(state.CanCut);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    public void ShelfEditsApplyToEverySelectedAnnotationOfTheSameKind()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-multi-" + Guid.NewGuid().ToString("N"));
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

                var desktop = desktops.CurrentDesktopId;
                CanvasAnnotation Shape(double x) => new(Guid.NewGuid(), desktop, AnnotationKind.Rectangle, new(x, 10), new(x + 120, 90));
                var left = Shape(10);
                var right = Shape(200);
                var note = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text, new(10, 200), new(260, 260), Text: "Checklist");
                var second = note with { Id = Guid.NewGuid(), B = new(260, 320) };
                overview.Annotations!.Put(left);
                overview.Annotations.Put(right);
                overview.Annotations.Put(note);
                overview.Annotations.Put(second);

                void Select(params CanvasAnnotation[] items)
                {
                    // Marquee-style selection never focuses the annotation tool.
                    overview.AnnotationTool!.ClearSelection();
                    overview.Selection.Replace(items.Select(i => new CanvasItemId(CanvasItemKind.Annotation, i.Id)).ToArray());
                }

                // Same-kind group: the shelf restyles both shapes at once, as one undo step.
                Select(left, right);
                Assert.Equal(AnnotationKind.Rectangle, overview.BuildDockState().Selected!.Kind);
                overview.ChangeAnnotationStyle("#34D399", 8);
                Assert.All(new[] { left.Id, right.Id }, id =>
                {
                    var item = overview.Annotations.Items.Single(a => a.Id == id);
                    Assert.Equal("#34D399", item.Color);
                    Assert.Equal(8, item.Stroke);
                });
                Assert.Equal("#FBBF24", overview.Annotations.Items.Single(a => a.Id == note.Id).Color);
                Assert.True(overview.Annotations.Undo(desktop));
                Assert.All(new[] { left.Id, right.Id }, id => Assert.Equal("#FBBF24", overview.Annotations.Items.Single(a => a.Id == id).Color));
                Assert.True(left == overview.Annotations.Items.Single(a => a.Id == left.Id));

                // Same-kind text group: size and text style travel together for every card.
                Select(note, second);
                overview.ChangeAnnotationFontSize(40);
                overview.ChangeAnnotationTextStyle(emphasis: AnnotationTextEmphasis.Bold, fill: AnnotationCardFill.None);
                Assert.All(new[] { note.Id, second.Id }, id =>
                {
                    var item = overview.Annotations.Items.Single(a => a.Id == id);
                    Assert.Equal(40, item.FontSize);
                    Assert.Equal(AnnotationTextEmphasis.Bold, item.Emphasis);
                    Assert.Equal(AnnotationCardFill.None, item.CardFill);
                });

                // Mixed selection keeps the other kind untouched instead of restyling it blindly.
                Select(left, note);
                Assert.Equal(note.Id, overview.BuildDockState().Selected!.Id);
                overview.ChangeAnnotationStyle("#60A5FA", 4);
                Assert.Equal("#60A5FA", overview.Annotations.Items.Single(a => a.Id == note.Id).Color);
                Assert.Equal("#FBBF24", overview.Annotations.Items.Single(a => a.Id == left.Id).Color);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void DockContextualCommandsEditRealSelectedAnnotationsAndKeepHistoryWorking()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-style-" + Guid.NewGuid().ToString("N"));
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
                var note = new CanvasAnnotation(Guid.NewGuid(), desktops.CurrentDesktopId, AnnotationKind.Text,
                    new(10, 10), new(240, 120), Text: "Dock style test", Stroke: 12);
                overview.Annotations!.Put(note);
                overview.Selection.Replace([new(CanvasItemKind.Annotation, note.Id)]);
                overview.SelectAnnotationMode(AnnotationMode.Select);

                var panel = overview.UnifiedDock!.Panel;
                panel.UpdateState(new CanvasDockState(AnnotationMode.Select, Selected: note, SelectedCount: 1));
                Assert.Equal(Visibility.Visible, panel.StrokeButton.Visibility);
                Assert.Equal("12 px", panel.StrokeButton.Content);

                void ChooseColor(string name)
                {
                    var swatch = panel.ColorSwatchesPanel.Children.OfType<System.Windows.Controls.Border>()
                        .Single(border => (string?)border.ToolTip == name);
                    swatch.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                    {
                        RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent
                    });
                }

                void ChooseMenu(string property, string value)
                {
                    var menu = property switch
                    {
                        "Stroke" => panel.OpenStrokeMenu(),
                        "Size" => panel.OpenSizeMenu(),
                        "Style" => panel.OpenStyleMenu(),
                        "Fill" => panel.OpenFillMenu(),
                        _ => throw new ArgumentOutOfRangeException(nameof(property))
                    };
                    Assert.NotNull(menu);
                    var item = menu!.Items.OfType<MenuItem>().Single(entry => (string)entry.Header == value);
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }

                ChooseColor("Mint");
                ChooseMenu("Stroke", "8 px");
                ChooseMenu("Size", "Size 56");
                ChooseMenu("Style", "Bold");
                ChooseMenu("Style", "Align center");
                ChooseMenu("Fill", "Outline");

                var changed = Assert.Single(overview.Annotations.Items);
                Assert.Equal("#34D399", changed.Color);
                Assert.Equal(8, changed.Stroke);
                Assert.Equal(56, changed.FontSize);
                Assert.Equal(AnnotationTextEmphasis.Bold, changed.Emphasis);
                Assert.Equal(AnnotationTextAlign.Center, changed.TextAlign);
                Assert.Equal(AnnotationCardFill.None, changed.CardFill);

                var moreMenu = panel.OpenMoreMenu();
                Assert.NotNull(moreMenu);
                var deleteItem = moreMenu!.Items.OfType<MenuItem>().Single(entry => (string)entry.Header == "Delete annotation");
                deleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Empty(overview.Annotations.Items);

                panel.UndoButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(changed, Assert.Single(overview.Annotations.Items));

                panel.RedoButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Empty(overview.Annotations.Items);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void GuidedTutorialPanelSitsAboveTheDock()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "dock-tour-" + Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas();
                canvas.SetCamera(700, -300);
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var desktops = new FakeVirtualDesktops { CurrentDesktopId = Guid.NewGuid() };
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config, prefersReducedStartupMotion: () => false);
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.AttachTutorial(Path.Combine(folder, "tutorial.json"));
                overview.TransitionTo(OverviewMode.Zooming);

                var dock = overview.UnifiedDock;
                Assert.NotNull(dock);
                Assert.True(dock!.IsDockVisible);

                overview.StartGuidedTutorial();
                System.Windows.Forms.Application.DoEvents();

                var tour = overview.TourPanel;
                Assert.NotNull(tour);
                Assert.True(tour!.Visible);
                var handle = new WindowInteropHelper(dock).EnsureHandle();
                Assert.True(PInvoke.GetWindowRect((HWND)handle, out var dockRect), "GetWindowRect failed");
                Assert.True(tour.Bottom <= dockRect.top,
                    $"tour bottom {tour.Bottom} must sit above dock top {dockRect.top}");
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    public void DockKeyboardBridgeMapsWpfKeysToTheFormsShortcutTable()
    {
        const System.Windows.Forms.Keys ControlShiftK =
            System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.K;

        Assert.Equal(System.Windows.Forms.Keys.Escape,
            UnifiedDockWindow.ToFormsKeys(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None));
        Assert.Equal(ControlShiftK,
            UnifiedDockWindow.ToFormsKeys(System.Windows.Input.Key.K,
                System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift));
        Assert.Equal(System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.F4,
            UnifiedDockWindow.ToFormsKeys(System.Windows.Input.Key.F4, System.Windows.Input.ModifierKeys.Alt));
        // Keys WPF cannot map (e.g. Key.None) must not be forwarded as a bogus shortcut.
        Assert.Equal(System.Windows.Forms.Keys.None,
            UnifiedDockWindow.ToFormsKeys(System.Windows.Input.Key.None, System.Windows.Input.ModifierKeys.None));
    }

    private static void AssertAbove(IntPtr upper, IntPtr lower)
    {
        var current = PInvoke.GetWindow((HWND)lower, GET_WINDOW_CMD.GW_HWNDPREV);
        while (current != HWND.Null)
        {
            if ((IntPtr)current == upper) return;
            current = PInvoke.GetWindow(current, GET_WINDOW_CMD.GW_HWNDPREV);
        }
        Assert.Fail($"Expected window {upper} above {lower}.");
    }
}
