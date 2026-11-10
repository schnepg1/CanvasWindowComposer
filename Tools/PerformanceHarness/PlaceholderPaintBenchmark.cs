using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop.PerformanceHarness;

/// <summary>Measures CPU work to record real placeholder drawing commands without showing a window.</summary>
internal static class PlaceholderPaintBenchmark
{
    public static void Run(string output)
    {
        const int count = 96, frames = 120, warmup = 40;
        var screen = new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var logo = new DrawingImage(new GeometryDrawing(System.Windows.Media.Brushes.CornflowerBlue, null,
            new EllipseGeometry(new Rect(0, 0, 32, 32))));
        logo.Freeze();
        var results = new List<object>();
        foreach (var (name, spacing) in new[] { ("Separated", 1.2), ("Overlapping", .65), ("Stacked", .06) })
        {
            var bounds = Enumerable.Range(0, count).Select(i => new WorldRect
            {
                X = 100 + i % 12 * 220 * spacing, Y = 100 + i / 12 * 160 * spacing,
                W = 220, H = 160
            }).ToArray();
            var cards = bounds.Select((rect, i) => new PlaceholderAnnotation((IntPtr)(i + 1),
                $"Saved window {i + 1}", rect, Occluders: new ArraySegment<WorldRect>(bounds, 0, i),
                ImageOpacity: 0, LabelOpacity: 1, Logo: i % 3 == 0 ? logo : null,
                LogoBounds: i % 3 == 0 ? new Rect(8, 8, 32, 32) : null,
                PeekProgress: i == count - 1 ? .5 : null)).ToArray();
            var layer = new PlaceholderAnnotationLayer();
            var times = new double[frames];
            var bytes = new double[frames];
            for (int i = -warmup; i < frames; i++)
            {
                double phase = i * .07;
                double x = 40 * Math.Sin(phase), y = 30 * Math.Cos(phase), zoom = .45 + .025 * Math.Sin(phase);
                var frame = OverviewAnnotationSnapshot.Create([], x, y, zoom, screen, null, false,
                    placeholders: cards, motionActive: true);
                var view = new CanvasDesktop.CanvasView.CanvasViewTransform(x, y, zoom, new(0, 0, 1920, 1080), 1, 1);
                var context = new OverviewAnnotationContext(frame, view, new DpiScale(1, 1), new System.Windows.Size(1920, 1080));
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                var drawing = new DrawingGroup();
                using (var dc = drawing.Open()) layer.Draw(dc, context);
                if (i < 0) continue;
                times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                bytes[i] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            }
            results.Add(new { Layout = name, DrawMs = TimingSummary.From(times), Bytes = TimingSummary.From(bytes) });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { Cards = count, Frames = frames, Results = results },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
