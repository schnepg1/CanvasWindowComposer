using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal enum WorkspaceMatchStrength { None, SingleApp, Title, Profile, Target, Launch }

internal sealed record WorkspaceMatch(WorkspaceItem? Item, IReadOnlyList<Guid> AmbiguousIds,
    WorkspaceMatchStrength Strength = WorkspaceMatchStrength.None)
{
    public bool IsAmbiguous => AmbiguousIds.Count > 0;
    public static WorkspaceMatch None { get; } = new(null, Array.Empty<Guid>());
    public static WorkspaceMatch Found(WorkspaceItem item, WorkspaceMatchStrength strength)
        => new(item, Array.Empty<Guid>(), strength);
    public static WorkspaceMatch Ambiguous(IEnumerable<WorkspaceItem> items)
        => new(null, items.Select(i => i.Id).ToArray());
}

internal sealed record WorkspaceMatchInput(Guid Token, string ExecutablePath, string Title, string Target, Guid? Requested)
{
    public IReadOnlyCollection<Guid> ExistingLaunches { get; init; } = Array.Empty<Guid>();
    public string? ChromeProfilePath { get; init; }
}

/// <summary>Pure policy: neither saved duplicates nor competing live windows are first-match-wins.</summary>
internal static class WorkspaceMatcher
{
    public static WorkspaceItem? Match(IReadOnlyCollection<WorkspaceItem> items, Guid desktop,
        string exe, string title, string target, IEnumerable<Guid> bound, Guid? requested = null)
        => Evaluate(items, desktop, exe, title, target, bound, requested).Item;

    public static WorkspaceMatch Evaluate(IReadOnlyCollection<WorkspaceItem> items, Guid desktop,
        string exe, string title, string target, IEnumerable<Guid> bound, Guid? requested = null, string? chromeProfilePath = null)
    {
        var used = new HashSet<Guid>(bound);
        var appItems = items.Where(i => i.DesktopId == desktop
            && string.Equals(i.ExecutablePath, exe, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ChromeProfiles.Supports(exe))
        {
            var configured = appItems.Where(i => i.ChromeProfilePath.Length > 0).ToArray();
            // Neither a caption nor launch timing proves a Chrome profile.
            if (chromeProfilePath == null && configured.Any(i => !used.Contains(i.Id)))
                return WorkspaceMatch.Ambiguous(configured.Where(i => !used.Contains(i.Id)));
            appItems = appItems.Where(i => i.ChromeProfilePath.Length == 0
                || string.Equals(i.ChromeProfilePath, chromeProfilePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        var available = appItems.Where(i => !used.Contains(i.Id)).ToArray();
        // The coordinator supplies launch intent only for a NEW runtime window.
        var chosen = available.SingleOrDefault(i => i.Id == requested);
        if (chosen != null && (target.Length > 0
                ? string.Equals(chosen.Target, target, StringComparison.OrdinalIgnoreCase)
                : !chosen.IsFolder)) return WorkspaceMatch.Found(chosen, WorkspaceMatchStrength.Launch);

        var profiled = appItems.Where(i => i.ChromeProfilePath.Length > 0
            && string.Equals(i.ChromeProfilePath, chromeProfilePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (profiled.Length == 1 && !used.Contains(profiled[0].Id))
            return WorkspaceMatch.Found(profiled[0], WorkspaceMatchStrength.Profile);
        if (profiled.Length > 0) { appItems = profiled; available = profiled.Where(i => !used.Contains(i.Id)).ToArray(); }

        var exact = appItems.Where(i => target.Length > 0
            ? string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase)
            : i.Target.Length == 0 && i.Title == title).ToArray();
        var remaining = exact.Where(i => !used.Contains(i.Id)).ToArray();
        // A previously bound duplicate does not make the remaining identity unique.
        if (exact.Length > 1 && remaining.Length > 0) return WorkspaceMatch.Ambiguous(remaining);
        if (remaining.Length == 1) return WorkspaceMatch.Found(remaining[0],
            target.Length > 0 ? WorkspaceMatchStrength.Target : WorkspaceMatchStrength.Title);
        if (target.Length == 0 && !requested.HasValue && appItems.Length == 1
            && available.Length == 1 && available[0].Target.Length == 0)
            return WorkspaceMatch.Found(available[0], WorkspaceMatchStrength.SingleApp);
        if (exact.Length == 0 && target.Length == 0)
        {
            var untargeted = available.Where(i => i.Target.Length == 0).ToArray();
            if (untargeted.Length > 0) return WorkspaceMatch.Ambiguous(untargeted);
        }
        return WorkspaceMatch.None;
    }

    public static IReadOnlyDictionary<Guid, WorkspaceMatch> Plan(IReadOnlyCollection<WorkspaceItem> items,
        Guid desktop, IReadOnlyCollection<WorkspaceMatchInput> windows, IEnumerable<Guid> bound)
    {
        var used = bound.ToArray();
        var results = windows.ToDictionary(w => w.Token,
            w => Evaluate(items, desktop, w.ExecutablePath, w.Title, w.Target, used, w.Requested, w.ChromeProfilePath));
        foreach (var window in windows)
            if (results[window.Token].Item is { } item && window.ExistingLaunches.Contains(item.Id))
                results[window.Token] = WorkspaceMatch.Ambiguous(new[] { item });
        // Resolve the batch before assigning anything: enumeration order cannot
        // choose a winner when two live windows claim one saved slot.
        foreach (var group in results.Where(p => p.Value.Item != null).GroupBy(p => p.Value.Item!.Id).ToArray())
            if (group.Count() > 1)
            {
                var strongest = group.Max(p => p.Value.Strength);
                bool uniqueWinner = group.Count(p => p.Value.Strength == strongest) == 1;
                foreach (var pair in group)
                    if (!uniqueWinner) results[pair.Key] = WorkspaceMatch.Ambiguous(new[] { pair.Value.Item! });
                    else if (pair.Value.Strength != strongest) results[pair.Key] = WorkspaceMatch.None;
            }
        return results;
    }
}
