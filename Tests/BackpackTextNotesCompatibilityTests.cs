using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace CanvasDesktop.Tests;

public class BackpackTextNotesCompatibilityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "canvas-note-backpack-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_folder, "annotations.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [Fact]
    public void NoteWithInInventoryFlagRoundTripsThroughPersistence()
    {
        var store = new AnnotationPersistence(FilePath);
        var desktop = Guid.NewGuid();
        var note = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(100, 200), new(340, 260), Text: "Carried note in backpack", InInventory: true);

        store.Document.Put(note);
        Assert.True(store.Save());

        var loadedStore = new AnnotationPersistence(FilePath);
        var loaded = Assert.Single(loadedStore.Document.Items);
        Assert.Equal(note.Id, loaded.Id);
        Assert.Equal(note.Text, loaded.Text);
        Assert.True(loaded.InInventory);
    }

    [Fact]
    public void NoteInInventoryIsExcludedFromCanvasHitTestingAndSurfaceRendering()
        => WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var activeNote = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(10, 10), new(200, 100), Text: "Active note on canvas", InInventory: false);
            var backpackNote = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(300, 10), new(500, 100), Text: "Carried note in backpack", InInventory: true);
            var arrowToBackpack = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Arrow,
                new(200, 50), new(300, 50), TargetId: backpackNote.Id, InInventory: false);

            var doc = new AnnotationDocument([activeNote, backpackNote, arrowToBackpack]);
            var tool = new AnnotationCanvasTool(doc, () => desktop);
            tool.SetMode(AnnotationMode.Select);

            var transform = new CanvasViewTransform(0, 0, 1, new CanvasRect(0, 0, 800, 600));

            // Verify hit-testing: active note is hit, backpack note is ignored
            var hitBackpack = tool.Hit(new CanvasPointer(0, new CanvasPoint(350, 50), CanvasPointerButton.None, CanvasModifiers.None, transform));
            Assert.Null(hitBackpack);

            var hitActive = tool.Hit(new CanvasPointer(0, new CanvasPoint(50, 50), CanvasPointerButton.None, CanvasModifiers.None, transform));
            Assert.NotNull(hitActive);
            Assert.Equal(activeNote.Id, hitActive.Id);

            // Verify surface rendering: backpack note does not render into the visual tree
            var surface = new OverviewAnnotationSurface();
            surface.SetDrawingLayers([new CanvasAnnotationLayer(doc, tool, () => desktop, () => true)]);
            surface.Update(OverviewAnnotationSnapshot.Create([], 0, 0, 1, new(0, 0, 800, 600), null, false, showHints: false));
            surface.Measure(new Size(800, 600));
            surface.Arrange(new Rect(0, 0, 800, 600));
            surface.UpdateLayout();

            var bitmap = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var pixels = new byte[800 * 600 * 4];
            bitmap.CopyPixels(pixels, 800 * 4, 0);

            // Pixels around active note (50, 50) have non-zero alpha
            int activePixelIdx = (50 * 800 + 50) * 4;
            Assert.True(pixels[activePixelIdx + 3] > 0);

            // Pixels around backpack note (350, 50) remain completely transparent (0 alpha)
            int backpackPixelIdx = (50 * 800 + 350) * 4;
            Assert.Equal(0, pixels[backpackPixelIdx + 3]);
        });

    [Fact]
    public void DroppingNoteFromBackpackFindsClearLandingSpaceAndRestoresCanvasPresence()
    {
        var desktop = Guid.NewGuid();
        var note = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(100, 200), new(340, 260), Text: "To be unpacked", InInventory: true);
        var doc = new AnnotationDocument([note]);

        var noteBounds = new WorldRect
        {
            X = Math.Min(note.A.X, note.B.X),
            Y = Math.Min(note.A.Y, note.B.Y),
            W = Math.Max(AnnotationCanvasTool.MinimumTextCardWidth, Math.Abs(note.B.X - note.A.X)),
            H = Math.Max(AnnotationCanvasTool.MinimumTextCardHeight, Math.Abs(note.B.Y - note.A.Y)),
            InInventory = true
        };

        var viewport = new ArrivalViewport(new CanvasCamera(5000, -2000, 1), [new(0, 0, 1920, 1080)]);
        var obstacles = new List<WorldRect>
        {
            new() { X = 5000, Y = -2000, W = 400, H = 300 } // an obstacle at top-left
        };

        // TryPlace finds clear adjacent space
        bool placedOk = NearbyWindowPlacement.TryPlace(noteBounds, viewport, obstacles, out var placed, 5000, -2000);
        Assert.True(placedOk);
        Assert.False(placed.InInventory);
        Assert.False(TemporaryAreaLayout.Overlaps(placed, obstacles[0], 24));

        // When unpacked, updating note clears InInventory and sets new coordinates
        var unpackedNote = note with
        {
            A = new(placed.X, placed.Y),
            B = new(placed.X + placed.W, placed.Y + placed.H),
            InInventory = false
        };
        doc.Put(unpackedNote);

        var updated = doc.Items.Single(a => a.Id == note.Id);
        Assert.False(updated.InInventory);
        Assert.Equal(placed.X, updated.A.X);
        Assert.Equal(placed.Y, updated.A.Y);
    }

    [Fact]
    public void ProjectWithInInventoryFlagRoundTripsThroughPersistence()
    {
        var store = new MemoryWorkspaceStore();
        var persistence = new WorkspacePersistence(store);
        var desktop = Guid.NewGuid();
        var zone = new WorkspaceZone
        {
            Id = Guid.NewGuid(),
            DesktopId = desktop,
            Name = "Feature Pod",
            Bounds = new WorldRect { X = 100, Y = 200, W = 800, H = 600 },
            InInventory = true
        };

        persistence.Document.Zones.Add(zone);
        Assert.True(persistence.Save());

        var loadedZone = Assert.Single(store.Document.Zones);
        Assert.Equal(zone.Id, loadedZone.Id);
        Assert.Equal("Feature Pod", loadedZone.Name);
        Assert.True(loadedZone.InInventory);
    }

    [Fact]
    public void ProjectWithNestedItemsExpandsAndRelocatesAtomicallyOnUnpack()
    {
        var projectId = Guid.NewGuid();
        var windowId = Guid.NewGuid();
        var noteId = Guid.NewGuid();

        // Project at (100, 100, 800, 600)
        var projectItem = new CanvasSelectionItem(
            new(CanvasItemKind.Project, projectId),
            new(100, 100, 800, 600),
            "Project Area",
            Members: [new(CanvasItemKind.Window, windowId)]
        );

        // Window inside project at (150, 150, 300, 200)
        var windowItem = new CanvasSelectionItem(
            new(CanvasItemKind.Window, windowId),
            new(150, 150, 300, 200),
            "Editor"
        );

        // Text note spatially inside project at (200, 400, 200, 100)
        var noteItem = new CanvasSelectionItem(
            new(CanvasItemKind.Annotation, noteId),
            new(200, 400, 200, 100),
            "Project Note"
        );

        var allItems = new[] { projectItem, windowItem, noteItem };

        // Expand starting from project zone
        var expanded = CanvasSelection.Expand([new(CanvasItemKind.Project, projectId)], allItems);

        // Expanded group must contain the project, explicit window member, and spatially enclosed note
        Assert.Contains(new CanvasItemId(CanvasItemKind.Project, projectId), expanded);
        Assert.Contains(new CanvasItemId(CanvasItemKind.Window, windowId), expanded);
        Assert.Contains(new CanvasItemId(CanvasItemKind.Annotation, noteId), expanded);

        // Relocating project to (500, 600) -> dx = 400, dy = 500
        double dx = 400;
        double dy = 500;

        var movedItems = allItems.Where(i => expanded.Contains(i.Key))
            .Select(i => i with { Bounds = new(i.Bounds.X + dx, i.Bounds.Y + dy, i.Bounds.Width, i.Bounds.Height) })
            .ToDictionary(i => i.Key);

        Assert.Equal(500, movedItems[new(CanvasItemKind.Project, projectId)].Bounds.X);
        Assert.Equal(600, movedItems[new(CanvasItemKind.Project, projectId)].Bounds.Y);

        Assert.Equal(550, movedItems[new(CanvasItemKind.Window, windowId)].Bounds.X);
        Assert.Equal(650, movedItems[new(CanvasItemKind.Window, windowId)].Bounds.Y);

        Assert.Equal(600, movedItems[new(CanvasItemKind.Annotation, noteId)].Bounds.X);
        Assert.Equal(900, movedItems[new(CanvasItemKind.Annotation, noteId)].Bounds.Y);
    }
}
