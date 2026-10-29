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
    private readonly WorkspaceWindowInspector _windowInspector;
    private readonly WorkspaceBindings _bindings = new();
    private readonly HashSet<Guid> _ambiguous = new();
    private Dictionary<IntPtr, string> _folders = new();
    private Guid _desktop;
    private long _nextCapture;
    private bool _switching, _disposed, _started;
    private bool _capturingStartup;
    private string? _status;
    public WorkspaceDocument Document => _persistence.Document;
    public ZoneService Zones { get; }
    public IReadOnlyList<WorkspaceItemView> Items => Document.Items.Select(View).ToArray();
    public string? Status => _persistence.LastError ?? _status ?? _persistence.LoadWarning
        ?? (_ambiguous.Count > 0 ? "Some saved places need linking. Use 'Link window…' to choose the correct open window." : null);
    public event Action? Changed;
    public event Action<string>? LaunchFailed;

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
        _windowInspector = new(api, identity, Document);
        Zones = new ZoneService(Document, () => _desktop, Save);
        _wm.IsAutomaticMinimizationExcluded = IsAutomaticMinimizationExcluded;
        _wm.WindowRemoving += OnRemoving;
        _wm.WindowStateChanged += OnWindowStateChanged;
        _wm.AutomaticParkingChanged += OnAutomaticParkingChanged;
        _wm.WindowPlacementChanged += CommitWindowPlacement;
        _wm.InventoryWindowFocused += OnInventoryWindowFocused;
        _canvas.PinnedChanged += OnWindowPinned;
        _cache.BeforeStateSaved += BeforeSwitch;
        _cache.AfterStateLoaded += AfterSwitch;
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        MigrateTemporaryAreas();
        InitializePlacementPolicy();
        RestoreCamera();
        _wm.DiscoverNewWindows();
        _capturingStartup = true;
        try { Capture(); } finally { _capturingStartup = false; }
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
            _launchMessages[id] = "Still waiting for a matching app window. Keep waiting, double-click to retry, or right-click to link an open window.";
            _status = "No matching window appeared. The placeholder is kept; you can retry or choose a launch target.";
        }
        Capture();
        EnsureCards();
        OpenStartupItems();
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
        {
            // Already-open windows may have been discovered by startup events.
            // Their desktop rectangles are not saved canvas positions yet: let
            // Capture match their slots before camera motion can project/park them.
            bool projection = _wm.SuspendProjection, parking = _wm.SuspendAutomaticParking;
            _wm.SuspendProjection = _wm.SuspendAutomaticParking = true;
            try
            {
                _canvas.LoadState(new CanvasState { CamX = c.X, CamY = c.Y, Zoom = c.Zoom,
                    Windows = new Dictionary<IntPtr, WorldRect>(_canvas.Windows) });
            }
            finally
            {
                _wm.SuspendProjection = projection;
                _wm.SuspendAutomaticParking = parking;
            }
        }
    }

    private void BeforeSwitch()
    {
        InvalidateOverviewSnapshot();
        if (!_started || _disposed) return;
        foreach (var (id, card) in _cards)
            if (Document.Items.FirstOrDefault(i => i.Id == id) is { } item) SyncCardGeometry(item, card);
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
        InvalidateOverviewSnapshot();
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
            if (Document.TemporaryAreas.Remove(Guid.Empty, out var area))
                Document.TemporaryAreas[_desktops.CurrentDesktopId] = area;
        }
        _desktop = _desktops.CurrentDesktopId;
        Zones.NotifyDesktopChanged();
        _switching = false;
        RestoreCamera();
        _capturingStartup = true;
        try { Capture(); } finally { _capturingStartup = false; }
        EnsureCards();
        OpenStartupItems();
    }

    private WorkspaceItem? Find(Guid id) => _disposed ? null : Document.Items.FirstOrDefault(i => i.Id == id);

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
        _wm.IsAutomaticMinimizationExcluded = null;
        _wm.WindowRemoving -= OnRemoving;
        _wm.WindowStateChanged -= OnWindowStateChanged;
        _wm.AutomaticParkingChanged -= OnAutomaticParkingChanged;
        _wm.WindowPlacementChanged -= CommitWindowPlacement;
        _wm.InventoryWindowFocused -= OnInventoryWindowFocused;
        ArrivalViewportProvider = null;
        _canvas.PinnedChanged -= OnWindowPinned;
        AdditionalPlacementObstacles = null;
        _cache.BeforeStateSaved -= BeforeSwitch;
        _cache.AfterStateLoaded -= AfterSwitch;
        foreach (var id in _cards.Keys.ToArray())
        {
            try { RemoveCard(id); }
            catch (Exception ex) { failures.Add(ex); }
        }
        _bindings.Clear();
        _launchBaselines.Clear();
        _launchMessages.Clear();
        if (failures.Count > 0) throw new AggregateException("Workspace shutdown failed.", failures);
    }
}
