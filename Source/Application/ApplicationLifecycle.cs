using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal enum ShutdownPhase
{
    Quiesce,
    FinalizeLayout,
    SaveWorkspace,
    StopServices,
    RecoverWindows,
    ReleaseUi
}

internal enum ApplicationLifetimeState { Starting, Running, Stopping, Stopped }
internal sealed record ShutdownFailure(string Step, Exception Error);

/// <summary>
/// UI-thread-owned cleanup plan. Register ownership immediately after acquisition,
/// including during startup. Phase order preserves save-before-recovery; a failed
/// step or error reporter never prevents the remaining steps from being attempted.
/// </summary>
internal sealed class ApplicationLifecycle : IDisposable
{
    private readonly List<(ShutdownPhase Phase, string Name, Action Cleanup)> _steps = new();
    private readonly List<ShutdownFailure> _failures = new();
    private readonly Action<ShutdownFailure>? _report;
    public ApplicationLifetimeState State { get; private set; } = ApplicationLifetimeState.Starting;
    public IReadOnlyList<ShutdownFailure> Failures { get; }
    public bool IsStopping => State is ApplicationLifetimeState.Stopping or ApplicationLifetimeState.Stopped;
    public bool StartupCompleted { get; private set; }

    public ApplicationLifecycle(Action<ShutdownFailure>? report = null)
    {
        _report = report;
        Failures = _failures.AsReadOnly();
    }

    public void Register(ShutdownPhase phase, string name, Action cleanup)
    {
        if (State != ApplicationLifetimeState.Starting)
            throw new InvalidOperationException("Register cleanup while acquiring startup resources.");
        _steps.Add((phase, name, cleanup));
    }

    public T Own<T>(T resource, ShutdownPhase phase, string name) where T : IDisposable
    {
        Register(phase, name, resource.Dispose);
        return resource;
    }

    public void CompleteStartup()
    {
        if (State != ApplicationLifetimeState.Starting) throw new InvalidOperationException("Startup is already complete or aborted.");
        State = ApplicationLifetimeState.Running;
        StartupCompleted = true;
    }

    public void Shutdown()
    {
        if (IsStopping) return;
        State = ApplicationLifetimeState.Stopping;
        try
        {
            // OrderBy is stable: registration order is retained within a phase.
            foreach (var step in _steps.OrderBy(s => s.Phase))
            {
                try { step.Cleanup(); }
                catch (Exception ex)
                {
                    var failure = new ShutdownFailure(step.Name, ex);
                    _failures.Add(failure);
                    try { _report?.Invoke(failure); } catch { /* Reporting is not cleanup. */ }
                }
            }
        }
        finally
        {
            _steps.Clear(); // Release callbacks and the resources they reference.
            State = ApplicationLifetimeState.Stopped;
        }
    }

    public void Dispose() => Shutdown();
}
