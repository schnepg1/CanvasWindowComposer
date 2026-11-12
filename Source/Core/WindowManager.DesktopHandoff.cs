using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class WindowManager
{
    private sealed record DesktopPlacementTarget(AutomaticParkingToken Token, WorldRect World,
        WindowRect Start, WindowRect Destination);

    private sealed record DesktopPlacementPlan(CanvasCamera Camera, long Topology,
        DesktopPlacementTarget[] Targets);

    /// <summary>
    /// Starts the controlled move-only handoff. The caller owns the cover and must
    /// keep explicit native actions fenced until the returned task finishes.
    /// </summary>
    internal Task<bool>? TryStartDesktopHandoffPlacement()
    {
        // The placement reservation and model reconciliation belong to the UI owner.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext
            || !SuspendProjection || !SuspendAutomaticParking || !SupportsMoveOnlyDesktopHandoff()) return null;

        var plan = PlanDesktopHandoff();
        if (plan == null || !plan.Targets.Any(target => target.Start != target.Destination)) return null;
        var cohort = plan.Targets.Select(target => target.Token.Identity.Handle).ToArray();
        var lease = TryAcquireDesktopPlacementLease(cohort);
        if (lease == null) return null;
        try
        {
            if (DesktopPlacementPlanIsCurrent(plan, verifyDestinations: false))
                return PlaceDesktopHandoff(plan, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        lease.Dispose();
        return null;
    }

    internal AutomaticParkingToken? GetDesktopHandoffNavigationIdentity(IntPtr handle)
        => _visibility.GetToken(handle);

    internal bool IsDesktopHandoffNavigationIdentityCurrent(AutomaticParkingToken token)
        => _visibility.IsCurrent(token) && IsParkingIdentityCurrent(token)
            && (_vds == null || _vds.IsOnCurrentDesktop(token.Identity.Handle));

    internal Guid DesktopHandoffNavigationDesktop => _vds?.CurrentDesktopId ?? Guid.Empty;

    private bool SupportsMoveOnlyDesktopHandoff()
        => !_disposed && !DesktopPlacementShutdownRequested && _vds == null
            && _parking.Count == 0 && _clippedWindows.Count == 0 && _previewUnclipped.Count == 0;

    private DesktopPlacementPlan? PlanDesktopHandoff()
    {
        var camera = _canvas.Camera;
        long topology = _win32.ScreenTopologyVersion;
        if (topology < 0) return null;
        var screens = _win32.GetScreenWorkingAreas();
        var snapshot = _canvas.Windows.ToArray();
        var targets = new DesktopPlacementTarget[snapshot.Length];
        uint ownPid = (uint)Environment.ProcessId;
        for (int i = 0; i < snapshot.Length; i++)
        {
            var (handle, world) = snapshot[i];
            if (world.State != WindowState.Normal || world.PinnedToScreen || world.InInventory
                || _pendingMoves.ContainsKey(handle) || _unconfirmedMoves.Contains(handle)
                || _visibility.GetToken(handle) is not { } token || token.Identity.ProcessId == ownPid
                || !DesktopPlacementIdentityIsCurrent(token)) return null;
            var plan = WindowPlacementPlanner.Plan(world, camera, screens, ClipOffScreen, wasClipped: false);
            if (!plan.ShouldMove || plan.RegionChange != WindowRegionChange.None || !plan.IntersectsViewport) return null;
            var (x, y, width, height) = _win32.GetWindowRect(handle);
            if (width <= 0 || height <= 0) return null;
            targets[i] = new(token, world, new(x, y, width, height),
                new(plan.TargetRect.X, plan.TargetRect.Y, width, height));
        }

        return new(camera, topology, targets);
    }

    private bool DesktopPlacementIdentityIsCurrent(AutomaticParkingToken token)
    {
        var handle = token.Identity.Handle;
        return _visibility.IsCurrent(token) && IsParkingIdentityCurrent(token)
            && _win32.IsManageable(handle, (uint)Environment.ProcessId);
    }

    private bool DesktopPlacementPlanIsCurrent(DesktopPlacementPlan plan, bool verifyDestinations)
    {
        if (!SupportsMoveOnlyDesktopHandoff() || _canvas.Camera != plan.Camera
            || _win32.ScreenTopologyVersion != plan.Topology || _canvas.Windows.Count != plan.Targets.Length) return false;

        foreach (var target in plan.Targets)
        {
            var handle = target.Token.Identity.Handle;
            if (!_canvas.Windows.TryGetValue(handle, out var current) || !current.Equals(target.World)
                || !DesktopPlacementIdentityIsCurrent(target.Token)) return false;

            var (x, y, width, height) = _win32.GetWindowRect(handle);
            var expected = verifyDestinations ? target.Destination : target.Start;
            if (Math.Abs((long)x - expected.X) > ReconcileTolerancePx || Math.Abs((long)y - expected.Y) > ReconcileTolerancePx
                || width != expected.W || height != expected.H) return false;
        }

        return SupportsMoveOnlyDesktopHandoff() && _canvas.Camera == plan.Camera
            && _win32.ScreenTopologyVersion == plan.Topology && _canvas.Windows.Count == plan.Targets.Length
            && plan.Targets.All(target => _visibility.IsCurrent(target.Token)
                && _canvas.Windows.TryGetValue(target.Token.Identity.Handle, out var world) && world.Equals(target.World));
    }

    private async Task<bool> PlaceDesktopHandoff(DesktopPlacementPlan plan, ProjectionWorker.PlacementLease lease)
    {
        using (lease)
        {
            var moves = new List<(BatchMoveItem Item, uint ProcessId)>();
            foreach (var target in plan.Targets)
            {
                if (target.Start == target.Destination) continue;
                var handle = target.Token.Identity.Handle;
                _lastScreen[handle] = (target.Start.X, target.Start.Y, target.Start.W, target.Start.H);
                moves.Add((TrackMove(handle, target.Destination, posOnly: true), target.Token.Identity.ProcessId));
            }

            bool verified = false;
            try
            {
                var result = await lease.Submit(moves);
                verified = result.Summary.Status == NativeOperationStatus.Succeeded
                    && result.Items.Count == moves.Count
                    && result.Items.Select((item, index) => (item, index)).All(entry =>
                        entry.item.Status == NativeOperationStatus.Succeeded
                        && entry.item.Window == moves[entry.index].Item.HWnd.ToInt64())
                    && DesktopPlacementPlanIsCurrent(plan, verifyDestinations: true);
                return verified;
            }
            finally
            {
                foreach (var (item, _) in moves)
                {
                    var status = verified ? NativeOperationStatus.Succeeded : NativeOperationStatus.Failed;
                    ApplyMoveResult(item, new("DesktopHandoffPlacement", item.HWnd.ToInt64(), status, null, 0));
                }
            }
        }
    }
}
