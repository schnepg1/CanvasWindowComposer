using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CanvasDesktop.PerformanceHarness;

/// <summary>Owned-process feasibility check for keeping a WinForms cover active during native placement.</summary>
internal static class DesktopHandoffProbe
{
    private const uint Callback = 0x8001, CallbackReturned = 0x8002, Close = 0x0010;
    private const uint OrderTargetBehindCover = 0x8003, TargetOrdered = 0x8004;
    private const uint BlockTargetQueue = 0x8005, TargetQueueBlocked = 0x8006, TargetQueueReleased = 0x8007;
    private const uint QueryEndSession = 0x0011, EndSession = 0x0016;
    private const uint PositionChanging = 0x0046, LeftDown = 0x0201, LeftUp = 0x0202;
    private const uint PreserveGeometryAndActivation = 0x0013, NoPositionChanging = 0x0400;
    private const int Acknowledgement = 73, HoldMilliseconds = 450;

    public static void Run(string output, bool verifyRecovery = false, bool verifyPlacement = false,
        bool verifyStallRecovery = false, bool requestShutdown = false)
    {
        var work = Screen.PrimaryScreen!.WorkingArea;
        Check(work.Width >= 500 && work.Height >= 260, "The probe needs a 500 by 260 pixel working area.");
        var bounds = new Rectangle(work.X + (work.Width - 500) / 2, work.Y + (work.Height - 260) / 2, 500, 260);
        var start = new Point(bounds.X + 20, bounds.Y + 100);
        var hitPoint = new Point(bounds.X + 320, start.Y + 20);
        string gateName = $@"Local\CanvasHandoff-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.AutoReset, gateName);
        var close = new DesktopHandoffClose();
        using var cover = new Cover(bounds, close);
        var diagnostics = new PerformanceDiagnostics();
        var native = new Win32WindowApi(diagnostics: diagnostics);
        var startInfo = new ProcessStartInfo(Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            ArgumentList = { "--desktop-handoff-target", cover.Handle.ToString(), gateName,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture), start.X.ToString(CultureInfo.InvariantCulture),
                start.Y.ToString(CultureInfo.InvariantCulture), (verifyPlacement || verifyStallRecovery).ToString(CultureInfo.InvariantCulture),
                verifyStallRecovery.ToString(CultureInfo.InvariantCulture) }
        };
        using var target = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start owned target.");
        var canvas = new Canvas();
        var config = new BenchmarkConfig { DesktopPanMomentum = false };
        var input = new BenchmarkInput();
        var windows = new WindowManager(canvas, native, config, input, useAsyncProjection: true);
        nint window = 0;
        Exception? failure = null;
        object? report = null;
        bool targetTerminated = false;
        Task<PlacementCompletion>? inFlight = null;
        Task<bool>? managerPlacement = null;
        OverviewManager? plannedOverview = null;
        int ticks = 0;
        using var heartbeat = new System.Windows.Forms.Timer { Interval = 16 };
        heartbeat.Tick += (_, _) => ticks++;
        cover.Shown += async (_, _) =>
        {
            try
            {
                string? handle = await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                window = (nint)long.Parse(handle ?? throw new InvalidOperationException("Target exited during startup."), CultureInfo.InvariantCulture);
                var lane = new PlacementLane(windows, native, (uint)target.Id, close, cover);
                var initial = Observed();
                Check(native.GetWindowProcessId(window) == target.Id && initial.X == start.X && initial.Y == start.Y
                    && initial.W > 20 && initial.H > 20, $"Unexpected owned target bounds {initial} at {start}.");
                // USER32 may enlarge the requested form to its system minimum size.
                var intermediate = initial with { X = bounds.X + 160 };
                var final = initial with { X = bounds.X + 300 };
                Check(lane.TrySubmit(new(cover.Handle, final, PosOnly: true), out _) == null,
                    "A Canvas-owned window must stay on the UI thread.");
                Check(SetWindowPos(cover.Handle, new nint(-1), 0, 0, 0, 0, PreserveGeometryAndActivation), "Could not raise the cover.");
                nint hit = WindowFromPoint(hitPoint);
                Check(hit == cover.Handle, $"Hit point {hitPoint} returned {hit} (PID {native.GetWindowProcessId(hit)}), expected cover {cover.Handle} at {ReadBounds(native, cover.Handle)}. Visible={cover.Visible}.");
                nint foreground = GetForegroundWindow();
                heartbeat.Start();

                if (verifyPlacement || verifyStallRecovery)
                {
                    windows.RegisterWindow(window);
                    Check(canvas.HasWindow(window), "The owned target was not enrolled in the placement model.");
                    plannedOverview = new OverviewManager(canvas, windows, native, input, config,
                        requestDesktopPlacement: () => managerPlacement = windows.TryStartDesktopHandoffPlacement());
                    input.BeginPan();
                    var overlay = (OverviewOverlay)Control.FromHandle(plannedOverview.MonitorHandles[0])!;
                    input.Pan(final.X - initial.X, 0);
                    input.ReleasePan();
                    Check(managerPlacement != null, "WindowManager declined the owned placement cohort.");

                    await WaitFor(() => cover.ReturnedCallbacks == 1, "The planned native placement did not call back into the UI.");
                    if (verifyStallRecovery)
                    {
                        report = await VerifyStallRecovery(plannedOverview, overlay, managerPlacement!, initial, final, foreground);
                        return;
                    }
                    int before = ticks;
                    await Task.Delay(HoldMilliseconds);
                    Check(ticks > before && managerPlacement is { IsCompleted: false } && windows.HasDesktopPlacementReservation
                        && Observed() == initial && overlay.Visible && plannedOverview.CurrentMode == OverviewMode.Panning,
                        "The planned placement did not retain the responsive Overview cover.");
                    int uiTicks = ticks - before;
                    overlay.OnMouseButtonDown!(overlay, new(MouseButtons.Left, 1, 200, 200, 0));
                    Check(overlay.Capture, "Overview did not capture the stopping click.");
                    release.Set();
                    Check(await managerPlacement!.WaitAsync(TimeSpan.FromSeconds(6)) && Observed() == final
                        && windows.HasDesktopPlacementReservation && overlay.Visible,
                        "The verified placement did not stay reserved under the held click.");
                    overlay.OnMouseButtonUp!(overlay, new(MouseButtons.Left, 1, 200, 200, 0));
                    await WaitFor(() => plannedOverview.CurrentMode == OverviewMode.Hidden && !overlay.Visible
                        && !windows.HasDesktopPlacementReservation, "The verified placement did not close the real Overview.");
                    report = new { TargetPid = target.Id, Initial = initial, Verified = final, UiTimerTicks = uiTicks,
                        RealOverviewClosedAfterVerification = true,
                        ForegroundPreserved = GetForegroundWindow() == foreground,
                        NativeWait = diagnostics.Snapshot().Operations.Single(op => op.Operation == "EndDeferWindowPos") };
                    Check(GetForegroundWindow() == foreground, "The planned placement changed foreground activation.");
                    return;
                }

                if (verifyRecovery)
                {
                    report = await VerifyRecovery();
                    return;
                }

                async Task<object> VerifyStallRecovery(OverviewManager overview, OverviewOverlay overlay, Task<bool> placement,
                    WindowRect beforeMove, WindowRect destination, nint initialForeground)
                {
                    var recovery = overlay.OwnedForms.OfType<DesktopHandoffRecoveryWindow>().Single();
                    var open = recovery.Controls.OfType<Button>().Single();
                    var originalCursor = Cursor.Position;
                    int hideCalls = 0;
                    int shutdownCalls = 0;
                    overview.AfterModeChanged += (_, mode) => { if (mode == OverviewMode.Hidden) hideCalls++; };
                    var desktopCamera = canvas.Camera;
                    var savedView = overview.ViewCamera;
                    long startTime = Stopwatch.GetTimestamp();
                    try
                    {
                        Click(open);
                        await WaitFor(() => recovery.RecoveryCamera.HasValue, "The recovery button did not open the read-only map.");
                        double openMs = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                        var layout = recovery.Controls.OfType<TableLayoutPanel>().Single();
                        var zoomOut = layout.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>()
                            .Single(button => button.Text == "Zoom out");
                        Click(zoomOut);
                        await WaitFor(() => recovery.RecoveryCamera is { } view && view.Zoom < savedView.Zoom,
                            "The read-only map did not respond to the zoom button.");
                        var recoveredView = recovery.RecoveryCamera;
                        if (requestShutdown)
                        {
                            Check(windows.TryDeferShutdownForDesktopPlacement(() => cover.BeginInvoke(() =>
                            {
                                shutdownCalls++;
                                overview.TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
                            })), "Shutdown escaped the held placement reservation.");
                            overview.TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
                            Check(overview.TryOpenDesktopHandoffRecovery(), "A recovery request during shutdown reached normal commands.");
                        }

                        int beforeTicks = ticks;
                        long holdStart = Stopwatch.GetTimestamp();
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        double heldMs = Stopwatch.GetElapsedTime(holdStart).TotalMilliseconds;
                        int uiTicks = ticks - beforeTicks;
                        Check(uiTicks > 0 && !placement.IsCompleted && !target.HasExited
                            && overview.DesktopHandoffPending && windows.HasDesktopPlacementReservation
                            && overview.CurrentMode == OverviewMode.Panning && overlay.Visible && recovery.Visible
                            && hideCalls == 0 && shutdownCalls == 0 && canvas.Camera == desktopCamera && overview.ViewCamera == savedView
                            && Observed() == beforeMove && GetForegroundWindow() == initialForeground,
                            "The recovery view changed placement, cover, shutdown ordering or foreground while the target was blocked.");
                        CheckPlacementRejected("A second placement was accepted during recovery.");
                        CheckCovered(beforeMove);
                        CheckCovered(destination);

                        release.Set();
                        bool verified = await placement.WaitAsync(TimeSpan.FromSeconds(6));
                        Check(verified != requestShutdown && Observed() == destination,
                            "The native result or geometry did not match the expected drain.");
                        await WaitFor(() => !overview.DesktopHandoffPending && !windows.HasDesktopPlacementReservation
                            && overview.CurrentMode == (requestShutdown ? OverviewMode.Hidden : OverviewMode.Zooming),
                            "The recovery view or shutdown did not complete after the target drained.");
                        Check(recovery.IsDisposed, "The recovery window survived handoff cleanup.");
                        if (requestShutdown)
                            Check(shutdownCalls == 1 && hideCalls == 1 && !overlay.Visible,
                                "Deferred shutdown did not finish once after placement drained.");
                        else
                        {
                            Check(hideCalls == 0 && overlay.Visible && overview.ViewCamera == recoveredView,
                                "Late placement bypassed the recovered Overview view.");
                            CheckCovered(destination);
                        }
                        return new { TargetPid = target.Id, Initial = beforeMove, Landed = destination, OpenMs = openMs,
                            HeldAfterActionMs = heldMs, UiTimerTicks = uiTicks, InputRoutedToRecovery = true,
                            MapZoomedWhileTargetBlocked = true, PlacementFenceRetained = true, ForegroundPreservedWhileBlocked = true,
                            LateCompletionKeptCover = !requestShutdown, ShutdownRequestedWhileBlocked = requestShutdown,
                            ShutdownRecoveryCalls = shutdownCalls, HiddenTransitions = hideCalls,
                            NativeWait = diagnostics.Snapshot().Operations.Single(op => op.Operation == "EndDeferWindowPos") };
                    }
                    finally { Cursor.Position = originalCursor; }

                    void Click(Control control)
                    {
                        var point = control.PointToScreen(new(control.Width / 2, control.Height / 2));
                        Check(WindowFromPoint(point) == control.Handle, $"The recovery control at {point} was not hit-testable.");
                        Cursor.Position = point;
                        MouseEvent(0x0002, 0, 0, 0, 0); // MOUSEEVENTF_LEFTDOWN
                        MouseEvent(0x0004, 0, 0, 0, 0); // MOUSEEVENTF_LEFTUP
                    }

                    void CheckCovered(WindowRect rect)
                    {
                        nint hit = WindowFromPoint(new(rect.X + rect.W / 2, rect.Y + rect.H / 2));
                        Check(native.GetWindowProcessId(hit) == Environment.ProcessId, "A native target position was exposed.");
                    }
                }

                var obsolete = Submit(intermediate, out _);
                int obsoleteTicks = await HoldNativeCall(1, obsolete, initial);
                close.Invalidate();
                release.Set();
                var stale = await obsolete.WaitAsync(TimeSpan.FromSeconds(6));
                Check(stale.PlacementVerified && stale.Observed == intermediate,
                    "The obsolete native move did not land at the expected position.");
                Check(stale.CloseDecision == DesktopHandoffCloseDecision.KeepCovered,
                    "An obsolete native move authorized cover removal.");
                Check(cover.Visible && WindowFromPoint(hitPoint) == cover.Handle, "An obsolete result exposed the desktop.");

                nint pointInCover = (hitPoint.X - bounds.X) | ((hitPoint.Y - bounds.Y) << 16);
                var afterCaptureLoss = initial with { X = bounds.X + 220 };
                var captureLost = Submit(afterCaptureLoss, out _);
                int captureLostTicks = await HoldNativeCall(2, captureLost, intermediate);
                SendMessage(cover.Handle, LeftDown, 1, pointInCover);
                Check(cover.StoppingClickHeld && cover.Capture, "The cover did not capture the test click.");
                cover.Capture = false;
                Check(!cover.StoppingClickHeld && !cover.Capture, "Losing capture did not invalidate the held click.");
                release.Set();
                var lost = await captureLost.WaitAsync(TimeSpan.FromSeconds(6));
                Check(lost.PlacementVerified && lost.Observed == afterCaptureLoss
                    && lost.CloseDecision == DesktopHandoffCloseDecision.KeepCovered,
                    "Placement after capture loss authorized cover removal.");
                Check(cover.Visible && WindowFromPoint(hitPoint) == cover.Handle, "Capture loss exposed the desktop.");

                var current = Submit(final, out _);
                int currentTicks = await HoldNativeCall(3, current, afterCaptureLoss);
                SendMessage(cover.Handle, LeftDown, 1, pointInCover);
                Check(cover.StoppingClickHeld && cover.Capture, "The cover did not capture the stopping click.");
                release.Set();
                var completed = await current.WaitAsync(TimeSpan.FromSeconds(6));
                Check(completed.PlacementVerified && completed.Observed == final, "Current completion did not confirm final placement.");
                Check(completed.CloseDecision == DesktopHandoffCloseDecision.WaitForButtonUp,
                    "Verified placement must wait for the held stopping click.");
                // Keep the fixture in front of unrelated windows without activating
                // either owned window or letting the target overtake the cover.
                Check(PostMessage(window, OrderTargetBehindCover, 0, 0), "Could not request owned target ordering.");
                await WaitFor(() => cover.OwnedTargetOrdered, "The owned target did not confirm its order behind the cover.");
                Check(cover.Visible && WindowFromPoint(hitPoint) == cover.Handle, "Completion exposed native input before button-up.");
                SendMessage(cover.Handle, LeftUp, 0, pointInCover);
                nint afterHit = WindowFromPoint(hitPoint);
                Check(!cover.Visible && !cover.Capture && afterHit == window,
                    $"Button-up left Visible={cover.Visible}, Capture={cover.Capture}, and hit {afterHit} (PID {native.GetWindowProcessId(afterHit)}), expected target {window}.");
                Check(GetForegroundWindow() == foreground, "The handoff changed foreground activation.");

                var queued = final with { X = final.X - 60 };
                Check(PostMessage(window, BlockTargetQueue, 0, 0), "Could not hold the target's message queue.");
                await WaitFor(() => cover.OwnedTargetQueueBlocked, "The target did not hold its message queue.");
                canvas.SetWindowFromScreen(window, queued.X, queued.Y, queued.W, queued.H);
                windows.Reproject(isAsync: true, isTransient: true);
                await WaitFor(() => diagnostics.Snapshot().Operations.Any(op => op.Operation == "SetWindowPos" && op.Queued == 1),
                    "The projection worker did not queue the native move.");
                var beforeDrain = Observed();
                Check(!cover.OwnedTargetQueueReleased && (beforeDrain == final || beforeDrain == queued),
                    $"Unexpected queued target state {beforeDrain} before release.");
                CheckPlacementRejected("The manager reserved a target with a queued placement.");
                release.Set();
                await WaitFor(() => cover.OwnedTargetQueueReleased && Observed() == queued,
                    "The earlier asynchronous placement did not reach the target.");
                CheckPlacementRejected("Rectangle equality was treated as a native queue acknowledgement.");

                var operations = diagnostics.Snapshot().Operations;
                var queuedMove = operations.SingleOrDefault(op => op.Operation == "SetWindowPos");
                Check(queuedMove?.Queued == 1, "Expected one queued native call.");
                var nativeWait = operations.SingleOrDefault(op => op.Operation == "EndDeferWindowPos");
                Check(nativeWait?.Succeeded == 3 && nativeWait.MaxMs >= HoldMilliseconds, "Expected three successful deferred native calls with a controlled wait.");
                report = new { TargetPid = target.Id, HoldMilliseconds, Callbacks = cover.ReturnedCallbacks,
                    StaleCompletion = stale, StaleUiTimerTicks = obsoleteTicks,
                    CaptureLostCompletion = lost, CaptureLostUiTimerTicks = captureLostTicks,
                    FinalCompletion = completed, FinalUiTimerTicks = currentTicks, NativeWait = nativeWait,
                    QueuedMove = queuedMove, PositionBeforeQueueRelease = beforeDrain, QueuedTarget = queued,
                    QueuedTargetRejectedBeforeAndAfterObservation = true,
                    StaleCloseDecision = stale.CloseDecision.ToString(), FinalCloseDecision = completed.CloseDecision.ToString(),
                    CoveredThroughButtonUp = true, ForegroundPreserved = true };

                Task<PlacementCompletion> Submit(WindowRect rectangle, out long generation)
                    => inFlight = lane.TrySubmit(new(window, rectangle, PosOnly: true), out generation)
                        ?? throw new InvalidOperationException("Placement lane is occupied.");

                async Task<object> VerifyRecovery()
                {
                    var pending = Submit(intermediate, out long pendingGeneration);
                    int pendingTicks = await HoldNativeCall(1, pending, initial);

                    // Exercise a UI action during the wait, without releasing the native writer.
                    using var continueButton = new Button { Text = "Keep using Canvas", AutoSize = true, Location = new(8, 8) };
                    cover.Controls.Add(continueButton);
                    bool keptUsingCanvas = false;
                    continueButton.Click += (_, _) => keptUsingCanvas = close.TryAbandonPendingClose(pendingGeneration);
                    continueButton.PerformClick();
                    Check(keptUsingCanvas && cover.Visible && !pending.IsCompleted,
                        "The recovery action could not abandon the close while USER32 was blocked.");
                    CheckPlacementRejected("The recovery action released an in-flight placement lease.");

                    release.Set();
                    var abandoned = await pending.WaitAsync(TimeSpan.FromSeconds(6));
                    Check(abandoned.PlacementVerified && abandoned.Observed == intermediate
                        && abandoned.CloseDecision == DesktopHandoffCloseDecision.KeepCovered,
                        "Late completion of the abandoned placement exposed the desktop.");

                    var ending = Submit(final, out _);
                    int endingTicks = await HoldNativeCall(2, ending, intermediate);
                    bool shutdownDeferred = false;
                    int shutdownRecoveryCalls = 0;
                    cover.CommittedShutdown += () => shutdownDeferred = windows.TryDeferShutdownForDesktopPlacement(() =>
                    {
                        Check(close.CanRunShutdownRecovery, "Manager invoked shutdown before the UI processed placement.");
                        shutdownRecoveryCalls++;
                    });
                    long queryStarted = Stopwatch.GetTimestamp();
                    nint queryResult = SendMessage(cover.Handle, QueryEndSession, 0, 0);
                    double queryMs = Stopwatch.GetElapsedTime(queryStarted).TotalMilliseconds;
                    Check(queryResult == 1 && !close.ShutdownRequested,
                        "The fixture rejected sign-out or started irreversible work during the query.");
                    SendMessage(cover.Handle, EndSession, 0, 0);
                    Check(!close.ShutdownRequested, "A cancelled sign-out requested shutdown.");
                    long endStarted = Stopwatch.GetTimestamp();
                    SendMessage(cover.Handle, EndSession, 1, 0);
                    double endMs = Stopwatch.GetElapsedTime(endStarted).TotalMilliseconds;
                    Check(close.ShutdownRequested && shutdownDeferred && shutdownRecoveryCalls == 0
                        && !close.CanRunShutdownRecovery && !ending.IsCompleted,
                        "Sign-out started native recovery before the in-flight placement drained.");
                    CheckPlacementRejected("A second placement was accepted during sign-out.");

                    // This is the owned fixture target. Simulate its process exiting during sign-out.
                    target.Kill();
                    targetTerminated = true;
                    await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
                    var drained = await ending.WaitAsync(TimeSpan.FromSeconds(6));
                    Check(!drained.PlacementVerified && drained.CloseDecision == DesktopHandoffCloseDecision.KeepCovered
                        && close.CanRunShutdownRecovery && shutdownRecoveryCalls == 1 && cover.Visible && !lane.Busy,
                        "Target exit did not drain placement for safe shutdown recovery.");
                    Check(close.TryBeginPlacement() == null, "A new close was accepted after committed sign-out.");
                    CheckPlacementRejected("The manager accepted new native placement after committed sign-out.");
                    Check(GetForegroundWindow() == foreground, "The recovery probe changed foreground activation.");

                    return new
                    {
                        TargetPid = target.Id, HoldMilliseconds, PendingUiTimerTicks = pendingTicks,
                        EndingUiTimerTicks = endingTicks, QueryEndSessionMs = queryMs, EndSessionMs = endMs,
                        AbandonedPlacement = abandoned, DrainedPlacement = drained,
                        RecoveryActionWhileBlocked = true, LateCompletionKeptCover = true,
                        ShutdownRecoveryWaitedForTargetExit = true, ShutdownRecoveryCalls = shutdownRecoveryCalls
                    };
                }

                void CheckPlacementRejected(string message)
                {
                    using var unexpected = windows.TryAcquireDesktopPlacementLease([window]);
                    Check(unexpected == null, message);
                }

                async Task<int> HoldNativeCall(int callbacks, Task<PlacementCompletion> completion, WindowRect expected)
                {
                    await WaitFor(() => cover.ReturnedCallbacks >= callbacks, "The target did not acknowledge the synchronous callback.");
                    Check(cover.ReturnedCallbacks == callbacks, "The target acknowledged an unexpected extra callback.");
                    int before = ticks;
                    await Task.Delay(HoldMilliseconds);
                    Check(ticks > before && !completion.IsCompleted && lane.Busy, "The UI timer must tick while USER32 is blocked.");
                    Check(lane.TrySubmit(new(window, final, PosOnly: true), out _) == null, "The lane accepted a second native writer.");
                    CheckPlacementRejected("The manager accepted a second placement lease.");
                    Check(Observed() == expected && cover.Visible && WindowFromPoint(hitPoint) == cover.Handle,
                        "The cover or native position changed before the owner released placement.");
                    return ticks - before;
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                heartbeat.Stop();
                release.Set();
                if (window != 0 && native.GetWindowProcessId(window) == target.Id)
                    PostMessage(window, Close, 0, 0);
                Task? pending = managerPlacement ?? (Task?)inFlight;
                if (pending is { IsCompleted: false })
                {
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(6)); }
                    catch (Exception ex) { failure ??= ex; }
                }
                try
                {
                    windows.ReleaseVerifiedDesktopHandoffPlacement();
                    plannedOverview?.Dispose();
                }
                catch (Exception ex) { failure ??= ex; }
                finally { cover.Close(); }
            }
        };
        Application.Run(cover);
        if (inFlight is not { IsCompleted: false } && managerPlacement is not { IsCompleted: false }) windows.Dispose();
        if (!target.WaitForExit(5000))
        {
            target.Kill();
            target.WaitForExit(5000);
            throw new InvalidOperationException("Owned target did not exit on request.", failure);
        }
        if (failure != null) throw new InvalidOperationException("Desktop handoff probe failed.", failure);
        Check((targetTerminated || target.ExitCode == 0) && report != null, "Owned target failed during the probe.");
        string path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(path);

        WindowRect Observed() => ReadBounds(native, window);
    }

    public static void RunTarget(string[] args)
    {
        nint cover = (nint)long.Parse(args[0], CultureInfo.InvariantCulture);
        using var release = EventWaitHandle.OpenExisting(args[1]);
        using var parent = Process.GetProcessById(int.Parse(args[2], CultureInfo.InvariantCulture));
        Check(new Win32WindowApi().GetWindowProcessId(cover) == parent.Id, "The cover belongs to a different process.");
        var bounds = new Rectangle(int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture), 100, 100);
        bool waitIndefinitely = bool.Parse(args[6]);
        using var target = new Target(cover, release, bounds, bool.Parse(args[5]), waitIndefinitely);
        if (waitIndefinitely)
        {
            parent.Exited += (_, _) => release.Set();
            parent.EnableRaisingEvents = true;
        }
        using var checkParent = new System.Windows.Forms.Timer { Interval = 100 };
        checkParent.Tick += (_, _) => { if (parent.HasExited) target.Close(); };
        target.Shown += (_, _) =>
        {
            target.Arm();
            Console.WriteLine(target.Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
            checkParent.Start();
        };
        Application.Run(target);
        Check(!target.Failed, "The synchronous callback or controlled target wait failed.");
    }

    private static async Task WaitFor(Func<bool> ready, string failure)
    {
        long started = Stopwatch.GetTimestamp();
        while (!ready() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(4))
            await Task.Delay(10);
        Check(ready(), failure);
    }

    private static WindowRect ReadBounds(Win32WindowApi native, nint handle)
    {
        var (x, y, w, h) = native.GetWindowRect(handle);
        return new(x, y, w, h);
    }

    private sealed record PlacementCompletion(long Generation, bool PlacementVerified, WindowRect Observed,
        DesktopHandoffCloseDecision CloseDecision);

    // The worker reads the immutable request and performs the synchronous foreign HWND move.
    // The WinForms thread verifies the result and decides whether to remove the cover.
    private sealed class PlacementLane(WindowManager windows, Win32WindowApi native, uint targetPid,
        DesktopHandoffClose close, Cover cover)
    {
        private readonly int _uiThread = Environment.CurrentManagedThreadId;
        public bool Busy { get; private set; }

        public Task<PlacementCompletion>? TrySubmit(BatchMoveItem item, out long generation)
        {
            generation = 0;
            Check(Environment.CurrentManagedThreadId == _uiThread, "Only the UI thread may submit placement.");
            if (Busy || targetPid == Environment.ProcessId || native.GetWindowProcessId(item.HWnd) != targetPid) return null;
            var lease = windows.TryAcquireDesktopPlacementLease([item.HWnd]);
            if (lease == null) return null;
            if (close.TryBeginPlacement() is not long request)
            {
                lease.Dispose();
                return null;
            }
            generation = request;
            Busy = true;
            return Execute(lease, item, request);
        }

        private async Task<PlacementCompletion> Execute(ProjectionWorker.PlacementLease lease, BatchMoveItem item, long generation)
        {
            using var reservation = lease;
            try
            {
                var result = await lease.Submit([(item, targetPid)]);
                Check(Environment.CurrentManagedThreadId == _uiThread, "Placement completion must run on the UI thread.");
                var observed = ReadBounds(native, item.HWnd);
                bool verified = result.Summary.Status == NativeOperationStatus.Succeeded
                    && native.GetWindowProcessId(item.HWnd) == targetPid && observed == item.Rect;
                var decision = cover.Accept(generation, verified);
                return new(generation, verified, observed, decision);
            }
            catch
            {
                close.CompletePlacement(generation, placementVerified: false);
                throw;
            }
            finally { Busy = false; }
        }
    }

    private abstract class OwnedWindow : Form
    {
        protected OwnedWindow(Rectangle bounds)
        {
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Bounds = bounds;
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.ExStyle |= 0x08000000; return parameters; }
        }
    }

    private sealed class Cover : OwnedWindow
    {
        private readonly DesktopHandoffClose _close;
        public int ReturnedCallbacks { get; private set; }
        public bool OwnedTargetOrdered { get; private set; }
        public bool OwnedTargetQueueBlocked { get; private set; }
        public bool OwnedTargetQueueReleased { get; private set; }
        public bool StoppingClickHeld => _close.StoppingClickHeld;
        public event Action? CommittedShutdown;

        public Cover(Rectangle bounds, DesktopHandoffClose close) : base(bounds)
        {
            _close = close;
            BackColor = Color.MidnightBlue;
        }

        public DesktopHandoffCloseDecision Accept(long generation, bool placementVerified)
        {
            var decision = _close.CompletePlacement(generation, placementVerified);
            if (decision == DesktopHandoffCloseDecision.ExposeDesktop) Hide();
            return decision;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture) _close.LoseStoppingClickCapture();
            base.OnMouseCaptureChanged(e);
        }

        protected override void WndProc(ref Message message)
        {
            switch ((uint)message.Msg)
            {
                case QueryEndSession:
                    message.Result = 1;
                    return;
                case EndSession:
                    if (message.WParam != 0)
                    {
                        _close.RequestShutdown();
                        CommittedShutdown?.Invoke();
                    }
                    message.Result = 0;
                    return;
                case Callback:
                    message.Result = Acknowledgement;
                    return;
                case CallbackReturned:
                    ReturnedCallbacks++;
                    return;
                case TargetOrdered:
                    OwnedTargetOrdered = true;
                    return;
                case TargetQueueBlocked:
                    OwnedTargetQueueBlocked = true;
                    return;
                case TargetQueueReleased:
                    OwnedTargetQueueReleased = true;
                    return;
                case LeftDown:
                    if (_close.HoldStoppingClick()) Capture = true;
                    return;
                case LeftUp when StoppingClickHeld:
                    var decision = _close.ReleaseStoppingClick();
                    Capture = false;
                    if (decision == DesktopHandoffCloseDecision.ExposeDesktop) Hide();
                    return;
                default:
                    base.WndProc(ref message);
                    break;
            }
        }
    }

    private sealed class Target(nint cover, EventWaitHandle release, Rectangle bounds, bool enrollInCanvas, bool waitIndefinitely) : OwnedWindow(bounds)
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                if (enrollInCanvas) parameters.ExStyle |= 0x00040000; // WS_EX_APPWINDOW
                return parameters;
            }
        }

        private bool _armed;
        public bool Failed { get; private set; }
        public void Arm() => _armed = true;

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == BlockTargetQueue)
            {
                _armed = false;
                Failed = !PostMessage(cover, TargetQueueBlocked, 0, 0) || !release.WaitOne(TimeSpan.FromSeconds(5))
                    || !PostMessage(cover, TargetQueueReleased, 0, 0);
                if (Failed) BeginInvoke(Close);
                return;
            }
            if (message.Msg == OrderTargetBehindCover)
            {
                Failed = !SetWindowPos(Handle, cover, 0, 0, 0, 0, PreserveGeometryAndActivation | NoPositionChanging)
                    || !PostMessage(cover, TargetOrdered, 0, 0);
                if (Failed) BeginInvoke(Close);
                return;
            }
            if (_armed && message.Msg == PositionChanging && !Failed)
            {
                // The manager-planned probes own one controlled move. Cleanup can
                // send further position messages after that move drains.
                if (enrollInCanvas) _armed = false;
                Failed = SendMessageTimeout(cover, Callback, 0, 0, 2, 3000, out nint response) == 0
                    || response != Acknowledgement || !PostMessage(cover, CallbackReturned, 0, 0)
                    || !release.WaitOne(waitIndefinitely ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(5));
                if (Failed) BeginInvoke(Close);
            }
            base.WndProc(ref message);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, nuint extraInfo);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
