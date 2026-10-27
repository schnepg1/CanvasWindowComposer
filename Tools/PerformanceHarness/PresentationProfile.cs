using System.Globalization;

namespace CanvasDesktop.PerformanceHarness;

internal sealed record PresentationProfile(bool Only, double Zoom, bool DesktopPanMomentum = true, bool CanvasPanMomentum = true)
{
    public static readonly string[] Phases = ["StationaryOverview", "OverviewPan", "ZoomBursts", "WindowDrag", "Panning", "CanvasPanMomentum", "DesktopPanMomentum"];

    public static PresentationProfile Parse(string[] args)
    {
        bool only = args.Contains("--presentation-only");
        if (only && args.Contains("--skip-overview")) throw new ArgumentException("Presentation-only requires overview.");
        string zoomText = args.FirstOrDefault(a => a.StartsWith("--overview-zoom=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "1";
        double zoom = double.Parse(zoomText, CultureInfo.InvariantCulture);
        if (!double.IsFinite(zoom) || zoom < .05 || zoom > 1) throw new ArgumentException("Use overview-zoom 0.05..1.");
        return new(only, zoom, !args.Contains("--no-desktop-momentum"), !args.Contains("--no-canvas-momentum"));
    }

    public static bool IsOverview(string phase) => Phases.Contains(phase);

    public static int WheelDelta(double current, double target)
        => (int)Math.Round(1200 * (target / current - 1));
}

internal sealed record TimingSummary(int Samples, double? P50, double? P95, double? P99, double? Max)
{
    public static TimingSummary From(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return new(0, null, null, null, null);
        var sorted = samples.Order().ToArray();
        double At(double p) => sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * p) - 1)];
        return new(sorted.Length, At(.5), At(.95), At(.99), sorted[^1]);
    }
}

// Sample a fixed trajectory rather than sending another wheel delta on every
// pump iteration. Slower backends catch up to the current target without adding
// a stale-input backlog; idle portions do not keep the gesture active.
internal sealed class PresentationZoomBursts(double baseZoom)
{
    private int _step = -1;

    public double? NextTarget(double elapsed)
    {
        int cycle = (int)(elapsed / .5);
        int index = Math.Min(2, (int)((elapsed - cycle * .5) / .04));
        int step = cycle * 3 + index;
        if (step == _step) return null;
        _step = step;
        int depth = cycle % 2 == 0 ? index + 1 : 2 - index;
        return Math.Clamp(baseZoom * (1 - .025 * depth), .05, 1);
    }
}

internal enum PresentationPanAction { Begin, Move, Release }
internal readonly record struct PresentationPanStep(PresentationPanAction Action, int Cycle, int X, int Y);

// One 200 ms held drag followed by idle time for the real production momentum
// timer. Targets follow elapsed time; missed cycles are not replayed as a burst.
internal sealed class PresentationPanGesture
{
    private int _cycle = -1, _x, _y;
    private bool _held;
    public IReadOnlyList<PresentationPanStep> Next(double elapsed)
    {
        int cycle = (int)(elapsed / 2);
        double phase = elapsed - cycle * 2;
        var steps = new List<PresentationPanStep>(3);
        if (_held && (cycle != _cycle || phase >= .2))
        {
            Move(1);
            steps.Add(new(PresentationPanAction.Release, _cycle, _x, _y));
            _held = false;
        }
        if (cycle != _cycle && phase < .2)
        {
            _cycle = cycle; _x = _y = 0; _held = true;
            steps.Add(new(PresentationPanAction.Begin, cycle, 0, 0));
        }
        if (_held) Move(phase / .2);
        return steps;

        void Move(double progress)
        {
            int direction = _cycle % 2 == 0 ? 1 : -1;
            int x = (int)Math.Round(120 * progress) * direction;
            int y = (int)Math.Round(60 * progress) * direction;
            if (x == _x && y == _y) return;
            _x = x; _y = y;
            steps.Add(new(PresentationPanAction.Move, _cycle, x, y));
        }
    }
}
