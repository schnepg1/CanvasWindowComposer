using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

// Observe the current desktop and apply a batch of conservative matches.
internal sealed partial class WorkspaceService
{
    private void Capture()
    {
        InvalidateOverviewSnapshot();
        _ambiguous.Clear();
        _folders = _identity.ReadExplorerFolders();
        SynchronizeCapturedCards();
        ValidateObservedWindows();
        EnrollUntrackedWindows();
        ApplyCaptureMatches(CollectPendingCaptures());
        FinishTemporaryCapture();
        Document.Cameras[_desktop] = new WorkspaceCamera { X = _canvas.CamX, Y = _canvas.CamY, Zoom = _canvas.Zoom };
    }

    private sealed record PendingWindowCapture(WorkspaceLiveWindow Live, WorkspaceMatchInput Input,
        WorldRect Bounds, WorkspaceWindowMetadata Metadata);

    private void SynchronizeCapturedCards()
    {
        // A dragged proxy and its live minimized/sleeping window share one saved
        // place. Reconcile proxy geometry before reading the live bindings.
        foreach (var (id, card) in _cards.ToArray())
            if (Document.Items.FirstOrDefault(i => i.Id == id) is { } item) SyncCardGeometry(item, card);
    }

    private void ValidateObservedWindows()
    {
        // Validate before reading canvas geometry. A recycled HWND must not write
        // the previous window's cached geometry into either saved slot.
        foreach (var live in _bindings.Windows.ToArray())
        {
            var current = _windowInspector.Inspect(live.Identity.Handle);
            if (current == null)
            {
                _bindings.Unbind(live); live.NeedsNativeGeometry = true;
                if (_api.GetWindowProcessId(live.Identity.Handle) == 0 && !_canvas.HasWindow(live.Identity.Handle))
                    _bindings.Remove(live.Identity.Handle);
                continue;
            }
            if (current.Value.Identity != live.Identity)
            {
                _bindings.Observe(current.Value.Identity, out _);
            }
            else if (!_api.IsWindowVisible(live.Identity.Handle)
                && Document.Items.Any(i => i.Id == live.ItemId && i.DesktopId == _desktop)) _bindings.Unbind(live);
        }
    }

    private void EnrollUntrackedWindows()
    {
        // The normal canvas intentionally excludes initially maximized/minimized
        // windows. Enroll these without moving them so the workspace saves them too.
        _api.EnumWindows(handle =>
        {
            if (_canvas.HasWindow(handle) || !_desktops.IsOnCurrentDesktop(handle)
                || !_api.IsManageable(handle, (uint)Environment.ProcessId, true, true)) return true;
            EnrollNativeGeometry(handle);
            return true;
        });
    }

    private List<PendingWindowCapture> CollectPendingCaptures()
    {
        var pending = new List<PendingWindowCapture>();
        foreach (var (handle, cachedWorld) in _canvas.Windows.ToArray())
        {
            var world = cachedWorld;
            var existingCard = _cards.FirstOrDefault(p => p.Value.Handle == handle);
            if (existingCard.Value != null)
            {
                var saved = Document.Items.FirstOrDefault(i => i.Id == existingCard.Key);
                if (saved != null && WorkspaceGeometry.ValidBounds(world))
                {
                    var bounds = world;
                    // A card is always normal; retain the application's saved state.
                    bounds.State = saved.Bounds.State;
                    bounds.PinnedToScreen = saved.Bounds.PinnedToScreen;
                    saved.Bounds = bounds;
                }
                continue;
            }
            if (!_desktops.IsOnCurrentDesktop(handle) || !_api.IsWindowVisible(handle)) continue;
            var observation = _windowInspector.Inspect(handle);
            if (observation == null) continue;
            var live = _bindings.Observe(observation.Value.Identity, out _);
            if (_capturingStartup) live.StartupArrival = true;
            if (live.NeedsNativeGeometry)
            {
                EnrollNativeGeometry(handle);
                world = _canvas.Windows[handle];
                live.NeedsNativeGeometry = false;
            }
            // Polling also covers a minimize event missed during startup. Canvas
            // parking is session ownership and must never become saved user intent.
            if ((_api.GetWindowStyle(handle) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0
                && !_wm.UsesCachedPreview(handle) && world.State != WindowState.Minimized)
            {
                world.State = WindowState.Minimized;
                _canvas.RestoreWindow(handle, world);
            }
            if (live.Forgotten) continue;
            var metadata = _windowInspector.ReadMetadata(handle, observation.Value.Exe, _folders);
            var exe = metadata.ExecutablePath;
            var title = metadata.Title;
            var chromeProfile = metadata.ChromeProfilePath;
            var target = metadata.Target;
            if (WorkspaceTargetIdentity.IsVsCode(exe) && target.Length == 0)
            {
                string? detected = _identity.ReadVsCodeTarget(live.Identity, exe, title);
                if (detected == null && live.NeedsInitialMatch && !HasDeliberatePlace(live))
                {
                    live.WaitingForProjectSince ??= _clock.TickCount64;
                    if (_clock.TickCount64 - live.WaitingForProjectSince.Value < 8000)
                    { StageTemporary(live, metadata); continue; }
                }
                target = detected ?? "";
                if (detected != null) live.WaitingForProjectSince = null;
            }
            metadata = metadata with { Target = target };
            if (target.Length > 0) live.WaitingForProjectSince = null;
            if (metadata.IsExplorer && target.Length == 0 && live.NeedsInitialMatch && !HasDeliberatePlace(live))
            {
                live.WaitingForFolderSince ??= _clock.TickCount64;
                if (_clock.TickCount64 - live.WaitingForFolderSince.Value < 8000)
                { StageTemporary(live, metadata); continue; }
            }
            if (live.ItemId is { } id)
            {
                var old = Document.Items.FirstOrDefault(i => i.Id == id);
                if (old != null && WorkspaceAppIdentity.Same(old.ExecutablePath, exe)
                    && (old.ChromeProfilePath.Length == 0 || chromeProfile == null
                        || string.Equals(old.ChromeProfilePath, chromeProfile, StringComparison.OrdinalIgnoreCase)))
                {
                    if (WorkspaceGeometry.ValidBounds(world)) old.Bounds = world;
                    old.Title = title;
                    old.ExecutablePath = exe;
                    metadata.CaptureProfileInto(old);
                    // Explorer navigation updates this slot's saved folder.
                    metadata.CaptureTargetInto(old);
                    if (old.DesktopId != _desktop) Zones.ForgetItem(old.Id);
                    old.DesktopId = _desktop;
                    if (old.Placement == WorkspacePlacement.Deliberate || !live.NeedsInitialMatch) continue;
                }
                else _bindings.Unbind(live);
            }
            Guid? requested = _launching.Keys.FirstOrDefault(key => Document.Items.Any(i => i.Id == key
                && i.DesktopId == _desktop && WorkspaceAppIdentity.Same(i.ExecutablePath, exe))
                && _launchBaselines.TryGetValue(key, out var baseline) && !baseline.Contains(handle));
            var input = new WorkspaceMatchInput(live.Token, exe, title, target, requested == Guid.Empty ? null : requested)
            {
                ExistingLaunches = _launching.Keys.Where(key => _launchBaselines.TryGetValue(key, out var baseline)
                    && baseline.Contains(handle)).ToArray(),
                ChromeProfilePath = chromeProfile
            };
            pending.Add(new(live, input, world, metadata));
        }
        return pending;
    }

    private void ApplyCaptureMatches(IReadOnlyList<PendingWindowCapture> pending)
    {
        var plan = WorkspaceMatcher.Plan(Document.Items.Where(i => i.Placement == WorkspacePlacement.Deliberate).ToArray(),
            _desktop, pending.Select(p => p.Input).ToArray(), _bindings.BoundIds);
        foreach (var (live, input, world, metadata) in pending)
        {
            var match = plan[live.Token];
            if (match.IsAmbiguous)
            {
                _ambiguous.UnionWith(match.AmbiguousIds);
                live.NeedsInitialMatch = false;
                StageTemporary(live, metadata);
                continue; // Keep saved places intact; no guessed identity.
            }
            if (!StillCurrent(live)) continue;
            var handle = live.Identity.Handle;
            var item = match.Item;
            if (item != null && (_launching.ContainsKey(item.Id) || _launchMessages.ContainsKey(item.Id))
                && !CanFinishLaunch(item, live, input)) continue;
            if (item == null && !_migrationWindows.Contains(live.Identity))
            {
                live.NeedsInitialMatch = false;
                StageTemporary(live, metadata);
                continue;
            }
            bool restore = item != null;
            if (restore) DetachTemporary(live);
            if (item == null)
            {
                if (!WorkspaceGeometry.ValidBounds(world) || Document.Items.Count >= 2000) continue;
                item = metadata.CreateItem(_desktop, world, WorkspacePlacement.Deliberate);
                Document.Items.Add(item);
            }
            metadata.CaptureProfileInto(item);
            if (_bindings.Bind(live, item.Id))
            {
                item.ExecutablePath = input.ExecutablePath;
                metadata.CaptureTargetInto(item);
                if (restore) RestoreBinding(live, item);
            }
        }
    }
}
