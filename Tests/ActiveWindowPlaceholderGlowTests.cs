using System;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class ActiveWindowPlaceholderGlowTests
{
    private static byte[] Render(PlaceholderAnnotation card, double zoom, long timestamp = 0, bool reduceMotion = false)
    {
        var surface = new OverviewAnnotationSurface();
        var snapshot = OverviewAnnotationSnapshot.Create([], 0, 0, zoom, new(0, 0, 400, 300),
            null, false, placeholders: [card], showHints: false, reduceMotion: reduceMotion, timestamp: timestamp);
        surface.Update(snapshot);
        surface.Measure(new System.Windows.Size(400, 300));
        surface.Arrange(new Rect(0, 0, 400, 300));
        var bitmap = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[400 * 300 * 4];
        bitmap.CopyPixels(pixels, 1600, 0);
        surface.ClearAnimation();
        return pixels;
    }

    [Fact]
    public void ActiveCardRendersDistinguishedBackgroundAndAnimatedGlow()
        => WorkspaceViewTests.OnSta(() =>
        {
            // Card bounds: X=100, Y=100, W=200, H=100 on a 400x300 surface at zoom=1.
            var inactiveCard = new PlaceholderAnnotation((IntPtr)1, "Closed App",
                new WorldRect { X = 100, Y = 100, W = 200, H = 100 }, ImageOpacity: 0, IsActiveWindow: false);
            var activeCard = new PlaceholderAnnotation((IntPtr)2, "Active App",
                new WorldRect { X = 100, Y = 100, W = 200, H = 100 }, ImageOpacity: 0, IsActiveWindow: true);

            var inactivePixels = Render(inactiveCard, zoom: 1);
            var activePixels = Render(activeCard, zoom: 1, timestamp: 550); // 550ms = peak of 2200ms pulse

            // Check card interior center pixel: (x=200, y=150)
            int centerIdx = (150 * 400 + 200) * 4;
            byte inactB = inactivePixels[centerIdx];
            byte inactG = inactivePixels[centerIdx + 1];
            byte inactR = inactivePixels[centerIdx + 2];

            byte actB = activePixels[centerIdx];
            byte actG = activePixels[centerIdx + 1];
            byte actR = activePixels[centerIdx + 2];

            // Inactive card fill is dark slate: RGB(28, 34, 46) => B=46, G=34, R=28
            Assert.InRange(inactB, 40, 52);
            Assert.InRange(inactG, 30, 40);
            Assert.InRange(inactR, 24, 34);

            // Active card fill is distinguished deep sapphire/azure gradient: B significantly higher, rich cyan-blue
            Assert.True(actB > inactB + 20, $"Active card blue ({actB}) should be noticeably higher than inactive ({inactB})");
            Assert.True(actG > inactG + 10, $"Active card green ({actG}) should be noticeably higher than inactive ({inactG})");

            // Check pixel 3px outside top card border: (x=200, y=97)
            int glowIdx = (97 * 400 + 200) * 4;
            byte inactiveOutsideA = inactivePixels[glowIdx + 3];
            byte activeOutsideA = activePixels[glowIdx + 3];
            byte activeOutsideB = activePixels[glowIdx];

            // Inactive card has no glow outside its border
            Assert.Equal(0, inactiveOutsideA);

            // Active card has an animated glow radiating outside its bounds
            Assert.True(activeOutsideA > 0, "Active card should have visible glow aura outside its rect");
            Assert.True(activeOutsideB > 0, "Active card glow should have blue/cyan tint");
        });

    [Fact]
    public void MinimizedCardRendersDeepOceanTealBackgroundAndCalmGlow()
        => WorkspaceViewTests.OnSta(() =>
        {
            var inactiveCard = new PlaceholderAnnotation((IntPtr)1, "Closed App",
                new WorldRect { X = 100, Y = 100, W = 200, H = 100 }, ImageOpacity: 0, IsActiveWindow: false, IsMinimizedWindow: false);
            var minCard = new PlaceholderAnnotation((IntPtr)2, "Minimized App",
                new WorldRect { X = 100, Y = 100, W = 200, H = 100 }, ImageOpacity: 0, IsActiveWindow: false, IsMinimizedWindow: true);

            var inactivePixels = Render(inactiveCard, zoom: 1);
            var minPixels = Render(minCard, zoom: 1, timestamp: 550);

            // Check card interior center pixel: (x=200, y=150)
            int centerIdx = (150 * 400 + 200) * 4;
            byte inactB = inactivePixels[centerIdx];
            byte inactG = inactivePixels[centerIdx + 1];
            byte inactR = inactivePixels[centerIdx + 2];

            byte minB = minPixels[centerIdx];
            byte minG = minPixels[centerIdx + 1];
            byte minR = minPixels[centerIdx + 2];

            // Inactive card fill is dark slate: RGB(28, 34, 46) => B=46, G=34, R=28
            Assert.InRange(inactB, 40, 52);
            Assert.InRange(inactG, 30, 40);
            Assert.InRange(inactR, 24, 34);

            // Minimized card fill is deep ocean teal: RGB(14, 67, 71) to RGB(9, 43, 45) -> Green and Blue dominate, Red is low (< 20)
            Assert.True(minG > inactG + 10, $"Minimized card green ({minG}) should be noticeably higher than inactive ({inactG})");
            Assert.True(minR < inactR, $"Minimized card red ({minR}) should be lower than inactive ({inactR})");

            // Check pixel 3px outside top card border: (x=200, y=97)
            int glowIdx = (97 * 400 + 200) * 4;
            byte inactiveOutsideA = inactivePixels[glowIdx + 3];
            byte minOutsideA = minPixels[glowIdx + 3];
            byte minOutsideG = minPixels[glowIdx + 1];

            // Inactive card has no glow outside its border
            Assert.Equal(0, inactiveOutsideA);

            // Minimized card has a calm teal glow radiating outside its bounds
            Assert.True(minOutsideA > 0, "Minimized card should have visible glow aura outside its rect");
            Assert.True(minOutsideG > 0, "Minimized card glow should have teal (green/blue) tint");
        });

    [Fact]
    public void ActiveCardGlowModulatesWithTimestamp()
        => WorkspaceViewTests.OnSta(() =>
        {
            var card = new PlaceholderAnnotation((IntPtr)1, "Live Window",
                new WorldRect { X = 100, Y = 100, W = 200, H = 100 }, ImageOpacity: 0, IsActiveWindow: true);

            // Pulse period is 2200ms:
            // At timestamp 1650ms: phase = 3pi/2 -> pulse is at minimum (0.0)
            // At timestamp 550ms: phase = pi/2 -> pulse is at maximum (1.0)
            var minPixels = Render(card, zoom: 1, timestamp: 1650);
            var maxPixels = Render(card, zoom: 1, timestamp: 550);

            int glowIdx = (96 * 400 + 200) * 4; // 4px above top edge
            byte minA = minPixels[glowIdx + 3];
            byte maxA = maxPixels[glowIdx + 3];

            Assert.True(maxA > minA, $"Peak pulse alpha ({maxA}) should be greater than trough alpha ({minA})");
        });

    [Fact]
    public void MinimizedCardGlowModulatesWithTimestamp()
        => WorkspaceViewTests.OnSta(() =>
        {
            var card = new PlaceholderAnnotation((IntPtr)1, "Minimized Window",
                new WorldRect { X = 100, Y = 100, W = 200, H = 100 }, ImageOpacity: 0, IsMinimizedWindow: true);

            var minPixels = Render(card, zoom: 1, timestamp: 1650);
            var maxPixels = Render(card, zoom: 1, timestamp: 550);

            int glowIdx = (97 * 400 + 200) * 4; // 3px above top edge
            byte minA = minPixels[glowIdx + 3];
            byte maxA = maxPixels[glowIdx + 3];

            Assert.True(maxA > minA, $"Peak pulse alpha ({maxA}) should be greater than trough alpha ({minA})");
        });

    [Fact]
    public void ReducedMotionRendersSteadyGlowWithoutAnimation()
        => WorkspaceViewTests.OnSta(() =>
        {
            var card = new PlaceholderAnnotation((IntPtr)1, "Live Window",
                new WorldRect { X = 100, Y = 100, W = 200, H = 100 }, ImageOpacity: 0, IsActiveWindow: true);

            var reduced1 = Render(card, zoom: 1, timestamp: 0, reduceMotion: true);
            var reduced2 = Render(card, zoom: 1, timestamp: 550, reduceMotion: true);

            int glowIdx = (97 * 400 + 200) * 4;
            Assert.True(reduced1[glowIdx + 3] > 0, "Glow should still render under reduced motion");
            Assert.Equal(reduced1[glowIdx + 3], reduced2[glowIdx + 3]); // Steady, identical across timestamps
        });

    [Fact]
    public void OccluderAboveActiveCardClipsItsGlow()
        => WorkspaceViewTests.OnSta(() =>
        {
            var card = new PlaceholderAnnotation((IntPtr)1, "Live Window",
                new WorldRect { X = 100, Y = 100, W = 200, H = 100 }, ImageOpacity: 0, IsActiveWindow: true,
                Occluders: [new WorldRect { X = 50, Y = 50, W = 300, H = 60 }]); // covers Y=50..110

            var pixels = Render(card, zoom: 1, timestamp: 550);

            // Inside occluder region above card: (x=200, y=97) should be clipped out
            int occludedIdx = (97 * 400 + 200) * 4;
            Assert.Equal(0, pixels[occludedIdx + 3]);

            // Unoccluded bottom glow: (x=200, y=203) should still be visible
            int bottomGlowIdx = (203 * 400 + 200) * 4;
            Assert.True(pixels[bottomGlowIdx + 3] > 0, "Bottom unoccluded glow should be drawn");
        });

    [Fact]
    public void PlaceholderFormReflectsActiveAndMinimizedStateAndColor()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new WorkspaceHarness();
            var openItem = new WorkspaceItemView(Guid.NewGuid(), "Open App", "", @"C:\app.exe", false,
                new WorldRect { W = 400, H = 300 }, "Open");
            var closedItem = new WorkspaceItemView(Guid.NewGuid(), "Closed App", "", @"C:\app.exe", false,
                new WorldRect { W = 400, H = 300 }, "Placeholder");
            var minItem = new WorkspaceItemView(Guid.NewGuid(), "Minimized App", "", @"C:\app.exe", false,
                new WorldRect { W = 400, H = 300 }, "Minimized");
            var sleepItem = new WorkspaceItemView(Guid.NewGuid(), "Sleeping App", "", @"C:\app.exe", false,
                new WorldRect { W = 400, H = 300 }, "Sleeping");

            using var openForm = new PlaceholderForm(rig.Workspace, openItem);
            using var closedForm = new PlaceholderForm(rig.Workspace, closedItem);
            using var minForm = new PlaceholderForm(rig.Workspace, minItem);
            using var sleepForm = new PlaceholderForm(rig.Workspace, sleepItem);

            Assert.True(openForm.IsActiveWindow);
            Assert.False(openForm.IsMinimizedWindow);

            Assert.False(closedForm.IsActiveWindow);
            Assert.False(closedForm.IsMinimizedWindow);

            Assert.False(minForm.IsActiveWindow);
            Assert.True(minForm.IsMinimizedWindow);

            Assert.False(sleepForm.IsActiveWindow);
            Assert.True(sleepForm.IsMinimizedWindow);

            Assert.Equal(System.Drawing.Color.FromArgb(18, 52, 84), openForm.BackColor);
            Assert.Equal(System.Drawing.Color.FromArgb(28, 34, 46), closedForm.BackColor);
            Assert.Equal(System.Drawing.Color.FromArgb(13, 59, 62), minForm.BackColor);
            Assert.Equal(System.Drawing.Color.FromArgb(13, 59, 62), sleepForm.BackColor);

            // Updating item state toggles active and minimized states and BackColor
            closedForm.UpdateItem(openItem);
            Assert.True(closedForm.IsActiveWindow);
            Assert.False(closedForm.IsMinimizedWindow);
            Assert.Equal(System.Drawing.Color.FromArgb(18, 52, 84), closedForm.BackColor);

            closedForm.UpdateItem(minItem);
            Assert.False(closedForm.IsActiveWindow);
            Assert.True(closedForm.IsMinimizedWindow);
            Assert.Equal(System.Drawing.Color.FromArgb(13, 59, 62), closedForm.BackColor);

            closedForm.UpdateItem(closedItem);
            Assert.False(closedForm.IsActiveWindow);
            Assert.False(closedForm.IsMinimizedWindow);
            Assert.Equal(System.Drawing.Color.FromArgb(28, 34, 46), closedForm.BackColor);
        });

    [Fact]
    public void OverviewCardTransitionsPreservesIsActiveAndMinimizedWindow()
    {
        var transitions = new OverviewCardTransitions();
        var activeCard = new PlaceholderAnnotation((IntPtr)10, "Terminal", new WorldRect { W = 300, H = 200 },
            IsActiveWindow: true, IsMinimizedWindow: false);
        var minCard = new PlaceholderAnnotation((IntPtr)11, "Editor", new WorldRect { W = 300, H = 200 },
            IsActiveWindow: false, IsMinimizedWindow: true);

        var input1 = new OverviewCardInput(Guid.NewGuid(), activeCard, OverviewCardMode.Simple);
        var input2 = new OverviewCardInput(Guid.NewGuid(), minCard, OverviewCardMode.Simple);
        var result = transitions.Update([input1, input2], 100);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].IsActiveWindow, "OverviewCardTransitions must preserve IsActiveWindow property");
        Assert.False(result[0].IsMinimizedWindow);
        Assert.False(result[1].IsActiveWindow);
        Assert.True(result[1].IsMinimizedWindow, "OverviewCardTransitions must preserve IsMinimizedWindow property");
    }

    [Fact]
    public void ActiveCardGlowSuppressedWhileMotionIsActive()
        => WorkspaceViewTests.OnSta(() =>
        {
            var card = new PlaceholderAnnotation((IntPtr)1, "Live Window",
                new WorldRect { X = 100, Y = 100, W = 200, H = 100 }, ImageOpacity: 0, IsActiveWindow: true);

            var surface = new OverviewAnnotationSurface();
            var snapshot = OverviewAnnotationSnapshot.Create([], 0, 0, 1, new(0, 0, 400, 300),
                null, false, placeholders: [card], showHints: false, motionActive: true, timestamp: 550);
            surface.Update(snapshot);
            surface.Measure(new System.Windows.Size(400, 300));
            surface.Arrange(new Rect(0, 0, 400, 300));
            var bitmap = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var pixels = new byte[400 * 300 * 4];
            bitmap.CopyPixels(pixels, 1600, 0);
            surface.ClearAnimation();

            int glowIdx = (97 * 400 + 200) * 4;
            Assert.Equal(0, pixels[glowIdx + 3]); // No glow rendered during motion
        });

    [Fact]
    public void ActiveCardGlowSuppressedWhileCardFadingIn()
        => WorkspaceViewTests.OnSta(() =>
        {
            // During card transition, opacity is between 0 and 1 (e.g. 0.5)
            var fadingCard = new PlaceholderAnnotation((IntPtr)1, "Live Window",
                new WorldRect { X = 100, Y = 100, W = 200, H = 100 }, ImageOpacity: 0, IsActiveWindow: true, Opacity: 0.5);

            var pixels = Render(fadingCard, zoom: 1, timestamp: 550);

            int glowIdx = (97 * 400 + 200) * 4;
            Assert.Equal(0, pixels[glowIdx + 3]); // Glow suppressed while card is fading in
        });

    [Fact]
    public void VisibleGlowAndVisibleCardReturnFrozenGeometries()
    {
        var card = new PlaceholderAnnotation((IntPtr)1, "Live Window",
            new WorldRect { X = 100, Y = 100, W = 200, H = 100 },
            Occluders: [new WorldRect { X = 50, Y = 50, W = 300, H = 60 }]);
        var snapshot = OverviewAnnotationSnapshot.Create([], 0, 0, 1, new(0, 0, 400, 300), null, false);
        var context = new OverviewAnnotationContext(snapshot, new CanvasViewTransform(0, 0, 1, new CanvasRect(0, 0, 1920, 1080)), new DpiScale(1, 1), new System.Windows.Size(400, 300));

        var cardGeom = PlaceholderAnnotationLayer.VisibleCard(card, context);
        var glowGeom = PlaceholderAnnotationLayer.VisibleGlow(card, new Rect(90, 90, 220, 120), context);

        Assert.True(cardGeom.IsFrozen, "VisibleCard geometry must be frozen for WPF hardware performance");
        Assert.True(glowGeom.IsFrozen, "VisibleGlow geometry must be frozen for WPF hardware performance");
    }
}
