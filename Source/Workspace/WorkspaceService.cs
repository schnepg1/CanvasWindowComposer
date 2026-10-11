using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CanvasDesktop;

internal sealed partial class WorkspaceService : IWorkspaceCommands, IDisposable
{
    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly IWindowApi _api;
    private readonly IVirtualDesktops _desktops;
    private readonly DesktopStateCache _cache;
    private readonly WorkspacePersistence _persistence;
    private readonly IWorkspacePlaceholderFactory _placeholderFactory;
    private readonly IWorkspaceWindowActions _windowActions;
    private readonly IClock _clock;
    private readonly IWorkspaceLauncher _launcher;
    private readonly IWorkspaceIdentity _identity;
    private readonly WorkspaceBindings _bindings = new();
    private readonly Dictionary<Guid, IWorkspacePlaceholder> _cards = new();
    private readonly Dictionary<Guid, long> _launching = new();
    private readonly Dictionary<Guid, HashSet<IntPtr>> _launchBaselines = new();
    private readonly HashSet<Guid> _ambiguous = new();
    private readonly HashSet<Guid> _queued = new();
    private readonly HashSet<Guid> _startupAttempted = new();
    private Dictionary<IntPtr, string> _folders = new();
    private Guid _desktop;
    private long _nextCapture;
    private bool _switching, _disposed, _started;
    private string? _status;
    public WorkspaceDocument Document => _persistence.Document;
    public ZoneService Zones { get; }
    public IReadOnlyList<WorkspaceItemView> Items => Document.Items.Select(View).ToArray();
    public string? Status => _persistence.LastError ?? _status ?? _persistence.LoadWarning
        ?? (_ambiguous.Count > 0 ? "Some saved places need linking. Use 'Link window…' to choose the correct open window." : null);
    public event Action? Changed;
    public event Action<string>? LaunchFailed;

    // Only actual cards on the current desktop are annotated. Their geometry is
    // supplied by the overview, so labels follow a drag before the next save.
    public IReadOnlyDictionary<IntPtr, string> GetPlaceholderTitles()
    {
        var titles = new Dictionary<IntPtr, string>();
        foreach (var item in Document.Items)
            if (_cards.TryGetValue(item.Id, out var card))
                titles[card.Handle] = item.Name.Length > 0 ? item.Name : item.Title;
        return titles;
    }

    public WorkspaceService(Canvas canvas, WindowManager wm, IWindowApi api,
        IVirtualDesktops desktops, DesktopStateCache cache, IWorkspaceStore store,
        IWorkspacePlaceholderFactory placeholders, IWorkspaceWindowActions windowActions,
        IWorkspaceLauncher launcher, IWorkspaceIdentity identity, IClock clock)
    {
        _canvas = canvas; _wm = wm; _api = api; _desktops = desktops; _cache = cache;
        _desktop = desktops.CurrentDesktopId;
        _launcher = launcher; _identity = identity; _clock = clock;
        _placeholderFactory = placeholders; _windowActions = windowActions;
        _persistence = new WorkspacePersistence(store);
        Zones = new ZoneService(Document, () => _desktop, Save);
        _wm.WindowRemoving += OnRemoving;
        _cache.BeforeStateSaved += BeforeSwitch;
        _cache.AfterStateLoaded += AfterSwitch;
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        RestoreCamera();
        _wm.DiscoverNewWindows();
        Capture();
        EnsureCards();
        OpenStartupItems();
        Save();
    }

    public void Tick()
    {
        if (!_started || _disposed || _switching || _clock.TickCount64 < _nextCapture) return;
        _nextCapture = _clock.TickCount64 + 2000;
        foreach (var id in _launching.Where(p => p.Value < _clock.TickCount64).Select(p => p.Key).ToArray())
        {
            _launching.Remove(id);
            _launchBaselines.Remove(id);
            _status = "No matching window appeared. The placeholder is kept; you can retry or choose a launch target.";
        }
        Capture();
        EnsureCards();
        foreach (var id in _queued.ToArray())
        {
            var item = Document.Items.FirstOrDefault(i => i.Id == id);
            if (item == null || IsOpen(item)) { _queued.Remove(id); continue; }
            if (item.DesktopId == _desktop && !IsLaunchingApp(item.ExecutablePath))
            { _queued.Remove(id); Open(item); }
        }
        Save();
        Changed?.Invoke();
    }

    private void RestoreCamera()
    {
        if (Document.Cameras.TryGetValue(_desktop, out var c))
            _canvas.LoadState(new CanvasState { CamX = c.X, CamY = c.Y, Zoom = c.Zoom,
                Windows = new Dictionary<IntPtr, WorldRect>(_canvas.Windows) });
    }

    private void BeforeSwitch()
    {
        if (!_started || _disposed) return;
        // The virtual-desktop service has already reported the new desktop.
        // Snapshot the old canvas before querying only the newly active desktop.
        foreach (var (handle, world) in _canvas.Windows)
        {
            var live = ValidBinding(handle);
            var id = live?.ItemId is { } bound ? bound
                : _cards.FirstOrDefault(p => p.Value.Handle == handle).Key;
            var item = Document.Items.FirstOrDefault(i => i.Id == id);
            if (item != null && WorkspaceGeometry.ValidBounds(world))
            {
                var saved = world;
                if (_cards.ContainsKey(id)) { saved.State = item.Bounds.State; saved.PinnedToScreen = item.Bounds.PinnedToScreen; }
                item.Bounds = saved;
            }
        }
        Document.Cameras[_desktop] = new WorkspaceCamera { X = _canvas.CamX, Y = _canvas.CamY, Zoom = _canvas.Zoom };
        Save();
        _switching = true;
        // Remove our own HWNDs from the in-memory cache before it snapshots.
        // Real windows on other desktops keep their bindings.
        foreach (var id in _cards.Keys.ToArray()) RemoveCard(id);
    }

    private void AfterSwitch()
    {
        if (!_started || _disposed) return;
        if (_desktop == Guid.Empty && _desktops.CurrentDesktopId != Guid.Empty)
        {
            // Initial desktop detection may complete after startup.
            foreach (var item in Document.Items.Where(i => i.DesktopId == Guid.Empty))
                item.DesktopId = _desktops.CurrentDesktopId;
            foreach (var zone in Document.Zones.Where(i => i.DesktopId == Guid.Empty))
                zone.DesktopId = _desktops.CurrentDesktopId;
            if (Document.Cameras.Remove(Guid.Empty, out var camera))
                Document.Cameras[_desktops.CurrentDesktopId] = camera;
        }
        _desktop = _desktops.CurrentDesktopId;
        Zones.NotifyDesktopChanged();
        _switching = false;
        RestoreCamera();
        Capture();
        EnsureCards();
        OpenStartupItems();
    }

    private void OpenStartupItems()
    {
        foreach (var item in Document.Items.Where(i => i.DesktopId == _desktop && i.OpenOnStartup).ToArray())
            if (_startupAttempted.Add(item.Id) && !IsOpen(item)) Open(item);
    }

    private void Capture()
    {
        _ambiguous.Clear();
        _folders = _identity.ReadExplorerFolders();
        // Validate before reading canvas geometry. A recycled HWND must not write
        // the previous window's cached geometry into either saved slot.
        foreach (var live in _bindings.Windows.ToArray())
        {
            var current = InspectWindow(live.Identity.Handle);
            if (current == null)
            {
                live.ItemId = null; live.NeedsNativeGeometry = true;
                if (_api.GetWindowProcessId(live.Identity.Handle) == 0 && !_canvas.HasWindow(live.Identity.Handle))
                    _bindings.Remove(live.Identity.Handle);
                continue;
            }
            if (current.Value.Identity != live.Identity)
            {
                _bindings.Observe(current.Value.Identity, out _);
            }
            else if (!_api.IsWindowVisible(live.Identity.Handle)
                && Document.Items.Any(i => i.Id == live.ItemId && i.DesktopId == _desktop)) live.ItemId = null;
        }
        // The normal canvas intentionally excludes initially maximized/minimized
        // windows. Enroll these without moving them so the workspace saves them too.
        _api.EnumWindows(handle =>
        {
            if (_canvas.HasWindow(handle) || !_desktops.IsOnCurrentDesktop(handle)
                || !_api.IsManageable(handle, (uint)Environment.ProcessId, true, true)) return true;
            EnrollNativeGeometry(handle);
            return true;
        });
        var pending = new List<(WorkspaceLiveWindow Live, WorkspaceMatchInput Input, WorldRect Bounds, bool Explorer)>();
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
            var observation = InspectWindow(handle);
            if (observation == null) continue;
            var live = _bindings.Observe(observation.Value.Identity, out _);
            if (live.NeedsNativeGeometry)
            {
                EnrollNativeGeometry(handle);
                world = _canvas.Windows[handle];
                live.NeedsNativeGeometry = false;
            }
            if (live.Forgotten) continue;
            var exe = observation.Value.Exe;
            var title = _api.GetWindowTitle(handle);
            bool explorer = string.Equals(Path.GetFileName(exe), "explorer.exe", StringComparison.OrdinalIgnoreCase);
            string target = explorer && _folders.TryGetValue(handle, out var folder) ? folder
                : WorkspaceTargetIdentity.Read(exe, title);
            if (explorer && target.Length == 0 && !live.ItemId.HasValue)
            {
                live.WaitingForFolderSince ??= _clock.TickCount64;
                if (_clock.TickCount64 - live.WaitingForFolderSince.Value < 8000) continue;
            }
            if (live.ItemId is { } id)
            {
                var old = Document.Items.FirstOrDefault(i => i.Id == id);
                if (old != null && string.Equals(old.ExecutablePath, exe, StringComparison.OrdinalIgnoreCase))
                {
                    if (WorkspaceGeometry.ValidBounds(world)) old.Bounds = world;
                    old.Title = title;
                    // Explorer navigation updates this slot's saved folder.
                    if (explorer && target.Length > 0) { old.Target = target; old.IsFolder = true; }
                    else if (target.Length > 0) old.Target = target;
                    if (old.DesktopId != _desktop) Zones.ForgetItem(old.Id);
                    old.DesktopId = _desktop;
                    continue;
                }
                live.ItemId = null;
            }
            Guid? requested = _launching.Keys.FirstOrDefault(key => Document.Items.Any(i => i.Id == key
                && i.DesktopId == _desktop && string.Equals(i.ExecutablePath, exe, StringComparison.OrdinalIgnoreCase))
                && _launchBaselines.TryGetValue(key, out var baseline) && !baseline.Contains(handle));
            var input = new WorkspaceMatchInput(live.Token, exe, title, target, requested == Guid.Empty ? null : requested)
            {
                ExistingLaunches = _launching.Keys.Where(key => _launchBaselines.TryGetValue(key, out var baseline)
                    && baseline.Contains(handle)).ToArray()
            };
            pending.Add((live, input, world, explorer));
        }
        var plan = WorkspaceMatcher.Plan(Document.Items, _desktop, pending.Select(p => p.Input).ToArray(), _bindings.BoundIds);
        foreach (var (live, input, world, explorer) in pending)
        {
            var match = plan[live.Token];
            if (match.IsAmbiguous)
            {
                _ambiguous.UnionWith(match.AmbiguousIds);
                continue; // No movement, no extra saved entry, no guessed identity.
            }
            if (!StillCurrent(live)) continue;
            var handle = live.Identity.Handle;
            var item = match.Item;
            bool restore = item != null;
            if (item == null)
            {
                if (!WorkspaceGeometry.ValidBounds(world) || Document.Items.Count >= 2000) continue;
                item = new WorkspaceItem { DesktopId = _desktop, Title = input.Title, ExecutablePath = input.ExecutablePath,
                    Target = input.Target, IsFolder = explorer && input.Target.Length > 0, Bounds = world };
                Document.Items.Add(item);
            }
            if (_bindings.Bind(live, item.Id) && restore) RestoreBinding(live, item);
        }
        Document.Cameras[_desktop] = new WorkspaceCamera { X = _canvas.CamX, Y = _canvas.CamY, Zoom = _canvas.Zoom };
    }

    private (WorkspaceWindowIdentity Identity, string Exe)? InspectWindow(IntPtr handle)
    {
        var pid = _api.GetWindowProcessId(handle);
        var process = _identity.InspectProcess(pid);
        if (pid == 0 || process is not { } p || p.ProcessId != pid || p.StartedAtUtcTicks <= 0
            || p.ExecutablePath.Length == 0 || _api.GetWindowProcessId(handle) != pid) return null;
        return (new(handle, pid, p.StartedAtUtcTicks), p.ExecutablePath);
    }

    private bool StillCurrent(WorkspaceLiveWindow live)
        => _bindings.At(live.Identity.Handle) == live && InspectWindow(live.Identity.Handle)?.Identity == live.Identity;

    private WorkspaceLiveWindow? ValidBinding(IntPtr handle)
    {
        var live = _bindings.At(handle);
        if (live?.ItemId == null) return null;
        bool inactive = Document.Items.Any(i => i.Id == live.ItemId && i.DesktopId != _desktop);
        if (StillCurrent(live) && (inactive || _api.IsWindowVisible(handle))) return live;
        live.ItemId = null;
        return null;
    }

    private WorkspaceLiveWindow? BoundWindow(Guid itemId)
        => _bindings.ForItem(itemId) is { } live ? ValidBinding(live.Identity.Handle) : null;

    private void EnrollNativeGeometry(IntPtr handle)
    {
        var (x, y, w, h) = _api.GetNormalRect(handle);
        _wm.TrackForDesktopRestore(handle);
        var (wx, wy) = _canvas.ScreenToWorld(x, y);
        var (ww, wh) = _canvas.ScreenToWorldSize(w, h);
        int style = _api.GetWindowStyle(handle);
        _canvas.RestoreWindow(handle, new WorldRect { X = wx, Y = wy, W = ww, H = wh,
            State = (style & (int)WINDOW_STYLE.WS_MINIMIZE) != 0 ? WindowState.Minimized
                : (style & (int)WINDOW_STYLE.WS_MAXIMIZE) != 0 ? WindowState.Maximized : WindowState.Normal });
    }

    private void RestoreBinding(WorkspaceLiveWindow live, WorkspaceItem item)
    {
        RemoveCard(item.Id);
        _launching.Remove(item.Id);
        _launchBaselines.Remove(item.Id);
        _ambiguous.Remove(item.Id);
        if (!StillCurrent(live)) { live.ItemId = null; return; }
        var handle = live.Identity.Handle;
        var placed = item.Bounds;
        _api.RestoreNativeState(handle, _canvas.WorldToScreen(placed), placed.State);
        if (placed.State == WindowState.Normal) _wm.RestorePlacement(handle, placed);
        else _canvas.RestoreWindow(handle, placed);
    }

    private void OnRemoving(IntPtr handle)
    {
        if (_disposed || _switching) return;
        var live = _bindings.Remove(handle);
        if (live?.ItemId is { } id)
        {
            var item = Document.Items.FirstOrDefault(i => i.Id == id);
            // A destroyed window may no longer be queryable. Cached canvas geometry
            // is safe only if the handle has not already been reused by another process.
            var current = InspectWindow(handle);
            if (item != null && (current?.Identity == live.Identity || _api.GetWindowProcessId(handle) == 0)
                && _canvas.Windows.TryGetValue(handle, out var world) && WorkspaceGeometry.ValidBounds(world))
                item.Bounds = world;
            Save();
        }
        // Defer form creation until Tick, outside WM's enumeration/removal loop.
    }

    private void EnsureCards()
    {
        foreach (var item in Document.Items.Where(i => i.DesktopId == _desktop).ToArray())
        {
            if (IsOpen(item)) { RemoveCard(item.Id); continue; }
            if (_cards.ContainsKey(item.Id))
            {
                var card = _cards[item.Id];
                card.UpdateItem(View(item));
                continue;
            }
            var form = _placeholderFactory.Create(this, View(item));
            _cards[item.Id] = form;
            // Position before Show avoids flashing a card at the desktop origin.
            var rect = _canvas.WorldToScreen(item.Bounds);
            form.ShowAt(rect);
            var bounds = item.Bounds;
            bounds.State = WindowState.Normal;
            bounds.PinnedToScreen = false;
            _wm.RestorePlacement(form.Handle, bounds);
            form.PlacementChanged += () => { if (!_disposed && !_switching) _wm.ReconcileWindow(form.Handle); };
        }
    }

    private void RemoveCard(Guid id)
    {
        if (!_cards.Remove(id, out var card)) return;
        try { _wm.RemoveWindow(card.Handle); }
        finally { card.Dispose(); }
    }

    public bool IsOpen(WorkspaceItem item) => BoundWindow(item.Id) != null;
    public string ItemState(WorkspaceItem item) => _launching.ContainsKey(item.Id) ? "Opening…"
        : _queued.Contains(item.Id) ? "Queued" : IsOpen(item) ? "Open"
        : _ambiguous.Contains(item.Id) ? "Needs linking" : "Placeholder";

    private WorkspaceItemView View(WorkspaceItem item) => new(item.Id, item.Name.Length > 0 ? item.Name : item.Title, item.ExecutablePath,
        item.Target, item.OpenOnStartup, item.Bounds, ItemState(item), item.Name, item.IsFolder);

    private WorkspaceItem? Find(Guid id) => _disposed ? null : Document.Items.FirstOrDefault(i => i.Id == id);

    public void Open(Guid id) { if (Find(id) is { } item) Open(item); }
    public void FocusItem(Guid id) { if (Find(id) is { } item) FocusItem(item); }
    public void CloseToPlaceholder(Guid id) { if (Find(id) is { } item) CloseToPlaceholder(item); }
    public void Forget(Guid id) { if (Find(id) is { } item) Forget(item); }

    private bool IsLaunchingApp(string exe) => _launching.Keys.Any(id => Document.Items.Any(i => i.Id == id
        && string.Equals(i.ExecutablePath, exe, StringComparison.OrdinalIgnoreCase)));

    private void Open(WorkspaceItem item)
    {
        if (BoundWindow(item.Id) is { } live)
        {
            Focus(live.Identity.Handle, item.Bounds);
            return;
        }
        if (_launching.ContainsKey(item.Id)) return;
        // Launch windows from the same app one at a time, so project/file slots
        // cannot steal one another's first new HWND.
        if (item.DesktopId != _desktop || IsLaunchingApp(item.ExecutablePath))
        { _queued.Add(item.Id); Changed?.Invoke(); return; }
        try
        {
            // Include even inaccessible HWNDs. Later gaining permission to inspect
            // an already-existing window does not make it evidence of this launch.
            var baseline = new HashSet<IntPtr>();
            _api.EnumWindows(handle => { baseline.Add(handle); return true; });
            _launchBaselines[item.Id] = baseline;
            _launching[item.Id] = _clock.TickCount64 + 30000;
            _launcher.Open(item);
            _status = null;
        }
        catch (Exception ex)
        {
            _launching.Remove(item.Id);
            _launchBaselines.Remove(item.Id);
            _status = "Could not open " + item.Title + ": " + ex.Message;
            LaunchFailed?.Invoke(_status);
        }
        Changed?.Invoke();
    }

    private void FocusItem(WorkspaceItem item)
    {
        if (IsOpen(item)) { Open(item); return; }
        if (_cards.TryGetValue(item.Id, out var card)) Focus(card.Handle, item.Bounds);
    }

    private void Focus(IntPtr handle, WorldRect bounds)
    {
        var screen = _windowActions.Viewport;
        _canvas.CenterOn(bounds.X, bounds.Y, bounds.W, bounds.H, screen.Width, screen.Height);
        _canvas.Commit();
        _windowActions.Focus(handle, (_api.GetWindowStyle(handle) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0);
    }

    private void CloseToPlaceholder(WorkspaceItem item)
    {
        if (!IsOpen(item)) return;
        Capture(); Save();
        if (BoundWindow(item.Id) is not { } live) return;
        // Normal close: application save prompts and cancellation still work.
        _windowActions.RequestClose(live.Identity.Handle);
    }

    private void Forget(WorkspaceItem item)
    {
        if (BoundWindow(item.Id) is { } live) { live.Forgotten = true; live.ItemId = null; }
        _launching.Remove(item.Id);
        _launchBaselines.Remove(item.Id);
        _ambiguous.Remove(item.Id);
        _queued.Remove(item.Id);
        Document.Items.Remove(item);
        Zones.ForgetItem(item.Id);
        RemoveCard(item.Id);
        Save(); Changed?.Invoke();
    }

    public IReadOnlyList<WorkspaceLinkCandidate> LinkCandidates(Guid id)
    {
        if (Find(id) is not { } item || item.DesktopId != _desktop || IsOpen(item))
            return Array.Empty<WorkspaceLinkCandidate>();
        return _bindings.Windows.Where(live => CanLink(live, item))
            .Select(live => new WorkspaceLinkCandidate(live.Token,
                $"{_api.GetWindowTitle(live.Identity.Handle)}  [PID {live.Identity.ProcessId}, window 0x{live.Identity.Handle.ToInt64():X}]"))
            .ToArray();
    }

    private bool CanLink(WorkspaceLiveWindow live, WorkspaceItem item)
        => !live.Forgotten && !live.ItemId.HasValue && _api.IsWindowVisible(live.Identity.Handle)
            && _desktops.IsOnCurrentDesktop(live.Identity.Handle) && StillCurrent(live)
            && InspectWindow(live.Identity.Handle) is { } observed
            && string.Equals(observed.Exe, item.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    public bool LinkWindow(Guid id, Guid candidateToken)
    {
        if (Find(id) is not { } item || item.DesktopId != _desktop || IsOpen(item)
            || _bindings.ForToken(candidateToken) is not { } live || !CanLink(live, item)
            || !_bindings.Bind(live, id)) return false;
        RestoreBinding(live, item);
        EnsureCards(); Save(); Changed?.Invoke();
        return IsOpen(item);
    }

    public void SetAutoOpen(Guid id, bool value)
    {
        if (Find(id) is not { } item) return;
        item.OpenOnStartup = value; Save(); Changed?.Invoke();
    }

    public void SetName(Guid id, string name)
    {
        if (Find(id) is not { } item || name == null || name.Length > 200) return;
        item.Name = name.Trim(); Save(); Changed?.Invoke();
    }

    public void SetTarget(Guid id, string selected, bool folder)
    {
        if (Find(id) is not { } item || string.IsNullOrWhiteSpace(selected)) return;
        item.Target = selected;
        // A project folder for an editor is an argument to that editor; a folder
        // for Explorer is opened through Explorer's folder-launch path.
        item.IsFolder = folder && string.Equals(Path.GetFileName(item.ExecutablePath), "explorer.exe", StringComparison.OrdinalIgnoreCase);
        Save(); Changed?.Invoke();
    }

    public bool SaveNow()
    { if (_disposed) return false; Capture(); EnsureCards(); return Save(); }

    private bool Save()
        => _persistence.Save();

    public void Dispose() => Stop(save: true);

    public void Stop(bool save)
    {
        if (_disposed) return;
        var failures = new List<Exception>();
        // Rollback of incomplete app startup must not save a half-restored canvas.
        if (save && _started)
        {
            try
            {
                Capture();
                if (!Save()) throw new IOException(Status ?? "Workspace could not be saved.");
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        _disposed = true;
        _wm.WindowRemoving -= OnRemoving;
        _cache.BeforeStateSaved -= BeforeSwitch;
        _cache.AfterStateLoaded -= AfterSwitch;
        foreach (var id in _cards.Keys.ToArray())
        {
            try { RemoveCard(id); }
            catch (Exception ex) { failures.Add(ex); }
        }
        _bindings.Clear();
        _launchBaselines.Clear();
        if (failures.Count > 0) throw new AggregateException("Workspace shutdown failed.", failures);
    }
}
