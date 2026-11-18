using System;
using System.Collections.Generic;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class CanvasZOrderTests
{
    private static readonly IReadOnlySet<IntPtr> EmptyPriority = new HashSet<IntPtr>();

    [Fact]
    public void TaskbarAndShellPopupClassMatching_MatchesExpectedShellClassesAndExcludesGenericApps()
    {
        Assert.True(CanvasZOrder.IsTaskbarClass("Shell_TrayWnd"));
        Assert.True(CanvasZOrder.IsTaskbarClass("Shell_SecondaryTrayWnd"));
        Assert.False(CanvasZOrder.IsTaskbarClass("TopLevelWindowForOverflowXamlIsland"));
        Assert.False(CanvasZOrder.IsTaskbarClass("Windows.UI.Core.CoreWindow"));
        Assert.False(CanvasZOrder.IsTaskbarClass("ApplicationFrameWindow"));
        Assert.False(CanvasZOrder.IsTaskbarClass("Progman"));

        Assert.True(CanvasZOrder.IsShellTrayPopupClass("TopLevelWindowForOverflowXamlIsland"));
        Assert.True(CanvasZOrder.IsShellTrayPopupClass("NotifyIconOverflowWindow"));
        Assert.True(CanvasZOrder.IsShellTrayPopupClass("TaskListThumbnailWnd"));
        Assert.True(CanvasZOrder.IsShellTrayPopupClass("Xaml_WindowedPopupClass"));
        Assert.True(CanvasZOrder.IsShellTrayPopupClass("XamlExplorerHostIslandWindow"));
        Assert.True(CanvasZOrder.IsShellTrayPopupClass("#32768"));

        Assert.False(CanvasZOrder.IsShellTrayPopupClass("Shell_TrayWnd"));
        Assert.False(CanvasZOrder.IsShellTrayPopupClass("Windows.UI.Core.CoreWindow"));
        Assert.False(CanvasZOrder.IsShellTrayPopupClass("TaskManagerWindow"));
        Assert.False(CanvasZOrder.IsShellTrayPopupClass("Notepad"));
    }

    [Fact]
    public void SelectCeilingAnchor_ReturnsHwndTopmost_WhenTaskbarHiddenAndNoPriorityPopups()
    {
        var band = new[]
        {
            new TopmostWindowCandidate((IntPtr)100, "Shell_TrayWnd", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
            new TopmostWindowCandidate((IntPtr)101, "TopLevelWindowForOverflowXamlIsland", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
        };

        IntPtr anchor = CanvasZOrder.SelectCeilingAnchor(band, clampBelowTaskbar: false, EmptyPriority);
        Assert.Equal(CanvasZOrder.HwndTopmost, anchor);
    }

    [Fact]
    public void SelectCeilingAnchor_ReturnsPriorityPopup_EvenWhenTaskbarHidden()
    {
        var menu = (IntPtr)200;
        var submenu = (IntPtr)201;
        var band = new[]
        {
            new TopmostWindowCandidate(submenu, "WindowsForms10.Window.8.app.0", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: menu),
            new TopmostWindowCandidate(menu, "WindowsForms10.Window.8.app.0", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
            new TopmostWindowCandidate((IntPtr)100, "Shell_TrayWnd", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
        };

        var priority = new HashSet<IntPtr> { menu, submenu };
        IntPtr anchor = CanvasZOrder.SelectCeilingAnchor(band, clampBelowTaskbar: false, priority);
        Assert.Equal(menu, anchor);
    }

    [Fact]
    public void SelectCeilingAnchor_ReturnsLowestTaskbar_AcrossMultiMonitorTaskbars()
    {
        var primary = (IntPtr)100;
        var secondary = (IntPtr)101;
        var band = new[]
        {
            new TopmostWindowCandidate((IntPtr)50, "Xaml_WindowedPopupClass", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: primary),
            new TopmostWindowCandidate(primary, "Shell_TrayWnd", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
            new TopmostWindowCandidate((IntPtr)200, "TaskManagerWindow", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
            new TopmostWindowCandidate(secondary, "Shell_SecondaryTrayWnd", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
        };

        IntPtr anchor = CanvasZOrder.SelectCeilingAnchor(band, clampBelowTaskbar: true, EmptyPriority);
        Assert.Equal(secondary, anchor);
    }

    [Fact]
    public void SelectCeilingAnchor_ReturnsLowestVisibleShellPopup_WhenOverflowIsBelowTaskbar()
    {
        var taskbar = (IntPtr)100;
        var overflow = (IntPtr)102;
        var band = new[]
        {
            new TopmostWindowCandidate(taskbar, "Shell_TrayWnd", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
            new TopmostWindowCandidate((IntPtr)101, "WindowsForms10.Window.8.app.0", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
            new TopmostWindowCandidate(overflow, "TopLevelWindowForOverflowXamlIsland", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
        };

        IntPtr anchor = CanvasZOrder.SelectCeilingAnchor(band, clampBelowTaskbar: true, EmptyPriority);
        Assert.Equal(overflow, anchor);
    }

    [Fact]
    public void SelectCeilingAnchor_IgnoresHiddenCloakedOrEmptyBoundsShellPopups_ToKeepCanvasAboveUserTopmostApps()
    {
        var taskbar = (IntPtr)100;
        var band = new[]
        {
            new TopmostWindowCandidate(taskbar, "Shell_TrayWnd", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
            new TopmostWindowCandidate((IntPtr)200, "TaskManagerWindow", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
            new TopmostWindowCandidate((IntPtr)201, "TopLevelWindowForOverflowXamlIsland", IsVisible: false, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
            new TopmostWindowCandidate((IntPtr)202, "Xaml_WindowedPopupClass", IsVisible: true, IsCloaked: true, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
            new TopmostWindowCandidate((IntPtr)203, "Xaml_WindowedPopupClass", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: false, Owner: IntPtr.Zero),
            new TopmostWindowCandidate((IntPtr)204, "Windows.UI.Core.CoreWindow", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
        };

        IntPtr anchor = CanvasZOrder.SelectCeilingAnchor(band, clampBelowTaskbar: true, EmptyPriority);
        Assert.Equal(taskbar, anchor);
    }

    [Fact]
    public void SelectCeilingAnchor_ExcludesSelfAndOwnedWindows_AndFallsBackWhenExplorerMissing()
    {
        var self = (IntPtr)300;
        var ownedMenu = (IntPtr)301;
        var band = new[]
        {
            new TopmostWindowCandidate(ownedMenu, "WindowsForms10.Window.8.app.0", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: self),
            new TopmostWindowCandidate(self, "WindowsForms10.Window.8.app.0", IsVisible: true, IsCloaked: false, HasNonEmptyBounds: true, Owner: IntPtr.Zero),
        };

        var priority = new HashSet<IntPtr> { ownedMenu, self };
        IntPtr anchor = CanvasZOrder.SelectCeilingAnchor(band, clampBelowTaskbar: true, priority, excludeWindow: self);
        Assert.Equal(CanvasZOrder.HwndTopmost, anchor);
    }

    [Fact]
    public void ShouldRewriteWindowPosInsertAfter_AllowsInitialTopmostPromotionAndDemotion_ClampsTopmostPromotions()
    {
        var anchor = (IntPtr)100;
        var aboveAnchor = (IntPtr)50;
        var belowAnchor = (IntPtr)150;
        static bool IsAbove(IntPtr upper, IntPtr lower) => upper == (IntPtr)50 && lower == (IntPtr)100;

        // Initial non-topmost -> topmost transition must NOT be rewritten.
        Assert.False(CanvasZOrder.ShouldRewriteWindowPosInsertAfter(
            CanvasZOrder.HwndTopmost, anchor, windowIsTopmost: false, IsAbove));

        // Demotions must NEVER be rewritten.
        Assert.False(CanvasZOrder.ShouldRewriteWindowPosInsertAfter(
            CanvasZOrder.HwndNoTopmost, anchor, windowIsTopmost: true, IsAbove));
        Assert.False(CanvasZOrder.ShouldRewriteWindowPosInsertAfter(
            CanvasZOrder.HwndBottom, anchor, windowIsTopmost: true, IsAbove));

        // No ceiling -> no rewrite.
        Assert.False(CanvasZOrder.ShouldRewriteWindowPosInsertAfter(
            CanvasZOrder.HwndTop, CanvasZOrder.HwndTopmost, windowIsTopmost: true, IsAbove));

        // Already targeting anchor or a sibling below anchor -> preserve intra-Canvas Z-order.
        Assert.False(CanvasZOrder.ShouldRewriteWindowPosInsertAfter(
            anchor, anchor, windowIsTopmost: true, IsAbove));
        Assert.False(CanvasZOrder.ShouldRewriteWindowPosInsertAfter(
            belowAnchor, anchor, windowIsTopmost: true, IsAbove));

        // Promoting to HWND_TOP, HWND_TOPMOST, or a window above anchor -> MUST clamp.
        Assert.True(CanvasZOrder.ShouldRewriteWindowPosInsertAfter(
            CanvasZOrder.HwndTop, anchor, windowIsTopmost: true, IsAbove));
        Assert.True(CanvasZOrder.ShouldRewriteWindowPosInsertAfter(
            CanvasZOrder.HwndTopmost, anchor, windowIsTopmost: true, IsAbove));
        Assert.True(CanvasZOrder.ShouldRewriteWindowPosInsertAfter(
            aboveAnchor, anchor, windowIsTopmost: true, IsAbove));
    }
}
