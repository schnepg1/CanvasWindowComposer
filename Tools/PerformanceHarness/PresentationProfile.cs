using System.Globalization;

namespace CanvasDesktop.PerformanceHarness;

internal sealed record PresentationProfile(bool Only, double Zoom)
{
    public static readonly string[] Phases = ["StationaryOverview", "OverviewPan", "ZoomBursts", "WindowDrag", "Panning"];

    public static PresentationProfile Parse(string[] args)
    {
        bool only = args.Contains("--presentation-only");
        if (only && args.Contains("--skip-overview")) throw new ArgumentException("Presentation-only requires overview.");
        string zoomText = args.FirstOrDefault(a => a.StartsWith("--overview-zoom=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "1";
        double zoom = double.Parse(zoomText, CultureInfo.InvariantCulture);
        if (!double.IsFinite(zoom) || zoom < .05 || zoom > 1) throw new ArgumentException("Use overview-zoom 0.05..1.");
        return new(only, zoom);
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
