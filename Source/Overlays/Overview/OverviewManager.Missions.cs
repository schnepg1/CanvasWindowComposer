using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    internal IAgentSessionManager HerdrSessions { get; set; } = new HerdrSessions();
    private MissionCardLayer? _missionCards;
    private MissionControlWindow? _missionControl;
    internal MissionControlWindow? MissionControl => _missionControl;
    private System.Windows.Forms.Timer? _missionStatusTimer;
    private CancellationTokenSource? _missionStatusRequest;
    private Dictionary<Guid, HerdrSessions.Terminal> _missionTerminals = new();
    private readonly Dictionary<Guid, AgentTerminalOverlayWindow> _activeTerminalOverlays = new();
    internal AgentTerminalOverlayWindow? ActiveTerminalOverlay => _activeTerminalOverlays.Values.FirstOrDefault();
    internal IReadOnlyDictionary<Guid, AgentTerminalOverlayWindow> ActiveTerminalOverlays => _activeTerminalOverlays;
    private bool _herdrDisconnected;
    private Guid? _terminalCard;
    private bool _terminalWasAnchored;

    private IEnumerable<MissionAgentCard> MissionCards()
    {
        if (CurrentMode != OverviewMode.Zooming || IsStartupIntroActive || _annotationDocument == null) return [];
        var desktop = _annotationDesktop();
        return _annotationDocument.Items.Where(item => item.DesktopId == desktop && !item.InInventory
            && MissionAgentCard.IsCard(item)).Select(item => new MissionAgentCard(item));
    }

    private AgentStatusReport? MissionReport(MissionAgentCard card)
    {
        var workflow = _telemetryProvider?.GetReportForCard(card.Layout);
        if (workflow is { State: AgentExecutionState.Testing }) return workflow;
        if (_missionTerminals.TryGetValue(card.Id, out var terminal) && terminal.IsLive
            && Enum.TryParse<AgentExecutionState>(terminal.AgentState, true, out var state))
            return new(card.Role, state, TaskId: card.TaskId);
        return workflow;
    }

    private string? MissionRuntimeLabel(Guid card)
    {
        if (!_missionTerminals.TryGetValue(card, out var terminal)) return null;
        if (_herdrDisconnected) return "Herdr disconnected";
        if (!terminal.IsLive) return "Session ended";
        return !Enum.TryParse<AgentExecutionState>(terminal.AgentState, true, out _)
            ? "Terminal live · no status" : null;
    }

    private void UpdateMissionStatusTimer()
    {
        if (CurrentMode != OverviewMode.Zooming || _teamPods == null) return;
        if (_missionStatusTimer == null)
        {
            _missionStatusTimer = new() { Interval = 5000 };
            _missionStatusTimer.Tick += (_, _) => RefreshMissionTerminals();
        }
        _missionStatusTimer.Start();
        RefreshMissionTerminals();
    }

    private async void RefreshMissionTerminals()
    {
        if (_disposed || CurrentMode != OverviewMode.Zooming || _missionStatusRequest != null || !MissionCards().Any()) return;
        using var request = new CancellationTokenSource();
        _missionStatusRequest = request;
        try
        {
            _missionTerminals = await HerdrSessions.Snapshot(request.Token);
            _herdrDisconnected = false;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception)
        {
            _herdrDisconnected = true;
        }
        finally { _missionStatusRequest = null; }
        if (!_disposed && CurrentMode == OverviewMode.Zooming) _visualFrames.Request();
    }

    public void ToggleAiTeams(CanvasPoint? anchor = null)
    {
        if (_missionControl != null) CloseMissionControl();
        else ShowMissionIndex(anchor);
    }

    private void ShowMissionIndex(CanvasPoint? anchor = null)
    {
        if (_teamPods == null || _disposed || CurrentMode != OverviewMode.Zooming) return;
        var window = NewMissionControl();
        window.ShowMissions((_zones?.Items ?? []).Where(z => z.IsMission).ToArray(), name =>
        {
            var area = _screens.PrimaryWorkingArea;
            var (x, y) = _camera.WorldFromVirtual(area.Left + area.Width / 2, area.Top + area.Height / 2);
            var id = _teamPods.CreateMission(_annotationDesktop(), name, anchor ?? new CanvasPoint(x, y));
            if (id == null) return false;
            if (FindMission(id.Value) is { } newMission)
            {
                JumpToZone(newMission);
                ShowMission(newMission);
            }
            return true;
        }, zone =>
        {
            if (FindMission(zone.Id) is { } current) { CloseMissionControl(); JumpToZone(current); }
        }, ShowMission);
        window.Present();
    }

    private ZoneView? FindMission(Guid id) => _zones?.Items.FirstOrDefault(z => z.Id == id && z.IsMission);

    private WorkspaceZone? MissionFor(MissionAgentCard card) => _placeholderWorkspace?.Document.Zones
        .FirstOrDefault(z => z.IsMission && z.DesktopId == card.Layout.DesktopId && z.AgentTaskId == card.TaskId);

    private void ShowMission(ZoneView mission)
    {
        if (FindMission(mission.Id) is not { } current) return;
        var workspace = _placeholderWorkspace?.Document.Zones.FirstOrDefault(z => z.Id == current.Id);
        var window = NewMissionControl();
        window.ShowMission(current, MissionCards().Where(c => c.TaskId == workspace?.AgentTaskId).ToArray(),
            ShowMissionAgent, locked => _teamPods?.SetPositionLocked(current.Id, locked) == true, name =>
            {
                if (_teamPods?.AddAgent(current.Id, name) != true) return false;
                ShowMission(current);
                return true;
            });
        window.Present();
    }

    private void ShowMissionAgent(MissionAgentCard card)
    {
        var data = MissionFor(card);
        var mission = data == null ? null : FindMission(data.Id);
        var project = AgentCardContext.ProjectAt(_placeholderWorkspace?.Document.Zones ?? [], card.Layout);
        var folder = AgentCardContext.ResolveWorkingFolder(card.Layout, _telemetryProvider, data?.MissionWorkingFolder, project?.ProjectFolderPath);
        var window = NewMissionControl();
        window.ShowAgent(mission, card, MissionReport(card), folder,
            locked => mission != null && _teamPods?.SetPositionLocked(mission.Id, locked) == true,
            directory =>
            {
                if (mission != null) return _teamPods?.SetWorkingFolder(mission.Id, directory) == true;
                if (!System.IO.Path.IsPathFullyQualified(directory) || !System.IO.Directory.Exists(directory)) return false;
                var current = _annotationDocument?.Items.FirstOrDefault(item => item.Id == card.Id);
                if (current == null) return false;
                _annotationDocument!.Put(current with { AgentWorkingFolder = directory });
                return true;
            }, () => { if (mission != null) ShowMission(mission); });
        _terminalCard = card.Id;
        window.TerminalLayoutChanged += () => PositionAgentTerminal(SceneCamera, layoutChanged: true);
        window.Present();
    }

    private MissionControlWindow NewMissionControl()
    {
        CloseMissionControl();
        var window = new MissionControlWindow(CanvasPanelOwner, HerdrSessions);
        _missionControl = window;
        _isAiTeamsActive = true;
        window.TerminalChanged += RefreshMissionTerminals;
        window.ExternalTerminalOpened += () => TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
        window.Closed += (_, _) =>
        {
            _missionControl = null;
            _terminalCard = null;
            _terminalWasAnchored = false;
            _isAiTeamsActive = false;
            _unifiedDock?.Panel.SetAiTeamsActive(false);
        };
        _unifiedDock?.Panel.SetAiTeamsActive(true);
        return window;
    }

    private void PositionAgentTerminal(CanvasCamera camera, bool layoutChanged = false)
    {
        if (_missionControl is not { IsVisible: true } window || !window.HasExpandedTerminal && !layoutChanged
            || _terminalCard is not { } id) return;
        bool collapse = !window.HasExpandedTerminal;
        var card = MissionCards().FirstOrDefault(item => item.Id == id);
        if (card == null) { CloseMissionControl(); return; }
        var bounds = card.Bounds;
        double left = (bounds.X - camera.X) * camera.Zoom;
        double top = (bounds.Y - camera.Y) * camera.Zoom;
        double width = bounds.Width * camera.Zoom;
        double height = bounds.Height * camera.Zoom;
        var pass = _passes.Where(p => p.Visible).Select(p =>
        {
            var area = p.Screen.WorkingArea;
            double intersection = Math.Max(0, Math.Min(left + width, area.Right) - Math.Max(left, area.Left))
                * Math.Max(0, Math.Min(top + height, area.Bottom) - Math.Max(top, area.Top));
            return (Pass: p, Area: area, Intersection: intersection);
        }).OrderByDescending(p => p.Intersection).FirstOrDefault();
        if (pass.Pass == null) return;
        if (pass.Intersection == 0)
        {
            if (_terminalWasAnchored && !collapse) { CloseMissionControl(); return; }
            if (layoutChanged && !collapse)
            {
                var area = _screens.PrimaryWorkingArea;
                var center = CanvasSelection.Center(bounds);
                NavigateToCamera(new CanvasCamera(center.X - (area.Left + area.Width / 2.0) / camera.Zoom,
                    center.Y - (area.Top + area.Height / 2.0) / camera.Zoom, camera.Zoom));
            }
            return;
        }
        _terminalWasAnchored = !collapse;
        double dpi = pass.Pass.DeviceDpi / 96.0;
        int w = Math.Min(pass.Area.Width, (int)Math.Round((collapse ? 460 : 760) * dpi));
        int h = Math.Min(pass.Area.Height, (int)Math.Round((collapse ? 475 : 560) * dpi));
        int x = (int)Math.Clamp(left, pass.Area.Left, pass.Area.Right - w);
        int y = (int)Math.Clamp(top, pass.Area.Top, pass.Area.Bottom - h);
        var hwnd = (HWND)new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (PInvoke.GetWindowRect(hwnd, out var current) && current.left == x && current.top == y
            && current.right == x + w && current.bottom == y + h) return;
        PInvoke.SetWindowPos(hwnd, default, x, y, w, h,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
    }

    private bool CloseMissionControl()
    {
        if (_missionControl == null) return false;
        _missionControl.Close();
        return true;
    }

    private void DisposeMissionControl()
    {
        CloseMissionControl();
        CloseTerminalOverlays();
        _missionStatusRequest?.Cancel();
        _missionStatusTimer?.Dispose();
        _missionStatusTimer = null;
    }

    internal void CloseMissions()
    {
        CloseMissionControl();
        CloseTerminalOverlays();
    }

    private bool IsAgentSessionLive(Guid cardId) =>
        _missionTerminals.TryGetValue(cardId, out var t) && t.IsLive;

    private HerdrSessions.Terminal? GetAgentTerminal(Guid cardId) =>
        _missionTerminals.TryGetValue(cardId, out var t) ? t : null;

    private string? ResolveCardFolder(MissionAgentCard card)
    {
        var data = MissionFor(card);
        var project = AgentCardContext.ProjectAt(_placeholderWorkspace?.Document.Zones ?? [], card.Layout);
        return AgentCardContext.ResolveWorkingFolder(card.Layout, _telemetryProvider, data?.MissionWorkingFolder, project?.ProjectFolderPath);
    }

    internal void CycleAgentProfile(MissionAgentCard card)
    {
        if (_disposed || _annotationDocument == null) return;
        var current = _annotationDocument.Items.FirstOrDefault(item => item.Id == card.Id);
        if (current == null) return;

        var currentAgent = HerdrSessions.FindAgent(current.AgentRole);
        int currentIndex = -1;
        for (int i = 0; i < HerdrSessions.SupportedAgents.Count; i++)
        {
            if (string.Equals(HerdrSessions.SupportedAgents[i].Id, currentAgent.Id, StringComparison.OrdinalIgnoreCase))
            {
                currentIndex = i;
                break;
            }
        }

        int nextIndex = (currentIndex + 1) % HerdrSessions.SupportedAgents.Count;
        var nextAgent = HerdrSessions.SupportedAgents[nextIndex];

        string newText = current.Text;
        if (string.IsNullOrWhiteSpace(newText) || newText == "AI agent"
            || string.Equals(newText, currentAgent.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            newText = nextAgent.DisplayName;
        }

        var updated = current with { AgentRole = nextAgent.Id, Text = newText };
        _annotationDocument.Put(updated);
        _annotationStore?.Save();
        _visualFrames.Request();
    }

    internal void ToggleAgentSession(MissionAgentCard card)
    {
        if (_disposed) return;
        if (IsAgentSessionLive(card.Id))
            StopAgentSession(card);
        else
            StartAgentSession(card);
    }

    internal async void StartAgentSession(MissionAgentCard card)
    {
        if (_disposed) return;
        var agent = HerdrSessions.FindAgent(card.Role);
        var folder = ResolveCardFolder(card);
        if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder))
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var terminal = await HerdrSessions.StartAgent(agent, folder, card.Id, cts.Token, card.Prompt);
            _missionTerminals[card.Id] = terminal;
            _herdrDisconnected = false;
            if (_activeTerminalOverlays.TryGetValue(card.Id, out var overlay))
            {
                _ = overlay.Attach(observe: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
        }
        finally
        {
            RefreshMissionTerminals();
            _visualFrames.Request();
        }
    }

    internal async void StopAgentSession(MissionAgentCard card)
    {
        if (_disposed) return;
        if (_activeTerminalOverlays.TryGetValue(card.Id, out var overlay))
        {
            overlay.TerminalPane.Disconnect();
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await HerdrSessions.StopAgent(card.Id, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
        }
        finally
        {
            RefreshMissionTerminals();
            _visualFrames.Request();
        }
    }

    private void PromptChangeWorkingFolder(MissionAgentCard card)
    {
        if (_disposed || _annotationDocument == null) return;
        var current = _annotationDocument.Items.FirstOrDefault(item => item.Id == card.Id);
        if (current == null) return;

        var resolved = ResolveCardFolder(card);
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select working directory for agent",
            SelectedPath = !string.IsNullOrWhiteSpace(resolved) && System.IO.Directory.Exists(resolved) ? resolved : "",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && System.IO.Directory.Exists(dialog.SelectedPath))
        {
            var updated = current with { AgentWorkingFolder = dialog.SelectedPath };
            _annotationDocument.Put(updated);
            _annotationStore?.Save();
            ReconcileVisuals();
        }
    }

    private void PromptEditPrompt(MissionAgentCard card)
    {
        if (_disposed || _annotationDocument == null) return;
        var current = _annotationDocument.Items.FirstOrDefault(item => item.Id == card.Id);
        if (current == null) return;

        var (confirmed, prompt) = PromptConfirmationDialog(CanvasPanelOwner, card.Name, current.AgentPrompt ?? "");
        if (confirmed)
        {
            string newPrompt = string.IsNullOrWhiteSpace(prompt) ? "" : prompt.Trim();
            if (newPrompt.Length > AnnotationDocument.MaximumTextLength)
                newPrompt = newPrompt.Substring(0, AnnotationDocument.MaximumTextLength);

            var updated = current with { AgentPrompt = newPrompt };
            _annotationDocument.Put(updated);
            _annotationStore?.Save();
            ReconcileVisuals();
        }
    }

    internal void ToggleAgentConsoleDrawer(Guid cardId)
    {
        if (_disposed || _annotationDocument == null) return;
        var current = _annotationDocument.Items.FirstOrDefault(item => item.Id == cardId);
        if (current == null) return;

        double currentHeight = Math.Abs(current.B.Y - current.A.Y);
        double newHeight = currentHeight > 200 ? 140 : 340;
        var updated = current with { B = new(current.B.X, current.A.Y + newHeight) };
        _annotationDocument.Put(updated);
        _annotationStore?.Save();

        if (newHeight >= 180)
        {
            OpenAgentTerminalOverlay(cardId, updated);
        }
        else
        {
            CloseAgentTerminalOverlay(cardId);
        }

        _visualFrames.Request();
    }

    internal AgentTerminalOverlayWindow? OpenAgentTerminalOverlay(Guid cardId, CanvasAnnotation? card = null)
    {
        if (_disposed) return null;
        card ??= _annotationDocument?.Items.FirstOrDefault(item => item.Id == cardId);
        if (card == null) return null;

        if (_activeTerminalOverlays.TryGetValue(cardId, out var existing))
        {
            UpdateTerminalOverlayPlacement(existing, card);
            existing.Show();
            existing.Activate();
            existing.Focus();
            return existing;
        }

        var profile = HerdrSessions.FindAgent(card.AgentRole);
        var overlay = new AgentTerminalOverlayWindow(HerdrSessions, cardId, profile, onCollapse: () => ToggleAgentConsoleDrawer(cardId));
        _activeTerminalOverlays[cardId] = overlay;
        RegisterTerminalLayer(cardId, overlay);

        var pass = _passes.FirstOrDefault(p => p.Screen.Primary) ?? _passes.FirstOrDefault();
        if (pass != null && pass.IsHandleCreated)
        {
            overlay.Show(pass);
        }
        else
        {
            overlay.Show();
        }

        UpdateTerminalOverlayPlacement(overlay, card);
        _ = overlay.Attach(observe: false);
        overlay.Activate();
        overlay.Focus();
        return overlay;
    }

    internal void DeleteAgentCard(Guid cardId)
    {
        if (_disposed || _annotationDocument == null) return;
        var note = _annotationDocument.Items.FirstOrDefault(item => item.Id == cardId);
        if (note == null) return;
        CloseAgentTerminalOverlay(cardId);
        _annotationDocument.Delete(note.DesktopId, cardId);
        SpawnDissolution([note]);
        _annotationStore?.Save();
        Selection.Clear();
        ReconcileVisuals();
    }

    internal void CloseAgentTerminalOverlay(Guid cardId)
    {
        if (_activeTerminalOverlays.Remove(cardId, out var overlay))
        {
            UnregisterTerminalLayer(cardId);
            overlay.Dispose();
        }
    }

    internal void FocusAgentTerminalOverlay(Guid cardId)
    {
        if (_disposed) return;
        if (_activeTerminalOverlays.TryGetValue(cardId, out var overlay))
        {
            overlay.Activate();
            overlay.Focus();
        }
        else
        {
            OpenAgentTerminalOverlay(cardId);
        }
    }

    internal void CloseTerminalOverlays()
    {
        var overlays = _activeTerminalOverlays.ToArray();
        _activeTerminalOverlays.Clear();
        foreach (var (cardId, overlay) in overlays)
        {
            UnregisterTerminalLayer(cardId);
            overlay.Dispose();
        }
    }

    private static string TerminalLayerName(Guid cardId) => $"AgentTerminal:{cardId:N}";

    /// <summary>
    /// Registers the console drawer with the shared layer stack so it participates in the
    /// same topmost raise order as every other canvas panel instead of fighting for z-order.
    /// </summary>
    private void RegisterTerminalLayer(Guid cardId, AgentTerminalOverlayWindow overlay)
    {
        _layerCoordinator.Register(CanvasLayerTier.AgentTerminal, TerminalLayerName(cardId),
            overlay.RaiseWithoutActivation,
            () => !overlay.IsDisposed && !overlay.IsClosing && overlay.IsVisible);
    }

    private void UnregisterTerminalLayer(Guid cardId) =>
        _layerCoordinator.Unregister(TerminalLayerName(cardId));

    internal void UpdateTerminalOverlaysPlacement()
    {
        if (_disposed || _activeTerminalOverlays.Count == 0) return;

        var pass = _passes.FirstOrDefault(p => p.Screen.Primary) ?? _passes.FirstOrDefault();
        double dpiX = (pass?.DeviceDpi ?? 96.0) / 96.0;
        double dpiY = (pass?.DeviceDpi ?? 96.0) / 96.0;
        var dpi = new DpiScale(dpiX, dpiY);
        var monitorBounds = pass != null ? pass.Screen.Bounds : new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var workingArea = pass != null ? pass.Screen.WorkingArea : monitorBounds;
        var transform = new CanvasViewTransform(_camera.X, _camera.Y, _camera.Zoom,
            new CanvasRect(monitorBounds.X, monitorBounds.Y, monitorBounds.Width, monitorBounds.Height), dpiX, dpiY);

        List<Guid>? toRemove = null;
        foreach (var (cardId, overlay) in _activeTerminalOverlays.ToArray())
        {
            var card = _annotationDocument?.Items.FirstOrDefault(item => item.Id == cardId);
            if (card == null || Math.Abs(card.B.Y - card.A.Y) < 180)
            {
                (toRemove ??= new()).Add(cardId);
                continue;
            }

            overlay.UpdatePlacement(transform, card.Bounds, dpi, workingArea);
        }

        if (toRemove != null)
        {
            foreach (var id in toRemove)
            {
                CloseAgentTerminalOverlay(id);
            }
        }
    }

    private void UpdateTerminalOverlayPlacement(AgentTerminalOverlayWindow overlay, CanvasAnnotation card)
    {
        var pass = _passes.FirstOrDefault(p => p.Screen.Primary) ?? _passes.FirstOrDefault();
        double dpiX = (pass?.DeviceDpi ?? 96.0) / 96.0;
        double dpiY = (pass?.DeviceDpi ?? 96.0) / 96.0;
        var dpi = new DpiScale(dpiX, dpiY);
        var monitorBounds = pass != null ? pass.Screen.Bounds : new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var workingArea = pass != null ? pass.Screen.WorkingArea : monitorBounds;
        var transform = new CanvasViewTransform(_camera.X, _camera.Y, _camera.Zoom,
            new CanvasRect(monitorBounds.X, monitorBounds.Y, monitorBounds.Width, monitorBounds.Height), dpiX, dpiY);

        overlay.UpdatePlacement(transform, card.Bounds, dpi, workingArea);
    }

    internal void ExpandAgentCard(Guid cardId)
    {
        if (_disposed || _annotationDocument == null) return;
        var current = _annotationDocument.Items.FirstOrDefault(item => item.Id == cardId);
        if (current == null) return;

        var obstacles = GetPlacementObstacles(current.DesktopId, excludeId: cardId);
        var containerZone = GetContainerZoneForCard(current);

        // Test if in-place 280x140 fits without collision
        var inPlace = new CanvasRect(current.Bounds.X, current.Bounds.Y, SubagentPlacementPlanner.FullCardWidth, SubagentPlacementPlanner.FullCardHeight);
        CanvasRect newBounds;
        if (IsPlacementSlotValid(inPlace, obstacles, containerZone))
        {
            newBounds = inPlace;
        }
        else
        {
            // Relocate if needed to avoid overlap
            newBounds = SubagentPlacementPlanner.PlanPlacement(current.Bounds, obstacles, containerZone);
        }

        var updated = current with
        {
            A = new CanvasPoint(newBounds.X, newBounds.Y),
            B = new CanvasPoint(newBounds.Right, newBounds.Bottom)
        };
        _annotationDocument.Put(updated);
        _annotationStore?.Save();
        ReconcileVisuals();
    }

    internal void CollapseAgentCard(Guid cardId)
    {
        if (_disposed || _annotationDocument == null) return;
        var current = _annotationDocument.Items.FirstOrDefault(item => item.Id == cardId);
        if (current == null) return;

        CloseAgentTerminalOverlay(cardId);

        var updated = current with
        {
            A = new CanvasPoint(current.Bounds.X, current.Bounds.Y),
            B = new CanvasPoint(current.Bounds.X + SubagentPlacementPlanner.CompactPillWidth, current.Bounds.Y + SubagentPlacementPlanner.CompactPillHeight)
        };
        _annotationDocument.Put(updated);
        _annotationStore?.Save();
        ReconcileVisuals();
    }

    internal void ToggleAgentCardSize(Guid cardId)
    {
        if (_disposed || _annotationDocument == null) return;
        var current = _annotationDocument.Items.FirstOrDefault(item => item.Id == cardId);
        if (current == null) return;

        if (current.Bounds.Height <= 60)
            ExpandAgentCard(cardId);
        else
            CollapseAgentCard(cardId);
    }

    internal CanvasAnnotation SpawnSubagent(Guid parentCardId, string subagentName, string? profileId = null, string? worktreeFolder = null)
    {
        if (_annotationDocument == null)
            throw new InvalidOperationException("Annotation document is not available.");

        var parent = _annotationDocument.Items.FirstOrDefault(a => a.Id == parentCardId);
        if (parent == null)
            throw new ArgumentException($"Parent agent card with ID {parentCardId} not found.", nameof(parentCardId));

        var obstacles = GetPlacementObstacles(parent.DesktopId, excludeId: parentCardId);
        var containerZone = GetContainerZoneForCard(parent);

        var placement = SubagentPlacementPlanner.PlanPlacement(parent.Bounds, obstacles, containerZone);

        var subagent = new CanvasAnnotation(
            Id: Guid.NewGuid(),
            DesktopId: parent.DesktopId,
            Kind: AnnotationKind.Text,
            A: new CanvasPoint(placement.X, placement.Y),
            B: new CanvasPoint(placement.Right, placement.Bottom),
            Text: subagentName,
            AgentTaskId: parent.AgentTaskId,
            AgentRole: profileId ?? parent.AgentRole)
        {
            AgentWorkingFolder = worktreeFolder ?? parent.AgentWorkingFolder
        };

        _annotationDocument.Put(subagent);
        _annotationStore?.Save();
        ReconcileVisuals();

        return subagent;
    }

    private IReadOnlyList<CanvasRect> GetPlacementObstacles(Guid desktopId, Guid? excludeId = null)
    {
        var list = new List<CanvasRect>();
        if (_annotationDocument != null)
        {
            foreach (var item in _annotationDocument.Items)
            {
                if (item.DesktopId == desktopId && !item.InInventory && item.Id != excludeId)
                {
                    list.Add(item.Bounds);
                }
            }
        }
        if (_windows != null)
        {
            foreach (var w in _windows.Windows)
            {
                if (!w.ScreenFixed && w.World.W > 0 && w.World.H > 0)
                {
                    list.Add(new CanvasRect(w.World.X, w.World.Y, w.World.W, w.World.H));
                }
            }
        }
        return list;
    }

    private CanvasRect? GetContainerZoneForCard(CanvasAnnotation card)
    {
        var zone = _placeholderWorkspace?.Document.Zones
            .FirstOrDefault(z => z.DesktopId == card.DesktopId && z.AgentTaskId == card.AgentTaskId);
        if (zone != null && zone.Bounds.W > 0 && zone.Bounds.H > 0)
        {
            return new CanvasRect(zone.Bounds.X, zone.Bounds.Y, zone.Bounds.W, zone.Bounds.H);
        }
        return null;
    }

    private static bool IsPlacementSlotValid(CanvasRect slot, IReadOnlyList<CanvasRect> obstacles, CanvasRect? containerZone)
    {
        if (containerZone.HasValue)
        {
            var zone = containerZone.Value;
            if (slot.X < zone.X || slot.Right > zone.Right || slot.Y < zone.Y || slot.Bottom > zone.Bottom)
                return false;
        }

        for (int i = 0; i < obstacles.Count; i++)
        {
            if (slot.IntersectsWith(obstacles[i]))
                return false;
        }

        return true;
    }

    internal bool TryHandleAgentCardClick(CanvasPointer pointer)
    {
        var card = HitMissionCard(pointer);
        if (card == null) return false;

        // When clicking a compact pill (height <= 60), expand it to full 280x140 card (relocating if needed to avoid overlap)
        if (card.Bounds.Height <= 60)
        {
            ExpandAgentCard(card.Id);
            return true;
        }

        var bounds = pointer.View.WorldToDip(card.Bounds);
        var rect = new System.Windows.Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        if (!MissionCardLayer.HasCardControls(rect.Width, rect.Height)) return false;

        var pt = new System.Windows.Point(pointer.Position.X, pointer.Position.Y);

        if (MissionCardLayout.AvatarRect(rect).Contains(pt))
        {
            CycleAgentProfile(card);
            return true;
        }

        if (MissionCardLayout.PlayStopRect(rect).Contains(pt))
        {
            ToggleAgentSession(card);
            return true;
        }

        if (MissionCardLayout.ConsoleToggleRect(rect).Contains(pt))
        {
            ToggleAgentConsoleDrawer(card.Id);
            return true;
        }

        if (MissionCardLayout.CloseRect(rect).Contains(pt))
        {
            DeleteAgentCard(card.Id);
            return true;
        }

        if (MissionCardLayout.EnvironmentRect(rect).Contains(pt))
        {
            PromptChangeWorkingFolder(card);
            return true;
        }

        return false;
    }

    internal bool TryInspectMission(CanvasPointer pointer)
    {
        if (pointer.Button != CanvasPointerButton.Left || pointer.Modifiers != CanvasModifiers.None || _tools.Active
            || _annotationTool?.Mode is not (AnnotationMode.Select or AnnotationMode.Navigate)
            || _backgroundTool?.SelectedId != null || _zoneTool.Gesture.Armed) return false;
        var header = (_zones?.Items ?? []).LastOrDefault(z => z.IsMission
            && ZoneLabelLayout.Get(z.Bounds, pointer.View) is { } label
            && label.Contains(pointer.Position.X, pointer.Position.Y));
        if (header != null)
        {
            if (header.MissionPositionLocked) { ShowMission(header); return true; }
            return false; // The existing zone tool owns the movable frame header.
        }

        return TryHandleAgentCardClick(pointer);
    }

    private MissionAgentCard? HitMissionCard(CanvasPointer pointer)
        => MissionCards().LastOrDefault(c =>
        {
            var bounds = pointer.View.WorldToDip(c.Bounds);
            return bounds.Width >= 6 && bounds.Height >= 4
                && CanvasSelection.Contains(c.Bounds, pointer.World);
        });
}
