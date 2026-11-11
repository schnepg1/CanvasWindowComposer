using System;
using System.Collections.Generic;
using System.Linq;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

public class ConnectionCanvasToolTests
{
    private static readonly Guid Desktop = Guid.NewGuid();

    private static CanvasPointer Pointer(double x, double y, CanvasPointerButton button = CanvasPointerButton.None)
        => new(0, new(x, y), button, CanvasModifiers.None, new CanvasViewTransform(0, 0, 1.0, new(0, 0, 1920, 1080)));

    [Fact]
    public void FindSnapTarget_OverlappingItems_PrefersTheSmallestUnderThePointer()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var source = new CanvasSelectionItem(new(CanvasItemKind.Window, Guid.NewGuid()), new CanvasRect(100, 100, 400, 300), "Source");
        var card = new CanvasSelectionItem(new(CanvasItemKind.Window, Guid.NewGuid()), new CanvasRect(700, 100, 600, 400), "Card");
        var note = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text,
            new CanvasPoint(900, 250), new CanvasPoint(1100, 450), Text: "Note");
        var noteItem = new CanvasSelectionItem(new(CanvasItemKind.Annotation, note.Id), note.Bounds, note.Text, Annotation: note);

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [source, card, noteItem], selection);
        selection.Replace([source.Key]);

        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        tool.PointerMove(Pointer(1000, 350));

        // Both the card and the note contain the pointer; the smaller note must win.
        Assert.NotNull(tool.SnapTarget);
        Assert.Equal(noteItem.Key, tool.SnapTarget!.Item.Key);
        tool.Cancel();
    }

    [Fact]
    public void FindSnapTarget_VisiblePortWinsOverALargerBodyUnderThePointer()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var source = new CanvasSelectionItem(new(CanvasItemKind.Window, Guid.NewGuid()), new CanvasRect(100, 100, 400, 300), "Source");
        var note = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text,
            new CanvasPoint(900, 250), new CanvasPoint(1100, 450), Text: "Note");
        var noteItem = new CanvasSelectionItem(new(CanvasItemKind.Annotation, note.Id), note.Bounds, note.Text, Annotation: note);
        var card = new CanvasSelectionItem(new(CanvasItemKind.Window, Guid.NewGuid()), new CanvasRect(700, 100, 600, 400), "Card");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [source, noteItem, card], selection);
        selection.Replace([source.Key]);

        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        tool.PointerMove(Pointer(900, 350));

        // The note Left port is under the pointer even though the larger card body is too.
        Assert.NotNull(tool.SnapTarget);
        Assert.Equal(noteItem.Key, tool.SnapTarget!.Item.Key);
        Assert.Equal(ConnectionPort.Left, tool.SnapTarget.Port);
    }

    [Fact]
    public void PointerUp_RecordsEphemeralEndpointsForLiveWindows()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var source = new CanvasSelectionItem(new(CanvasItemKind.Window, Guid.NewGuid()), new CanvasRect(100, 100, 400, 300), "Live", Ephemeral: true);
        var target = new CanvasSelectionItem(new(CanvasItemKind.Window, Guid.NewGuid()), new CanvasRect(700, 100, 400, 300), "Live two", Ephemeral: true);
        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [source, target], selection);
        selection.Replace([source.Key]);

        CanvasAnnotation? created = null;
        tool.ConnectionCreated += arrow => created = arrow;
        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        tool.PointerMove(Pointer(695, 250));
        tool.PointerUp(Pointer(695, 250, CanvasPointerButton.Left));

        Assert.NotNull(created);
        Assert.True(created!.SourceEphemeral);
        Assert.True(created.TargetEphemeral);
    }

    [Fact]
    public void Resolve_EndpointDrop_AttachesToSmallestItemAndDetachesInEmptySpace()
    {
        var cardId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var note = new CanvasAnnotation(noteId, Desktop, AnnotationKind.Text,
            new CanvasPoint(900, 250), new CanvasPoint(1100, 450), Text: "Note");
        var card = new CanvasSelectionItem(new(CanvasItemKind.Window, cardId), new CanvasRect(700, 100, 600, 400), "Card");
        var noteItem = new CanvasSelectionItem(new(CanvasItemKind.Annotation, noteId), note.Bounds, note.Text, Annotation: note);
        var arrow = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Arrow,
            new CanvasPoint(100, 100), new CanvasPoint(910, 350), SourceId: cardId);

        var attached = ConnectionRetarget.Resolve(arrow, false, new CanvasPoint(910, 350), [card, noteItem]);
        Assert.Equal(noteId, attached.TargetId);
        Assert.Equal(ConnectionPort.Left, attached.TargetPort);
        Assert.Equal(900, attached.B.X);
        Assert.Equal(350, attached.B.Y);

        var detached = ConnectionRetarget.Resolve(attached, false, new CanvasPoint(2000, 2000), [card, noteItem]);
        Assert.Null(detached.TargetId);
        Assert.Equal(ConnectionPort.Auto, detached.TargetPort);

        // The item already holding the other end is never chosen, so one end cannot fold onto it.
        var freeArrow = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Arrow,
            new CanvasPoint(100, 100), new CanvasPoint(400, 300), TargetId: noteId);
        Assert.Equal(cardId, ConnectionRetarget.Resolve(freeArrow, true, new CanvasPoint(910, 350), [card, noteItem]).SourceId);
    }

    [Fact]
    public void EndpointDrag_CommitsThroughTheResolver()
    {
        var doc = new AnnotationDocument();
        var arrowId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        doc.Put(new CanvasAnnotation(arrowId, Desktop, AnnotationKind.Arrow,
            new CanvasPoint(100, 100), new CanvasPoint(400, 300)));

        var tool = new AnnotationCanvasTool(doc, () => Desktop, null,
            (item, sourceEndpoint) => item with { TargetId = targetId, TargetEphemeral = true });
        tool.SetMode(AnnotationMode.Select);
        tool.Select(arrowId);

        tool.PointerDown(Pointer(400, 300, CanvasPointerButton.Left));
        tool.PointerMove(Pointer(520, 380));
        tool.PointerUp(Pointer(520, 380, CanvasPointerButton.Left));

        var saved = Assert.Single(doc.Items);
        Assert.Equal(targetId, saved.TargetId);
        Assert.True(saved.TargetEphemeral);
        Assert.Equal(520, saved.B.X);
        Assert.Equal(380, saved.B.Y);
    }

    [Fact]
    public void EphemeralAnchorsSurviveJsonRoundTrip()
    {
        var arrow = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Arrow,
            new CanvasPoint(0, 0), new CanvasPoint(120, 90),
            SourceId: Guid.NewGuid(), SourceEphemeral: true, TargetId: Guid.NewGuid(), TargetEphemeral: true);
        var json = System.Text.Json.JsonSerializer.Serialize(arrow);
        Assert.Equal(arrow, System.Text.Json.JsonSerializer.Deserialize<CanvasAnnotation>(json));
    }

    [Fact]
    public void FindHandleAt_UnselectedCard_DoesNotOfferHandles()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var window = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Unfocused Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [window], selection);

        Assert.Null(tool.FindHandleAt(Pointer(302, 101)));
        Assert.Null(tool.FindHandleAt(Pointer(500, 250)));
        Assert.False(tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left)));
        Assert.False(tool.HasPendingInteraction);

        // Focused once: the same border now starts a connection.
        selection.Replace([window.Key]);
        Assert.NotNull(tool.FindHandleAt(Pointer(500, 250)));
        Assert.True(tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left)));
    }

    [Fact]
    public void SelectedNoteAnnotation_ConnectsFromNoteBorderToCard()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var note = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text,
            new CanvasPoint(100, 100), new CanvasPoint(300, 200), Text: "Note");
        var noteItem = new CanvasSelectionItem(new(CanvasItemKind.Annotation, note.Id), note.Bounds, note.Text, Annotation: note);
        var window = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(700, 100, 400, 300),
            "Target Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [noteItem, window], selection);
        CanvasAnnotation? createdArrow = null;
        tool.ConnectionCreated += arrow => createdArrow = arrow;

        // Unfocused note borders are inert.
        Assert.Null(tool.FindHandleAt(Pointer(300, 150)));

        selection.Replace([noteItem.Key]);
        var right = tool.FindHandleAt(Pointer(300, 150));
        Assert.NotNull(right);
        Assert.Equal(noteItem.Key, right.Item.Key);
        Assert.Equal(ConnectionPort.Right, right.Port);

        tool.PointerDown(Pointer(300, 150, CanvasPointerButton.Left));
        tool.PointerMove(Pointer(695, 250));
        tool.PointerUp(Pointer(695, 250, CanvasPointerButton.Left));

        Assert.NotNull(createdArrow);
        Assert.Equal(note.Id, createdArrow.SourceId);
        Assert.Equal(ConnectionPort.Right, createdArrow.SourcePort);
        Assert.Equal(window.Key.Id, createdArrow.TargetId);
        Assert.Equal(ConnectionPort.Left, createdArrow.TargetPort);
    }

    [Fact]
    public void PointerMove_SnapsToSelectedCardAndNoteTargets()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var source = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");
        var note = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text,
            new CanvasPoint(700, 100), new CanvasPoint(900, 200), Text: "Target note");
        var noteItem = new CanvasSelectionItem(new(CanvasItemKind.Annotation, note.Id), note.Bounds, note.Text, Annotation: note);

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [source, noteItem], selection);
        selection.Replace([source.Key]);

        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        tool.PointerMove(Pointer(800, 150));

        Assert.NotNull(tool.SnapTarget);
        Assert.Equal(noteItem.Key, tool.SnapTarget.Item.Key);
        Assert.Equal(ConnectionPort.Left, tool.SnapTarget.Port);
        Assert.Equal(700, tool.SnapTarget.Point.X);
    }

    [Fact]
    public void FindHandleAt_ReturnsCardinalPorts_WhenPointerNearHandle()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var windowId = Guid.NewGuid();
        var window = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, windowId),
            new CanvasRect(100, 100, 400, 300),
            "Test Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [window], selection);
        selection.Replace([window.Key]);

        // Top port: (300, 100)
        var top = tool.FindHandleAt(Pointer(302, 101));
        Assert.NotNull(top);
        Assert.Equal(ConnectionPort.Top, top.Port);
        Assert.Equal(window.Key, top.Item.Key);

        // Right port: (500, 250)
        var right = tool.FindHandleAt(Pointer(499, 252));
        Assert.NotNull(right);
        Assert.Equal(ConnectionPort.Right, right.Port);

        // Bottom port: (300, 400)
        var bottom = tool.FindHandleAt(Pointer(300, 398));
        Assert.NotNull(bottom);
        Assert.Equal(ConnectionPort.Bottom, bottom.Port);

        // Left port: (100, 250)
        var left = tool.FindHandleAt(Pointer(101, 250));
        Assert.NotNull(left);
        Assert.Equal(ConnectionPort.Left, left.Port);

        // Far away point
        var far = tool.FindHandleAt(Pointer(800, 800));
        Assert.Null(far);
    }

    [Fact]
    public void PointerDown_OnHandle_StartsConnectionGesture()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var window = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [window], selection);
        selection.Replace([window.Key]);

        // Pointer down on Right port (500, 250)
        bool handled = tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        Assert.True(handled);
        Assert.True(tool.HasPendingInteraction);
        Assert.NotNull(tool.Preview);
        Assert.Equal(500, tool.Preview.A.X);
        Assert.Equal(250, tool.Preview.A.Y);
        Assert.Equal(ConnectionPort.Right, tool.Preview.SourcePort);
        Assert.Equal(window.Key.Id, tool.Preview.SourceId);
    }

    [Fact]
    public void PointerMove_NearTargetPort_SnapsMagnetically()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var w1 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");
        var w2 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(700, 100, 400, 300),
            "Target Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [w1, w2], selection);
        selection.Replace([w1.Key]);

        // Start from Right port of w1 (500, 250)
        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));

        // Drag to free space
        tool.PointerMove(Pointer(600, 250));
        Assert.Null(tool.SnapTarget);
        Assert.Equal(600, tool.Preview!.B.X);

        // Drag near Left port of w2: (700, 250) - within 20 DIPs
        tool.PointerMove(Pointer(692, 252));
        Assert.NotNull(tool.SnapTarget);
        Assert.Equal(w2.Key, tool.SnapTarget.Item.Key);
        Assert.Equal(ConnectionPort.Left, tool.SnapTarget.Port);
        Assert.Equal(700, tool.Preview.B.X);
        Assert.Equal(250, tool.Preview.B.Y);
    }

    [Fact]
    public void PointerMove_InsideTargetBounds_SnapsToAutoPortFacingSource()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var w1 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");
        var w2 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(700, 100, 400, 300),
            "Target Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [w1, w2], selection);
        selection.Replace([w1.Key]);

        // Start from w1 Right port
        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));

        // Move inside w2 center: (900, 250)
        tool.PointerMove(Pointer(900, 250));
        Assert.NotNull(tool.SnapTarget);
        Assert.Equal(w2.Key, tool.SnapTarget.Item.Key);
        Assert.Equal(ConnectionPort.Left, tool.SnapTarget.Port);
        Assert.Equal(700, tool.Preview!.B.X);
        Assert.Equal(250, tool.Preview.B.Y);
    }

    [Fact]
    public void PointerUp_OverTarget_CreatesCurvedConnectedArrowAndFiresEvent()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var w1 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");
        var w2 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(700, 100, 400, 300),
            "Target Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [w1, w2], selection);
        selection.Replace([w1.Key]);

        CanvasAnnotation? createdArrow = null;
        tool.ConnectionCreated += arrow => createdArrow = arrow;

        // Start drag from w1
        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        // Move over w2 Left port
        tool.PointerMove(Pointer(695, 250));
        // Release
        tool.PointerUp(Pointer(695, 250, CanvasPointerButton.Left));

        Assert.False(tool.HasPendingInteraction);
        Assert.NotNull(createdArrow);
        Assert.Equal(AnnotationKind.Arrow, createdArrow.Kind);
        Assert.Equal(ConnectorRouting.Curved, createdArrow.Routing);
        Assert.Equal(w1.Key.Id, createdArrow.SourceId);
        Assert.Equal(ConnectionPort.Right, createdArrow.SourcePort);
        Assert.Equal(w2.Key.Id, createdArrow.TargetId);
        Assert.Equal(ConnectionPort.Left, createdArrow.TargetPort);
        Assert.Equal(500, createdArrow.A.X);
        Assert.Equal(250, createdArrow.A.Y);
        Assert.Equal(700, createdArrow.B.X);
        Assert.Equal(250, createdArrow.B.Y);

        // Verify arrow exists in document
        var docArrow = Assert.Single(doc.Items);
        Assert.Equal(createdArrow.Id, docArrow.Id);
    }

    [Fact]
    public void PointerUp_MicroDrag_RejectsAccidentalClick()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var w1 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [w1], selection);
        selection.Replace([w1.Key]);

        CanvasAnnotation? createdArrow = null;
        tool.ConnectionCreated += arrow => createdArrow = arrow;

        // Start drag at (500, 250)
        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        // Only moved 2px
        tool.PointerMove(Pointer(502, 251));
        tool.PointerUp(Pointer(502, 251, CanvasPointerButton.Left));

        Assert.False(tool.HasPendingInteraction);
        Assert.Null(createdArrow);
        Assert.Empty(doc.Items);
    }

    [Fact]
    public void Cancel_ClearsPendingGestureAndPreview()
    {
        var doc = new AnnotationDocument();
        var selection = new CanvasSelection();
        var w1 = new CanvasSelectionItem(
            new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid()),
            new CanvasRect(100, 100, 400, 300),
            "Source Window");

        var tool = new ConnectionCanvasTool(doc, () => Desktop, () => [w1], selection);
        selection.Replace([w1.Key]);

        tool.PointerDown(Pointer(500, 250, CanvasPointerButton.Left));
        tool.PointerMove(Pointer(600, 250));
        Assert.True(tool.HasPendingInteraction);
        Assert.NotNull(tool.Preview);

        tool.Cancel();
        Assert.False(tool.HasPendingInteraction);
        Assert.Null(tool.Preview);
        Assert.Null(tool.SnapTarget);
        Assert.Empty(doc.Items);
    }
    [Fact]
    public void DeleteWithConnectors_RemovesAnchoredArrowsAndKeepsUnrelatedOnes()
    {
        var note = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text,
            new CanvasPoint(100, 100), new CanvasPoint(300, 200), Text: "Note");
        var cardId = Guid.NewGuid();
        var connected = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Arrow,
            new CanvasPoint(300, 150), new CanvasPoint(700, 150), SourceId: note.Id, TargetId: cardId);
        var unrelated = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Arrow,
            new CanvasPoint(0, 0), new CanvasPoint(50, 50));
        var doc = new AnnotationDocument([note, connected, unrelated]);

        Assert.True(doc.DeleteWithConnectors(Desktop, [note.Id]));
        Assert.DoesNotContain(doc.Items, a => a.Id == note.Id);
        Assert.DoesNotContain(doc.Items, a => a.Id == connected.Id);
        Assert.Contains(doc.Items, a => a.Id == unrelated.Id);

        // One step: undo restores the note and its connector together.
        Assert.True(doc.Undo(Desktop));
        Assert.Contains(doc.Items, a => a.Id == note.Id);
        Assert.Contains(doc.Items, a => a.Id == connected.Id);
    }

    [Fact]
    public void DeleteWithConnectors_LeavesOtherDesktopsAlone()
    {
        var other = Guid.NewGuid();
        var note = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text,
            new CanvasPoint(100, 100), new CanvasPoint(300, 200), Text: "Note");
        var foreign = new CanvasAnnotation(Guid.NewGuid(), other, AnnotationKind.Arrow,
            new CanvasPoint(300, 150), new CanvasPoint(700, 150), SourceId: note.Id);
        var doc = new AnnotationDocument([note, foreign]);

        Assert.True(doc.DeleteWithConnectors(Desktop, [note.Id]));
        Assert.DoesNotContain(doc.Items, a => a.Id == note.Id);
        Assert.Contains(doc.Items, a => a.Id == foreign.Id);
    }
}
