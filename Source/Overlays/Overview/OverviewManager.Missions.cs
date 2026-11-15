using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly HerdrSessions _herdrSessions = new();
    private MissionCardLayer? _missionCards;
    private MissionControlWindow? _missionControl;
    internal MissionControlWindow? MissionControl => _missionControl;
    private System.Windows.Forms.Timer? _missionStatusTimer;
    private CancellationTokenSource? _missionStatusRequest;
    private Dictionary<Guid, HerdrSessions.Terminal> _missionTerminals = new();
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
            _missionTerminals = await _herdrSessions.Snapshot(request.Token);
            _herdrDisconnected = false;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or System.Text.Json.JsonException or System.IO.IOException or ArgumentException or KeyNotFoundException)
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
        var window = new MissionControlWindow(CanvasPanelOwner, _herdrSessions);
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
            _annotationToolbar?.SetAiTeamsActive(false);
            _unifiedDock?.Panel.SetAiTeamsActive(false);
        };
        _annotationToolbar?.SetAiTeamsActive(true);
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
        _missionStatusRequest?.Cancel();
        _missionStatusTimer?.Dispose();
        _missionStatusTimer = null;
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

    private void ToggleAgentSession(MissionAgentCard card)
    {
        if (_disposed) return;
        if (IsAgentSessionLive(card.Id))
            StopAgentSession(card);
        else
            StartAgentSession(card);
    }

    private async void StartAgentSession(MissionAgentCard card)
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
            var terminal = await _herdrSessions.StartAgent(agent, folder, card.Id, cts.Token, card.Prompt);
            _missionTerminals[card.Id] = terminal;
            _herdrDisconnected = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        finally
        {
            RefreshMissionTerminals();
            _visualFrames.Request();
        }
    }

    private async void StopAgentSession(MissionAgentCard card)
    {
        if (_disposed) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _herdrSessions.StopAgent(card.Id, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
        _visualFrames.Request();
    }

    internal bool TryHandleAgentCardClick(CanvasPointer pointer)
    {
        var card = HitMissionCard(pointer);
        if (card == null) return false;

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
