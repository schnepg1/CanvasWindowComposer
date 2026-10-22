using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace CanvasDesktop;

internal enum OverviewCardMode { Simple, Detailed, Live }
internal sealed record OverviewCardInput(Guid Id, PlaceholderAnnotation Card, OverviewCardMode Mode, bool LiveReady = true);

/// <summary>Visible-only, UI-thread transition state. Images are immutable borrowed snapshots.</summary>
internal sealed class OverviewCardTransitions
{
    internal const long DurationMs = 150;
    private sealed class Entry
    {
        public OverviewCardMode Target;
        public double Detail, Live, FromDetail, FromLive;
        public long Started;
        public ImageSource? Image;
        public string Title = "";
        public bool Moving;
    }
    private readonly Dictionary<Guid, Entry> _entries = new();
    public bool IsAnimating => _entries.Values.Any(e => e.Moving);
    public bool HasPendingLiveSources { get; private set; }
    internal int RetainedImages => _entries.Values.Count(e => e.Image != null);

    public bool NeedsLiveUnderlay(Guid id) => _entries.TryGetValue(id, out var e) && e.Live > 0;

    public IReadOnlyList<PlaceholderAnnotation> Update(IReadOnlyList<OverviewCardInput> inputs, long now)
    {
        HasPendingLiveSources = false;
        var seen = new HashSet<Guid>();
        var result = new List<PlaceholderAnnotation>();
        foreach (var input in inputs)
        {
            seen.Add(input.Id);
            if (!_entries.TryGetValue(input.Id, out var e))
            {
                e = new Entry { Target = input.Mode, Detail = input.Mode == OverviewCardMode.Detailed ? 1 : 0,
                    Live = input.Mode == OverviewCardMode.Live ? 1 : 0 };
                _entries.Add(input.Id, e);
            }
            Advance(e, now);
            if (input.Card.Image != null) e.Image = input.Card.Image;
            if (input.Card.Title.Length > 0) e.Title = input.Card.Title;
            // If a native window has already been manually minimized there are
            // no underlying pixels to fade from. Show its card without a hole.
            if (input.Mode != OverviewCardMode.Live && !input.LiveReady && e.Live > 0)
            {
                e.Target = input.Mode; e.Live = 0;
                e.Detail = input.Mode == OverviewCardMode.Detailed ? 1 : 0;
                e.Moving = false;
            }
            // A restored window remains covered until its live DWM source is registered.
            if (input.Mode != e.Target && (input.Mode != OverviewCardMode.Live || input.LiveReady))
            {
                e.FromDetail = e.Detail; e.FromLive = e.Live;
                e.Target = input.Mode; e.Started = now; e.Moving = true;
            }
            if (!e.Moving && e.Target != OverviewCardMode.Detailed) e.Image = null;
            if (input.Mode == OverviewCardMode.Live && !input.LiveReady && e.Live < 1)
                HasPendingLiveSources = true;
            double cover = 1 - e.Live;
            if (cover <= 0) continue;
            double detail = Math.Clamp(e.Detail / cover, 0, 1);
            // Stagger the two title treatments through a plain rectangle so
            // the bitmap's title and the simplified title never overlap.
            double imageOpacity = Math.Max(0, detail * 2 - 1);
            double labelOpacity = Math.Max(0, 1 - detail * 2);
            result.Add(input.Card with { Title = e.Title, Image = e.Image, Opacity = cover,
                ImageOpacity = imageOpacity, LabelOpacity = labelOpacity });
        }
        foreach (var id in _entries.Keys.Where(id => !seen.Contains(id)).ToArray()) _entries.Remove(id);
        return result;
    }

    private static void Advance(Entry e, long now)
    {
        if (!e.Moving) return;
        double t = Math.Clamp((double)(now - e.Started) / DurationMs, 0, 1);
        double eased = t * t * (3 - 2 * t);
        e.Detail = e.FromDetail + ((e.Target == OverviewCardMode.Detailed ? 1 : 0) - e.FromDetail) * eased;
        e.Live = e.FromLive + ((e.Target == OverviewCardMode.Live ? 1 : 0) - e.FromLive) * eased;
        if (t >= 1) e.Moving = false;
    }

    public void Clear()
    {
        _entries.Clear();
        HasPendingLiveSources = false;
    }
}
