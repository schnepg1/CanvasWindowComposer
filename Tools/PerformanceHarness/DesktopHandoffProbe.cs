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
    private const uint PositionChanging = 0x0046, LeftDown = 0x0201, LeftUp = 0x0202;
    private const uint PreserveGeometryAndActivation = 0x0013, NoPositionChanging = 0x0400;
    private const int Acknowledgement = 73, HoldMilliseconds = 450;

    public static void Run(string output)
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
                start.Y.ToString(CultureInfo.InvariantCulture) }
        };
        using var target = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start owned target.");
        var canvas = new Canvas();
        var windows = new WindowManager(canvas, native, new BenchmarkConfig(), new BenchmarkInput(),
            useAsyncProjection: true);
        nint window = 0;
        Exception? failure = null;
        object? report = null;
        Task<PlacementCompletion>? inFlight = null;
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
                Check(lane.TrySubmit(new(cover.Handle, final, PosOnly: true)) == null,
                    "A Canvas-owned window must stay on the UI thread.");
                Check(SetWindowPos(cover.Handle, new nint(-1), 0, 0, 0, 0, PreserveGeometryAndActivation), "Could not raise the cover.");
                nint hit = WindowFromPoint(hitPoint);
                Check(hit == cover.Handle, $"Hit point {hitPoint} returned {hit} (PID {native.GetWindowProcessId(hit)}), expected cover {cover.Handle} at {ReadBounds(native, cover.Handle)}. Visible={cover.Visible}.");
                nint foreground = GetForegroundWindow();
                heartbeat.Start();

                var obsolete = Submit(intermediate);
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
                var captureLost = Submit(afterCaptureLoss);
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

                var current = Submit(final);
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

                Task<PlacementCompletion> Submit(WindowRect rectangle)
                    => inFlight = lane.TrySubmit(new(window, rectangle, PosOnly: true))
                        ?? throw new InvalidOperationException("Placement lane is occupied.");

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
                    Check(lane.TrySubmit(new(window, final, PosOnly: true)) == null, "The lane accepted a second native writer.");
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
                if (inFlight is { IsCompleted: false })
                {
                    try { await inFlight.WaitAsync(TimeSpan.FromSeconds(6)); }
                    catch (Exception ex) { failure ??= ex; }
                }
                cover.Close();
            }
        };
        Application.Run(cover);
        if (inFlight is not { IsCompleted: false }) windows.Dispose();
        if (!target.WaitForExit(5000))
        {
            target.Kill();
            target.WaitForExit(5000);
            throw new InvalidOperationException("Owned target did not exit on request.", failure);
        }
        if (failure != null) throw new InvalidOperationException("Desktop handoff probe failed.", failure);
        Check(target.ExitCode == 0 && report != null, "Owned target failed during the probe.");
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
        using var target = new Target(cover, release, bounds);
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

        public Task<PlacementCompletion>? TrySubmit(BatchMoveItem item)
        {
            Check(Environment.CurrentManagedThreadId == _uiThread, "Only the UI thread may submit placement.");
            if (Busy || targetPid == Environment.ProcessId || native.GetWindowProcessId(item.HWnd) != targetPid) return null;
            var lease = windows.TryAcquireDesktopPlacementLease([item.HWnd]);
            if (lease == null) return null;
            if (close.TryBeginPlacement() is not long generation)
            {
                lease.Dispose();
                return null;
            }
            Busy = true;
            return Execute(lease, item, generation);
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

    private sealed class Target(nint cover, EventWaitHandle release, Rectangle bounds) : OwnedWindow(bounds)
    {
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
                Failed = SendMessageTimeout(cover, Callback, 0, 0, 2, 3000, out nint response) == 0
                    || response != Acknowledgement || !PostMessage(cover, CallbackReturned, 0, 0)
                    || !release.WaitOne(TimeSpan.FromSeconds(5));
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
