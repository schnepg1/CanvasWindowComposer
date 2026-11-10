using System.Diagnostics.Tracing;

namespace CanvasDesktop;

/// <summary>Opt-in ETW and EventPipe timings with no window identities or workspace data.</summary>
[EventSource(Name = "CanvasDesktop-Performance", Guid = "854023e4-c25a-4841-8d6e-7307c236be53")]
internal sealed class CanvasPerformanceEvents : EventSource
{
    public static readonly CanvasPerformanceEvents Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Operation(string operation, long startQpc, double durationMs, int status, int itemCount)
    {
        if (IsEnabled()) WriteEvent(1, operation, startQpc, durationMs, status, itemCount);
    }
}
