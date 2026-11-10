using System;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class DynamicLodTests
{
    [Theory]
    [InlineData(0.10, 100.0, true)]    // distant zoom (< 0.25)
    [InlineData(0.249, 100.0, true)]   // boundary zoom just under 0.25
    [InlineData(0.25, 100.0, false)]   // boundary zoom at 0.25 with large window
    [InlineData(0.30, 100.0, false)]   // close zoom (>= 0.25) with large window
    [InlineData(0.30, 30.0, true)]     // close zoom but window smaller than 40px (< 40)
    [InlineData(0.30, 39.9, true)]     // boundary size just under 40px
    [InlineData(0.30, 40.0, false)]    // boundary size at 40px
    [InlineData(0.30, 50.0, false)]    // close zoom with large window (>= 40px)
    [InlineData(1.0, 35.0, true)]      // 1.0 zoom with window smaller than 40px
    [InlineData(1.0, 500.0, false)]    // 1.0 zoom with standard window
    public void IsDistantLodEvaluatesZoomAndProjectedSizeThresholds(double zoom, double projectedSize, bool expected)
    {
        Assert.Equal(expected, PlaceholderAnnotationLayer.IsDistantLod(zoom, projectedSize));
    }

    [Fact]
    public void IsDistantLodEvaluatesWidthAndHeightRectDimensions()
    {
        // Zoom < 0.25 activates LOD regardless of size
        Assert.True(PlaceholderAnnotationLayer.IsDistantLod(0.20, width: 200, height: 150));
        Assert.True(PlaceholderAnnotationLayer.IsDistantLod(0.20, new Rect(0, 0, 200, 150)));

        // Zoom >= 0.25 with both dimensions >= 40px does not activate LOD
        Assert.False(PlaceholderAnnotationLayer.IsDistantLod(0.30, width: 60, height: 45));
        Assert.False(PlaceholderAnnotationLayer.IsDistantLod(0.30, new Rect(0, 0, 60, 45)));

        // Zoom >= 0.25 with window smaller than 40px (< 40px maximum extent) activates LOD
        Assert.True(PlaceholderAnnotationLayer.IsDistantLod(0.30, width: 30, height: 20));
        Assert.True(PlaceholderAnnotationLayer.IsDistantLod(0.30, new Rect(0, 0, 30, 20)));
    }

    [Theory]
    [InlineData(true, 0.20, 100.0, true)]   // In motion at distant zoom -> Wireframe LOD active
    [InlineData(false, 0.20, 100.0, false)]  // Resting at distant zoom -> Wireframe LOD inactive (full detail)
    [InlineData(true, 0.30, 30.0, true)]    // In motion with tiny window -> Wireframe LOD active
    [InlineData(false, 0.30, 30.0, false)]   // Resting with tiny window -> Wireframe LOD inactive (full detail)
    [InlineData(true, 0.30, 60.0, false)]   // In motion with standard window -> Standard motion (no wireframe)
    [InlineData(false, 0.30, 60.0, false)]  // Resting with standard window -> Full detail
    public void ShouldUseWireframeLodRequiresActiveMotion(bool motionActive, double zoom, double projectedSize, bool expected)
    {
        Assert.Equal(expected, PlaceholderAnnotationLayer.ShouldUseWireframeLod(motionActive, zoom, projectedSize));
    }

    private static byte[] RenderSurface(PlaceholderAnnotation card, double zoom, bool motionActive, out OverviewAnnotationSurface surface)
        => RenderSurface([card], zoom, motionActive, out surface, 400, 300);

    private static byte[] RenderSurface(IReadOnlyList<PlaceholderAnnotation> cards, double zoom, bool motionActive,
        out OverviewAnnotationSurface surface, int width = 400, int height = 300, bool simplify = true)
    {
        surface = new OverviewAnnotationSurface();
        var snapshot = OverviewAnnotationSnapshot.Create([], 0, 0, zoom, new(0, 0, width, height),
            null, false, placeholders: cards, showHints: false, motionActive: motionActive)
            with { SimplifyWindowCardsDuringMotion = simplify };
        surface.Update(snapshot);
        surface.Measure(new System.Windows.Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistantMotionShowsWindowContentUnlessSimplificationIsEnabled(bool simplify)
        => WorkspaceViewTests.OnSta(() =>
        {
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255 }, 4);
            image.Freeze();
            var preview = new PlaceholderAnnotation((IntPtr)1, "Snapshot",
                new WorldRect { X = 200, Y = 200, W = 800, H = 400 }, Image: image, ImageOpacity: 1, LabelOpacity: 0);
            var title = new PlaceholderAnnotation((IntPtr)2, "Editor",
                new WorldRect { X = 1125, Y = 200, W = 800, H = 400 }, ImageOpacity: 0, LabelOpacity: 1);

            var pixels = RenderSurface([preview, title], 0.16, true, out var surface, simplify: simplify);
            int center = (64 * 400 + 96) * 4;
            Assert.Equal(simplify ? 2 : 0, surface.Placeholders.WireframeCardCount);
            Assert.Equal(simplify ? 0 : 1, surface.Placeholders.Labels.Count);
            Assert.Equal(!simplify, pixels[center + 2] > 240);
        });

    [Fact]
    public void InMotionDistantZoomBypassesLabelsAndIconsAndRendersWireframe()
        => WorkspaceViewTests.OnSta(() =>
        {
            var logo = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255 }, 4); // Red icon pixel (B=0, G=0, R=255, A=255)
            logo.Freeze();

            var card = new PlaceholderAnnotation((IntPtr)1, "Test Window Title",
                new WorldRect { X = 100, Y = 100, W = 800, H = 400 },
                ImageOpacity: 0, LabelOpacity: 1, Logo: logo);

            // Motion Active at zoom=0.10 (< 0.25 threshold):
            // Card projected size at zoom 0.10: X=10, Y=10, W=80, H=40
            var inMotionPixels = RenderSurface(card, zoom: 0.10, motionActive: true, out var motionSurface);

            // Verify that wireframe card was counted and labels collection is bypassed
            Assert.Equal(1, motionSurface.Placeholders.WireframeCardCount);
            Assert.Empty(motionSurface.Placeholders.Labels);

            // Check that the red logo icon was bypassed (no red pixels from logo)
            var redPixelsInMotion = Enumerable.Range(0, inMotionPixels.Length / 4)
                .Where(i => inMotionPixels[i * 4 + 2] > 240 && inMotionPixels[i * 4 + 1] < 10 && inMotionPixels[i * 4] < 10)
                .ToArray();
            Assert.Empty(redPixelsInMotion);

            // Settle Motion (motionActive = false) at same distant zoom:
            var settledPixels = RenderSurface(card, zoom: 0.10, motionActive: false, out var settledSurface);

            // Verify resting state detail restoration
            Assert.Equal(0, settledSurface.Placeholders.WireframeCardCount);
            // In resting state, distant icon fills the card and renders the logo
            var redPixelsSettled = Enumerable.Range(0, settledPixels.Length / 4)
                .Where(i => settledPixels[i * 4 + 2] > 240 && settledPixels[i * 4 + 1] < 10 && settledPixels[i * 4] < 10)
                .ToArray();
            Assert.NotEmpty(redPixelsSettled);
        });

    [Fact]
    public void StandardMotionPreservesDetailedRenderingWhenAboveThresholds()
        => WorkspaceViewTests.OnSta(() =>
        {
            var logo = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255 }, 4);
            logo.Freeze();

            // At zoom=0.30 (>= 0.25) and W=800, H=400: projected size is 240x120 (>= 40px)
            var card = new PlaceholderAnnotation((IntPtr)1, "App",
                new WorldRect { X = 100, Y = 100, W = 800, H = 400 },
                ImageOpacity: 0, LabelOpacity: 1, Logo: logo);

            RenderSurface(card, zoom: 0.30, motionActive: true, out var surface);

            // Not forced to wireframe: standard motion behavior is preserved
            Assert.Equal(0, surface.Placeholders.WireframeCardCount);
        });

    [Fact]
    public void MotionAtDistantZoomRendersColorCodedWireframeCards()
        => WorkspaceViewTests.OnSta(() =>
        {
            var inactiveCard = new PlaceholderAnnotation((IntPtr)1, "Normal",
                new WorldRect { X = 100, Y = 100, W = 400, H = 200 }, ImageOpacity: 0);
            var activeCard = new PlaceholderAnnotation((IntPtr)2, "Active",
                new WorldRect { X = 100, Y = 100, W = 400, H = 200 }, ImageOpacity: 0, IsActiveWindow: true);
            var minCard = new PlaceholderAnnotation((IntPtr)3, "Minimized",
                new WorldRect { X = 100, Y = 100, W = 400, H = 200 }, ImageOpacity: 0, IsMinimizedWindow: true);

            var inactivePixels = RenderSurface(inactiveCard, zoom: 0.10, motionActive: true, out var inactSurface);
            var activePixels = RenderSurface(activeCard, zoom: 0.10, motionActive: true, out var actSurface);
            var minPixels = RenderSurface(minCard, zoom: 0.10, motionActive: true, out var minSurface);

            Assert.Equal(1, inactSurface.Placeholders.WireframeCardCount);
            Assert.Equal(1, actSurface.Placeholders.WireframeCardCount);
            Assert.Equal(1, minSurface.Placeholders.WireframeCardCount);

            // Card center at zoom 0.10: (X=10 + 20 = 30, Y=10 + 10 = 20)
            int centerIdx = (20 * 400 + 30) * 4;

            byte inactB = inactivePixels[centerIdx];
            byte inactG = inactivePixels[centerIdx + 1];
            byte inactR = inactivePixels[centerIdx + 2];

            byte actB = activePixels[centerIdx];
            byte actG = activePixels[centerIdx + 1];
            byte actR = activePixels[centerIdx + 2];

            byte minB = minPixels[centerIdx];
            byte minG = minPixels[centerIdx + 1];
            byte minR = minPixels[centerIdx + 2];

            // Inactive wireframe is dark slate (RGB 28, 34, 46)
            Assert.InRange(inactB, 40, 52);
            Assert.InRange(inactG, 30, 40);
            Assert.InRange(inactR, 24, 34);

            // Active wireframe is sapphire/azure blue (B=84, G=52, R=18)
            Assert.True(actB > inactB + 20, $"Active wireframe blue ({actB}) must be distinctly higher than inactive ({inactB})");

            // Minimized wireframe is ocean teal (B=62, G=59, R=13)
            Assert.True(minG > inactG + 15, $"Minimized wireframe green ({minG}) must be distinctly higher than inactive ({inactG})");
            Assert.True(minR < inactR, $"Minimized wireframe red ({minR}) must be lower than inactive ({inactR})");
        });

    [Fact]
    public void OverviewManagerTransitionsBetweenMotionAndSettledRestoringDetail()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var canvas = new Canvas();
            var api = new FakeWindowApi();
            var input = new FakeInputRouter();
            var clock = new FakeClock(1000);
            var dwm = new FakeDwmThumbnailApi();
            var config = new FakeAppConfig { EnableStartupIntro = false, LowQualityMode = false, SmoothZoom = true };

            api.AddWindow(source.Handle, 100, 100, 800, 550, title: "LOD Test App");
            canvas.SetWindow(source.Handle, 100, 100, 800, 550);

            using var wm = new WindowManager(canvas, api, config, input, clock);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: dwm);
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;

            // Initially stationary at resting state
            Assert.False(overview.MotionActive);

            // Start pan gesture -> MotionActive becomes true
            pass.OnMouseButtonDown!(pass, new MouseEventArgs(MouseButtons.Middle, 1, 200, 200, 0));
            Assert.True(overview.MotionActive);

            // Panning motion continues
            pass.OnMouseMoved!(pass, new MouseEventArgs(MouseButtons.Middle, 0, 260, 240, 0));
            Assert.True(overview.MotionActive);

            // Release pan after short pause -> Motion settles
            clock.Advance(50);
            pass.OnMouseButtonUp!(pass, new MouseEventArgs(MouseButtons.Middle, 1, 260, 240, 0));
            Assert.False(overview.MotionActive);
        });

    [Fact]
    public void PanMomentumCoastingAcrossZoomThresholdTogglesLodAndRestoresSettledDetail()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var canvas = new Canvas();
            var api = new FakeWindowApi();
            var input = new FakeInputRouter();
            var clock = new FakeClock(1000);
            var dwm = new FakeDwmThumbnailApi();
            var config = new FakeAppConfig { EnableStartupIntro = false, LowQualityMode = false, SmoothZoom = true };

            api.AddWindow(source.Handle, 100, 100, 800, 500, title: "LOD Coasting App");
            canvas.SetWindow(source.Handle, 100, 100, 800, 500);

            using var wm = new WindowManager(canvas, api, config, input, clock);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: dwm);
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;

            // 1. Kick off pan gesture with quick movement to trigger inertia momentum
            pass.OnMouseButtonDown!(pass, new MouseEventArgs(MouseButtons.Middle, 1, 500, 500, 0));
            clock.Advance(20); pass.OnMouseMoved!(pass, new MouseEventArgs(MouseButtons.Middle, 0, 540, 500, 0));
            clock.Advance(20); pass.OnMouseMoved!(pass, new MouseEventArgs(MouseButtons.Middle, 0, 580, 500, 0));
            pass.OnMouseButtonUp!(pass, new MouseEventArgs(MouseButtons.Middle, 1, 580, 500, 0));

            // Pan momentum is active
            Assert.True(overview.MomentumActive);
            Assert.True(overview.MotionActive);

            // While momentum is active:
            // At distant zoom 0.20, wireframe LOD is required
            Assert.True(PlaceholderAnnotationLayer.ShouldUseWireframeLod(overview.MotionActive, zoom: 0.20, projectedSize: 160));

            // Still in motion, if camera reaches zoom 0.30 with projectedSize 160 (>= 40), wireframe LOD is not forced
            Assert.False(PlaceholderAnnotationLayer.ShouldUseWireframeLod(overview.MotionActive, zoom: 0.30, projectedSize: 160));

            // 2. Advance clock until momentum settles
            clock.Advance(5000);
            overview.AdvanceMomentum();

            Assert.False(overview.MomentumActive);
            Assert.False(overview.MotionActive);

            // Once settled, even at distant zoom 0.20, wireframe LOD is inactive (full detail restored)
            Assert.False(PlaceholderAnnotationLayer.ShouldUseWireframeLod(overview.MotionActive, zoom: 0.20, projectedSize: 160));
        });

    [Fact]
    public void MixedDpiProjectionUsesTheSamePhysicalSizeForWireframes()
        => WorkspaceViewTests.OnSta(() =>
        {
            var card = new PlaceholderAnnotation((IntPtr)1, "Mixed DPI Card",
                new WorldRect { X = 0, Y = 0, W = 160, H = 100 });

            // Zoom = 0.30 (>= 0.25 threshold, so size threshold governs)
            // On Monitor 1 with 100% DPI (DpiScale = 1.0):
            // Projected size: 160 * 0.30 / 1.0 = 48.0 DIPs (>= 40px -> Standard motion, wireframe inactive)
            var transform96Dpi = new CanvasViewTransform(0, 0, 0.30, new CanvasRect(0, 0, 1920, 1080), dpiX: 1.0, dpiY: 1.0);
            var snapshot1 = OverviewAnnotationSnapshot.Create([], 0, 0, 0.30, new(0, 0, 1920, 1080), null, false,
                placeholders: [card], motionActive: true) with { SimplifyWindowCardsDuringMotion = true };
            var context96Dpi = new OverviewAnnotationContext(snapshot1, transform96Dpi, new DpiScale(1.0, 1.0), new System.Windows.Size(1920, 1080));
            var rect96 = context96Dpi.Project(card.Bounds);

            Assert.Equal(48.0, rect96.Width);
            Assert.False(PlaceholderAnnotationLayer.UsesWireframe(context96Dpi, card.Bounds));

            // On Monitor 2 with 200% DPI (DpiScale = 2.0):
            // The logical bounds shrink but the projected size remains 48 physical pixels.
            var transform192Dpi = new CanvasViewTransform(0, 0, 0.30, new CanvasRect(0, 0, 3840, 2160), dpiX: 2.0, dpiY: 2.0);
            var snapshot2 = OverviewAnnotationSnapshot.Create([], 0, 0, 0.30, new(0, 0, 3840, 2160), null, false,
                placeholders: [card], motionActive: true) with { SimplifyWindowCardsDuringMotion = true };
            var context192Dpi = new OverviewAnnotationContext(snapshot2, transform192Dpi, new DpiScale(2.0, 2.0), new System.Windows.Size(1920, 1080));
            var rect192 = context192Dpi.Project(card.Bounds);

            Assert.Equal(24.0, rect192.Width);
            Assert.False(PlaceholderAnnotationLayer.UsesWireframe(context192Dpi, card.Bounds));
        });

    [Fact]
    public void WireframeRenderingHandlesOccludersAndCardOpacityCleanly()
        => WorkspaceViewTests.OnSta(() =>
        {
            var occluder = new WorldRect { X = 50, Y = 50, W = 100, H = 100 };
            var card = new PlaceholderAnnotation((IntPtr)1, "Semi-transparent Occluded",
                new WorldRect { X = 100, Y = 100, W = 200, H = 200 },
                Opacity: 0.5, Occluders: [occluder]);

            RenderSurface(card, zoom: 0.10, motionActive: true, out var surface);
            Assert.Equal(1, surface.Placeholders.WireframeCardCount);
        });

    [Fact]
    public void InMotionWindowSmallerThan40PxWithDefaultImageOpacityRendersWireframe()
        => WorkspaceViewTests.OnSta(() =>
        {
            // Window is 50x50 in world space. At zoom 0.50 (>= 0.40 live preview threshold),
            // projected size is 25x25 (< 40px threshold).
            // ImageOpacity is null (defaults to 1 at zoom 0.50), Image is null.
            var card = new PlaceholderAnnotation((IntPtr)1, "Tiny Live Window",
                new WorldRect { X = 100, Y = 100, W = 50, H = 50 });

            RenderSurface(card, zoom: 0.50, motionActive: true, out var motionSurface);

            // It MUST be rendered as wireframe LOD because projected size is 25px < 40px!
            Assert.Equal(1, motionSurface.Placeholders.WireframeCardCount);
        });

    [Fact]
    public void ContinuousMotionTogglingLodAcrossZoomThresholdsAndRestoringSettledRestingFidelity()
        => WorkspaceViewTests.OnSta(() =>
        {
            var logo = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255 }, 4);
            logo.Freeze();

            var card = new PlaceholderAnnotation((IntPtr)1, "Dynamic Zoom Test Card",
                new WorldRect { X = 50, Y = 50, W = 400, H = 300 },
                ImageOpacity: 0, LabelOpacity: 1, Logo: logo);

            // Phase 1: In motion at close zoom (zoom = 0.50, projected = 200x150 >= 40px)
            RenderSurface(card, zoom: 0.50, motionActive: true, out var closeMotionSurface);
            Assert.Equal(0, closeMotionSurface.Placeholders.WireframeCardCount);

            // Phase 2: In motion at distant zoom (zoom = 0.15 < 0.25 threshold)
            var distantMotionPixels = RenderSurface(card, zoom: 0.15, motionActive: true, out var distantMotionSurface);
            Assert.Equal(1, distantMotionSurface.Placeholders.WireframeCardCount);
            Assert.Empty(distantMotionSurface.Placeholders.Labels);
            var redInMotion = Enumerable.Range(0, distantMotionPixels.Length / 4)
                .Where(i => distantMotionPixels[i * 4 + 2] > 240 && distantMotionPixels[i * 4 + 1] < 10 && distantMotionPixels[i * 4] < 10)
                .ToArray();
            Assert.Empty(redInMotion);

            // Phase 3: In motion zooming back in (zoom = 0.60 >= 0.25 threshold)
            RenderSurface(card, zoom: 0.60, motionActive: true, out var zoomedInMotionSurface);
            Assert.Equal(0, zoomedInMotionSurface.Placeholders.WireframeCardCount);

            // Phase 4: Settle camera motion at distant zoom (zoom = 0.15, motionActive = false)
            var settledPixels = RenderSurface(card, zoom: 0.15, motionActive: false, out var settledSurface);
            Assert.Equal(0, settledSurface.Placeholders.WireframeCardCount);
            var redSettled = Enumerable.Range(0, settledPixels.Length / 4)
                .Where(i => settledPixels[i * 4 + 2] > 240 && settledPixels[i * 4 + 1] < 10 && settledPixels[i * 4] < 10)
                .ToArray();
            Assert.NotEmpty(redSettled);
        });

    [Fact]
    public void MixedProjectedSizesInSameFrameDuringMotionSelectivelyAppliesLod()
        => WorkspaceViewTests.OnSta(() =>
        {
            // At zoom = 0.30 (>= 0.25 threshold):
            // Large card: 400x300 world -> projected 120x90 DIPs (>= 40px)
            // Small card: 50x50 world -> projected 15x15 DIPs (< 40px)
            var largeCard = new PlaceholderAnnotation((IntPtr)101, "Large Window",
                new WorldRect { X = 10, Y = 10, W = 400, H = 300 },
                ImageOpacity: 0, LabelOpacity: 1);
            var smallCard = new PlaceholderAnnotation((IntPtr)102, "Small Window",
                new WorldRect { X = 500, Y = 10, W = 50, H = 50 },
                ImageOpacity: 0, LabelOpacity: 1);

            // In Motion:
            RenderSurface([largeCard, smallCard], zoom: 0.30, motionActive: true, out var motionSurface, width: 800, height: 600);
            Assert.Equal(1, motionSurface.Placeholders.WireframeCardCount);
            // Large window retains its label, while small window bypasses label
            Assert.Single(motionSurface.Placeholders.Labels);
            Assert.Equal((IntPtr)101, motionSurface.Placeholders.Labels[0].Card.Handle);

            // Settled Rest:
            RenderSurface([largeCard, smallCard], zoom: 0.30, motionActive: false, out var settledSurface, width: 800, height: 600);
            Assert.Equal(0, settledSurface.Placeholders.WireframeCardCount);
            // Both windows restore their resting labels
            Assert.Equal(2, settledSurface.Placeholders.Labels.Count);
        });

    [Fact]
    public void RapidMotionStateChurnDoesNotLeakWireframeCountsOrLabels()
        => WorkspaceViewTests.OnSta(() =>
        {
            var card = new PlaceholderAnnotation((IntPtr)201, "Rapid Churn Card",
                new WorldRect { X = 50, Y = 50, W = 100, H = 100 },
                ImageOpacity: 0, LabelOpacity: 1);

            var surface = new OverviewAnnotationSurface();

            for (int i = 0; i < 5; i++)
            {
                // In motion at distant zoom (0.10)
                var motionSnapshot = OverviewAnnotationSnapshot.Create([], 0, 0, 0.10, new(0, 0, 400, 300),
                    null, false, placeholders: [card], showHints: false, motionActive: true)
                    with { SimplifyWindowCardsDuringMotion = true };
                surface.Update(motionSnapshot);
                surface.Measure(new System.Windows.Size(400, 300));
                surface.Arrange(new Rect(0, 0, 400, 300));
                var motionBmp = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
                motionBmp.Render(surface);

                Assert.Equal(1, surface.Placeholders.WireframeCardCount);
                Assert.Empty(surface.Placeholders.Labels);

                // Settle motion at same zoom
                var restingSnapshot = OverviewAnnotationSnapshot.Create([], 0, 0, 0.10, new(0, 0, 400, 300),
                    null, false, placeholders: [card], showHints: false, motionActive: false);
                surface.Update(restingSnapshot);
                surface.Measure(new System.Windows.Size(400, 300));
                surface.Arrange(new Rect(0, 0, 400, 300));
                var restingBmp = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
                restingBmp.Render(surface);

                Assert.Equal(0, surface.Placeholders.WireframeCardCount);
                Assert.Single(surface.Placeholders.Labels);
            }
        });
}

