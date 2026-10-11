using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CanvasDesktop;

namespace CanvasDesktop.PerformanceHarness;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            if (args.Length > 0 && args[0] == "--target") { RunTarget(args); return 0; }
            return Run(args);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int Run(string[] args)
    {
        string Option(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? fallback;
        int count = int.Parse(Option("--count", "8")), repeats = int.Parse(Option("--repeats", "3"));
        double seconds = double.Parse(Option("--seconds", "5"), System.Globalization.CultureInfo.InvariantCulture);
        if (count < 1 || count > 100 || repeats < 1 || repeats > 20 || seconds < 1 || seconds > 60)
            throw new ArgumentException("Use count 1..100, repeats 1..20 and seconds 1..60.");
        string output = Path.GetFullPath(Option("--output", "performance-baseline.json"));
        using var workload = new OwnedWorkload(count, args.Contains("--always-work"));
        var target = workload.Process;
        string handlesPath = workload.HandlesPath;
        var diagnostics = PerformanceDiagnostics.Shared;
        var config = new BenchmarkConfig();
        var input = new BenchmarkInput();
        var canvas = new Canvas();
        var api = new Win32WindowApi(diagnostics: diagnostics);
        using var wm = new WindowManager(canvas, api, config, input, useAsyncProjection: true);
        using var overview = new OverviewManager(canvas, wm, api, input, config);
        var results = new List<object>();
        var processes = new List<Process> { Process.GetCurrentProcess(), target };
        var dwm = Process.GetProcessesByName("dwm");
        foreach (var process in dwm)
            if (process.SessionId == processes[0].SessionId) processes.Add(process); else process.Dispose();
        IntPtr[] handlesForValidation = [];
        bool interrupted = false;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; interrupted = true; };
        Console.CancelKeyPress += cancel;
        try
        {
            PumpUntil(() => File.Exists(handlesPath) || target.HasExited, 10);
            if (target.HasExited) throw new InvalidOperationException("Workload exited before publishing its windows.");
            var handles = JsonSerializer.Deserialize<long[]>(File.ReadAllText(handlesPath))!.Select(h => (IntPtr)h).ToArray();
            handlesForValidation = handles;
            if (handles.Length != count || handles.Any(h => api.GetWindowProcessId(h) != target.Id))
                throw new InvalidOperationException("Workload HWND identities did not match the owned process.");
            bool includeOverview = !args.Contains("--skip-overview");
            if (includeOverview) { GridRenderer.CompileShaders(); overview.Warmup(); }
            string[] phases = includeOverview
                ? ["Visible", "OffScreenDefault", "Clipped", "Minimized", "StationaryOverview", "Panning", "Restoration"]
                : ["Visible", "OffScreenDefault", "Clipped", "Minimized", "Restoration"];
            var working = Screen.PrimaryScreen!.WorkingArea;
            var worlds = handles.Select((h, i) => new WorldRect { X = working.X + 40 + i % 4 * 340,
                Y = working.Y + 40 + i / 4 * 240, W = 320, H = 220 }).ToArray();

            // Rotate phase order between repeats to reduce consistent order bias.
            for (int repeat = 0; repeat < repeats && !interrupted; repeat++)
            foreach (string phase in phases.Skip(repeat % phases.Length).Concat(phases.Take(repeat % phases.Length)))
            {
                if (interrupted) break;
                VerifyOwnership();
                diagnostics.Reset();
                overview.TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
                wm.Reset();
                config.DisableGreedyDraw = phase is not ("Clipped" or "StationaryOverview" or "Panning");
                for (int i = 0; i < handles.Length; i++)
                {
                    var world = worlds[i];
                    if (phase is "OffScreenDefault" or "Clipped") world.X += 10000;
                    api.UnclipWindow(handles[i]);
                    api.RestoreNativeState(handles[i], new((int)world.X, (int)world.Y, (int)world.W, (int)world.H), WindowState.Normal);
                    wm.RestorePlacement(handles[i], world);
                    if (phase is "Minimized" or "Restoration")
                    {
                        api.RestoreNativeState(handles[i], new((int)world.X, (int)world.Y, 320, 220), WindowState.Minimized);
                        canvas.CollapseWindow(handles[i]);
                    }
                }
                if (phase is "StationaryOverview" or "Panning") overview.TransitionTo(phase == "Panning" ? OverviewMode.Panning : OverviewMode.Zooming);
                PumpUntil(() => interrupted, 0.3);
                var preparation = diagnostics.Snapshot();
                var nativeWindows = handles.Select(h =>
                {
                    var r = api.GetWindowRect(h);
                    return new { Handle = h.ToInt64(), X = r.x, Y = r.y, Width = r.w, Height = r.h, Style = api.GetWindowStyle(h) };
                }).ToArray();
                diagnostics.Reset();
                using var gpu = new GpuCounters();
                var baseline = processes.Select(Capture).ToArray();
                var latencies = new List<double>();
                long started = Stopwatch.GetTimestamp();
                double lastCycle = -1;
                bool restored = false;
                Console.WriteLine($"Repeat {repeat + 1}: {phase} ({seconds}s)");
                while (Stopwatch.GetElapsedTime(started).TotalSeconds < seconds && !interrupted)
                {
                    double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    if (phase == "Panning")
                    {
                        canvas.SetCamera(Math.Sin(elapsed * 2) * 100, Math.Cos(elapsed * 2) * 50);
                        overview.SyncCamera();
                    }
                    if (phase == "Restoration" && elapsed - lastCycle >= 0.5)
                    {
                        VerifyOwnership();
                        long restoreStarted = Stopwatch.GetTimestamp();
                        for (int i = 0; i < handles.Length; i++)
                        {
                            var world = worlds[i];
                            api.RestoreNativeState(handles[i], new((int)world.X, (int)world.Y, 320, 220),
                                restored ? WindowState.Minimized : WindowState.Normal);
                            if (restored) canvas.CollapseWindow(handles[i]); else wm.RestorePlacement(handles[i], world);
                        }
                        if (!restored) latencies.Add(Stopwatch.GetElapsedTime(restoreStarted).TotalMilliseconds);
                        restored = !restored; lastCycle = elapsed;
                    }
                    Application.DoEvents(); Thread.Sleep(10);
                }
                double duration = Stopwatch.GetElapsedTime(started).TotalSeconds;
                var after = processes.Select(Capture).ToArray();
                results.Add(new { Repeat = repeat + 1, Phase = phase, DurationSeconds = duration,
                    Processes = after.Select((sample, i) => new { sample.Pid, sample.Name, sample.Error,
                        CpuOneCorePercent = sample.CpuMs.HasValue && baseline[i].CpuMs.HasValue
                            ? (sample.CpuMs - baseline[i].CpuMs) / (duration * 10) : null,
                        sample.WorkingSetBytes, sample.PrivateBytes }),
                    GpuEngines = gpu.Read(processes.Select(p => p.Id).ToHashSet()), gpu.UnavailableReason,
                    NativeWindows = nativeWindows, PreparationDiagnostics = preparation,
                    RestoreBatchApiLatencyMs = latencies.ToArray(), Diagnostics = diagnostics.Snapshot() });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { SchemaVersion = 1, CapturedUtc = DateTime.UtcNow,
                OS = RuntimeInformation.OSDescription, Framework = RuntimeInformation.FrameworkDescription,
                LogicalProcessors = Environment.ProcessorCount, WindowCount = count, Repeats = repeats, SecondsPerPhase = seconds,
                Workload = args.Contains("--always-work") ? "always-work" : "cooperative-minimize",
                Monitors = Screen.AllScreens.Select(s => new { s.Bounds, s.WorkingArea }), Interrupted = interrupted, Results = results,
                Limits = "Synthetic GDI/timer workload; DWM includes unrelated desktop activity. CPU is percent of one core. GPU values are separate engine counters, not an overall sum. Restore timing is API acknowledgement, not first painted frame." }, Json));
            Console.WriteLine(output);
            return interrupted ? 2 : 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            try { overview.TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false); }
            finally
            {
                foreach (var process in processes) if (process != target) process.Dispose();
            }
        }

        void VerifyOwnership()
        {
            if (target.HasExited || handlesForValidation.Any(h => api.GetWindowProcessId(h) != target.Id))
                throw new InvalidOperationException("Owned workload exited or a window identity changed.");
        }
    }

    private sealed record ProcessSample(int Pid, string Name, double? CpuMs, long? WorkingSetBytes, long? PrivateBytes, string? Error);
    private static ProcessSample Capture(Process process)
    {
        try { process.Refresh(); return new(process.Id, process.ProcessName, process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64, process.PrivateMemorySize64, null); }
        catch (Exception ex) { return new(process.Id, "unavailable", null, null, null, ex.GetType().Name); }
    }
    private static void PumpUntil(Func<bool> done, double seconds)
    {
        long started = Stopwatch.GetTimestamp();
        while (!done() && Stopwatch.GetElapsedTime(started).TotalSeconds < seconds) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static void RunTarget(string[] args)
    {
        int count = int.Parse(args[1]);
        string stop = args[3];
        bool always = args[4] == "always";
        using var parent = Process.GetProcessById(int.Parse(args[5]));
        var windows = Enumerable.Range(0, count).Select(i => new WorkloadWindow { Text = "Canvas benchmark " + i, Size = new(320, 220) }).ToArray();
        foreach (var window in windows) window.Show();
        string temporary = args[2] + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(windows.Select(w => w.Handle.ToInt64()).ToArray()));
        File.Move(temporary, args[2]);
        double sink = 0;
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += (_, _) =>
        {
            if (parent.HasExited || File.Exists(stop)) { Application.ExitThread(); return; }
            foreach (var window in windows)
            {
                if (!always && window.WindowState == FormWindowState.Minimized) continue;
                for (int i = 0; i < 20000; i++) sink += Math.Sqrt(i + window.Frame);
                window.Frame++; window.Invalidate();
            }
        };
        timer.Start();
        try { Application.Run(); }
        finally { foreach (var window in windows) window.Dispose(); GC.KeepAlive(sink); }
    }
    // Declared before the window manager/overlays so even setup failures stop the
    // owned child. The child also watches the parent in case the host is killed.
    private sealed class OwnedWorkload : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "canvas-benchmark-" + Guid.NewGuid().ToString("N"));
        public Process Process { get; }
        public string HandlesPath => Path.Combine(_directory, "handles.json");
        private string StopPath => Path.Combine(_directory, "stop");
        public OwnedWorkload(int count, bool always)
        {
            Directory.CreateDirectory(_directory);
            try
            {
                Process = System.Diagnostics.Process.Start(new ProcessStartInfo(Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe"))
                {
                    UseShellExecute = false,
                    ArgumentList = { "--target", count.ToString(), HandlesPath, StopPath, always ? "always" : "cooperative", Environment.ProcessId.ToString() }
                }) ?? throw new InvalidOperationException("Could not start benchmark workload.");
            }
            catch { Directory.Delete(_directory); throw; }
        }
        public void Dispose()
        {
            try
            {
                File.WriteAllText(StopPath, "stop");
                if (!Process.WaitForExit(5000)) { Process.Kill(); Process.WaitForExit(5000); }
            }
            finally
            {
                Process.Dispose();
                // Delete only the four known files; no recursive/computed-target deletion.
                foreach (string file in new[] { HandlesPath, HandlesPath + ".tmp", StopPath }) File.Delete(file);
                Directory.Delete(_directory);
            }
        }
    }

    private sealed class WorkloadWindow : Form
    {
        public int Frame;
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x80; return p; } }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(25, 30, 40));
            for (int i = 0; i < 120; i++) e.Graphics.FillEllipse(Brushes.CornflowerBlue,
                (Frame * 2 + i * 17) % 320, i * 19 % 220, 10, 10);
        }
    }
}

internal sealed class BenchmarkConfig : IAppConfig
{
    public bool DisableGreedyDraw { get; set; } = true;
    public bool DisableSearch => true;
    public bool DisableAltPan => true;
    public bool ShowScreenFixedWindowsDuringPan => false;
    public bool DisableMouseCurve => true;
    public bool DisableZoomHotkey => true;
}
