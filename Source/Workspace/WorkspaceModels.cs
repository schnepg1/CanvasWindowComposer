using System;
using System.Collections.Generic;

namespace CanvasDesktop;

// Persisted data only: no HWNDs, UI objects, or matching/storage behaviour.
internal sealed class WorkspaceItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DesktopId { get; set; }
    public string Title { get; set; } = "";
    public string Name { get; set; } = ""; // Optional user label; never overwritten by a changing window title.
    public string ExecutablePath { get; set; } = "";
    public string Target { get; set; } = "";
    public string ChromeProfilePath { get; set; } = "";
    public bool CaptureChromeProfile { get; set; } = true;
    public bool IsFolder { get; set; }
    public bool OpenOnStartup { get; set; }
    public WorldRect Bounds { get; set; }
}

internal sealed class WorkspaceCamera
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Zoom { get; set; } = 1;
}

internal sealed class WorkspaceDocument
{
    public int Version { get; set; } = 1;
    public List<WorkspaceItem> Items { get; set; } = new();
    public Dictionary<Guid, WorkspaceCamera> Cameras { get; set; } = new();
    public List<WorkspaceZone> Zones { get; set; } = new();
}

internal static class WorkspaceGeometry
{
    public static bool ValidBounds(WorldRect b) =>
        double.IsFinite(b.X) && double.IsFinite(b.Y) && Math.Abs(b.X) < 10000000 && Math.Abs(b.Y) < 10000000
        && double.IsFinite(b.W) && double.IsFinite(b.H) && b.W >= 1 && b.H >= 1 && b.W <= 32000 && b.H <= 32000;
}

internal sealed class WorkspaceZone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DesktopId { get; set; }
    public string Name { get; set; } = "Project area";
    public string Color { get; set; } = "#60A5FA";
    public WorldRect Bounds { get; set; }
    public List<Guid> ItemIds { get; set; } = new();
}
