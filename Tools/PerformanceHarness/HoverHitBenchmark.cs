using System.Diagnostics;
using System.Text.Json;
using CanvasDesktop;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop.PerformanceHarness;

/// <summary>
/// Measures one pointer move's worth of the canvas hover hit-test path: rebuilding the
/// desktop item list the way SelectionItems() does, scanning connections for port dots,
/// and the two Win32 lookups SelectionItems() repeats for every visible window.
/// Read-only; it manipulates no other application's windows and opens none.
/// </summary>
internal static class HoverHitBenchmark
{
    public static void Run(string output)
    {
        const int cardCount = 60, noteCount = 40, samples = 600, lookupRounds = 100;
        var desktop = Guid.NewGuid();

        var document = new AnnotationDocument();
        for (int i = 0; i < noteCount; i++)
            document.Put(new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new CanvasPoint(60 + i % 8 * 400, 2600 + i / 8 * 200),
                new CanvasPoint(60 + i % 8 * 400 + 320, 2600 + i / 8 * 200 + 140),
                Text: "Note " + i));

        var cards = new List<CanvasSelectionItem>();
        for (int i = 0; i < cardCount; i++)
            cards.Add(new(new(CanvasItemKind.Window, Guid.NewGuid()),
                new CanvasRect(40 + i % 10 * 320, 40 + i / 10 * 240, 300, 200), "Saved window " + i));

        // A. Rebuilding the list exactly as SelectionItems() does: document.Items (which
        // allocates its own wrapper), one record per annotation, then the card records.
        var rebuilt = new CanvasSelectionItem[cardCount + noteCount];
        var rebuildMs = new double[samples];
        var rebuildBytes = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            int index = 0;
            foreach (var note in document.Items)
                if (note.DesktopId == desktop)
                    rebuilt[index++] = new CanvasSelectionItem(new(CanvasItemKind.Annotation, note.Id), note.Bounds, note.Text, Annotation: note);
            foreach (var card in cards) rebuilt[index++] = card;
            rebuildMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            rebuildBytes[i] = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }

        var items = rebuilt.ToArray();
        var selection = new CanvasSelection();
        selection.SwitchDesktop(desktop);
        selection.Replace([items[0].Key]);
        var tool = new ConnectionCanvasTool(document, () => desktop, () => items, selection);

        // B. The hover hit test itself, with one card focused. An empty selection returns early.
        var view = new CanvasViewTransform(0, 0, 0.6, new CanvasRect(0, 0, 1920, 1080));
        var hoverMs = new double[samples];
        var hoverBytes = new double[samples];
        int hits = 0;
        for (int i = 0; i < samples; i++)
        {
            var pointer = new CanvasPointer(0, new CanvasPoint(20 + i % 300, 20 + i % 200),
                CanvasPointerButton.None, CanvasModifiers.None, view);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            if (tool.FindHandleAt(pointer) != null) hits++;
            hoverMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            hoverBytes[i] = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }

        // C. The two Win32 lookups the real SelectionItems() repeats for every visible window.
        var api = new Win32WindowApi();
        var handles = new List<IntPtr>();
        foreach (var process in Process.GetProcesses())
        {
            try { if (process.MainWindowHandle != IntPtr.Zero) handles.Add(process.MainWindowHandle); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
        var probed = handles.Distinct().Take(12).ToArray();
        var titleMs = new List<double>();
        var pidMs = new List<double>();
        long sink = 0;
        for (int round = 0; round < lookupRounds; round++)
            foreach (var handle in probed)
            {
                long start = Stopwatch.GetTimestamp();
                string title = api.GetWindowTitle(handle);
                titleMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                start = Stopwatch.GetTimestamp();
                uint pid = api.GetWindowProcessId(handle);
                pidMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                sink += title.Length + pid;
            }
        GC.KeepAlive(sink);

        double rebuild = Percentile(rebuildMs, 0.50), hover = Percentile(hoverMs, 0.50);
        double titles = Percentile(titleMs, 0.50), pids = Percentile(pidMs, 0.50);
        var report = new
        {
            Cards = cardCount, Notes = noteCount, Samples = samples, WindowsProbed = probed.Length,
            SelectionItemsProxy = new
            {
                P50Ms = rebuild, P95Ms = Percentile(rebuildMs, 0.95), MaxMs = rebuildMs.Max(),
                BytesPerCall = rebuildBytes.Average()
            },
            FindHandleAt = new
            {
                P50Ms = hover, P95Ms = Percentile(hoverMs, 0.95), MaxMs = hoverMs.Max(),
                BytesPerCall = hoverBytes.Average(), PortHits = hits
            },
            GetWindowTitle = new { P50Ms = titles, P95Ms = Percentile(titleMs, 0.95), MaxMs = titleMs.Max(), Calls = titleMs.Count },
            GetWindowProcessId = new { P50Ms = pids, P95Ms = Percentile(pidMs, 0.95), MaxMs = pidMs.Max(), Calls = pidMs.Count },
            EstimatedPerMoveMs = rebuild + hover + probed.Length * (titles + pids),
            Note = "Projected cost of one hover with every window enumerated; a real move also queries titles only for visible windows."
        };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(output);
    }

    private static double Percentile(IReadOnlyCollection<double> values, double quantile)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        if (ordered.Length == 0) return 0;
        int index = Math.Clamp((int)Math.Ceiling(quantile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }
}
