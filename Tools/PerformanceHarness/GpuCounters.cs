using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CanvasDesktop.PerformanceHarness;

internal sealed record GpuEngineSample(int ProcessId, string Instance, double UtilizationPercent);

/// <summary>English PDH counter names work independently of the Windows display language.</summary>
internal sealed class GpuCounters : IDisposable
{
    private IntPtr _query, _counter;
    public string? UnavailableReason { get; private set; }
    public GpuCounters()
    {
        uint status = PdhOpenQuery(null, IntPtr.Zero, out _query);
        if (status == 0) status = PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter);
        if (status == 0) status = PdhCollectQueryData(_query);
        if (status != 0) UnavailableReason = $"GPU counters unavailable: PDH 0x{status:X8}";
    }

    public GpuEngineSample[] Read(IReadOnlySet<int> processes)
    {
        if (UnavailableReason != null) return [];
        uint status = PdhCollectQueryData(_query);
        uint bytes = 0, count = 0;
        if (status == 0) status = PdhGetFormattedCounterArray(_counter, 0x200, ref bytes, out count, IntPtr.Zero);
        if (status != 0x800007D2 || bytes == 0) return Fail(status);
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            status = PdhGetFormattedCounterArray(_counter, 0x200, ref bytes, out count, buffer);
            if (status != 0) return Fail(status);
            var samples = new List<GpuEngineSample>();
            int stride = Marshal.SizeOf<CounterItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, i * stride));
                string name = Marshal.PtrToStringUni(item.Name) ?? "";
                var match = Regex.Match(name, @"(?:^|_)pid_(\d+)_");
                if (item.Value.Status <= 1 && match.Success && int.TryParse(match.Groups[1].Value, out int pid) && processes.Contains(pid))
                    samples.Add(new(pid, name, item.Value.Double));
            }
            return samples.ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private GpuEngineSample[] Fail(uint status)
    {
        UnavailableReason = $"GPU sample unavailable: PDH 0x{status:X8}";
        return [];
    }
    public void Dispose() { if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct CounterValue { public uint Status; public double Double; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr values);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
