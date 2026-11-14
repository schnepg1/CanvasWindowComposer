using System;
using System.Collections.Generic;
using System.Linq;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

public class AnnotationClipboardTests
{
    private static readonly Guid Desktop = Guid.NewGuid();

    [Fact]
    public void SerializeAndDeserialize_PreservesAllAnnotationProperties()
    {
        var text = new CanvasAnnotation(
            Id: Guid.NewGuid(),
            DesktopId: Desktop,
            Kind: AnnotationKind.Text,
            A: new CanvasPoint(100, 200),
            B: new CanvasPoint(340, 248),
            Color: "#FB7185",
            Stroke: 4,
            Text: "Important note",
            FontSize: 24,
            TextAlign: AnnotationTextAlign.Center,
            Emphasis: AnnotationTextEmphasis.BoldItalic,
            CardFill: AnnotationCardFill.Tinted);

        var rect = new CanvasAnnotation(
            Id: Guid.NewGuid(),
            DesktopId: Desktop,
            Kind: AnnotationKind.Rectangle,
            A: new CanvasPoint(50, 60),
            B: new CanvasPoint(250, 160),
            Color: "#34D399",
            Stroke: 2,
            CardFill: AnnotationCardFill.None);

        var arrow = new CanvasAnnotation(
            Id: Guid.NewGuid(),
            DesktopId: Desktop,
            Kind: AnnotationKind.Arrow,
            A: new CanvasPoint(340, 224),
            B: new CanvasPoint(50, 110),
            Color: "#60A5FA",
            Stroke: 3,
            SourceId: text.Id,
            SourcePort: ConnectionPort.Right,
            TargetId: rect.Id,
            TargetPort: ConnectionPort.Left,
            Routing: ConnectorRouting.Curved);

        var items = new[] { text, rect, arrow };
        string json = AnnotationClipboard.Serialize(items);
        Assert.False(string.IsNullOrWhiteSpace(json));

        bool success = AnnotationClipboard.TryDeserialize(json, out var deserialized);
        Assert.True(success);
        Assert.Equal(3, deserialized.Count);

        var dText = deserialized.Single(a => a.Kind == AnnotationKind.Text);
        Assert.Equal(text.Id, dText.Id);
        Assert.Equal(text.Text, dText.Text);
        Assert.Equal(text.FontSize, dText.FontSize);
        Assert.Equal(text.TextAlign, dText.TextAlign);
        Assert.Equal(text.Emphasis, dText.Emphasis);
        Assert.Equal(text.CardFill, dText.CardFill);
        Assert.Equal(text.Color, dText.Color);
        Assert.Equal(text.Stroke, dText.Stroke);

        var dRect = deserialized.Single(a => a.Kind == AnnotationKind.Rectangle);
        Assert.Equal(rect.Id, dRect.Id);
        Assert.Equal(rect.Color, dRect.Color);
        Assert.Equal(rect.CardFill, dRect.CardFill);

        var dArrow = deserialized.Single(a => a.Kind == AnnotationKind.Arrow);
        Assert.Equal(arrow.Id, dArrow.Id);
        Assert.Equal(arrow.SourceId, dArrow.SourceId);
        Assert.Equal(arrow.SourcePort, dArrow.SourcePort);
        Assert.Equal(arrow.TargetId, dArrow.TargetId);
        Assert.Equal(arrow.TargetPort, dArrow.TargetPort);
        Assert.Equal(arrow.Routing, dArrow.Routing);
    }

    [Fact]
    public void Serialize_ExcludesAgentCards()
    {
        var text = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text, new(10, 10), new(200, 50), Text: "User note");
        var agent = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text, new(250, 10), new(450, 120),
            Text: "AI Agent", AgentTaskId: "task-123", AgentRole: "Coder");

        string json = AnnotationClipboard.Serialize([text, agent]);
        Assert.True(AnnotationClipboard.TryDeserialize(json, out var items));
        Assert.Single(items);
        Assert.Equal(text.Id, items[0].Id);
    }

    [Fact]
    public void TryDeserialize_WithInvalidOrEmptyInput_ReturnsFalse()
    {
        Assert.False(AnnotationClipboard.TryDeserialize(null, out var items1));
        Assert.Empty(items1);

        Assert.False(AnnotationClipboard.TryDeserialize("", out var items2));
        Assert.Empty(items2);

        Assert.False(AnnotationClipboard.TryDeserialize("{ invalid json }", out var items3));
        Assert.Empty(items3);

        Assert.False(AnnotationClipboard.TryDeserialize("{\"Version\":1,\"Items\":[]}", out var items4));
        Assert.Empty(items4);
    }

    [Fact]
    public void PrepareForPaste_GeneratesNewGuids_AndAppliesOffset()
    {
        var targetDesktop = Guid.NewGuid();
        var orig1 = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Rectangle, new(100, 150), new(300, 250));
        var orig2 = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text, new(400, 450), new(640, 498), Text: "Hello");

        var prepared = AnnotationClipboard.PrepareForPaste([orig1, orig2], targetDesktop, 32, 32);

        Assert.Equal(2, prepared.Count);
        Assert.All(prepared, a => Assert.Equal(targetDesktop, a.DesktopId));
        Assert.All(prepared, a => Assert.NotEqual(Guid.Empty, a.Id));
        Assert.DoesNotContain(prepared, a => a.Id == orig1.Id || a.Id == orig2.Id);

        var p1 = prepared.Single(a => a.Kind == AnnotationKind.Rectangle);
        Assert.Equal(132, p1.A.X);
        Assert.Equal(182, p1.A.Y);
        Assert.Equal(332, p1.B.X);
        Assert.Equal(282, p1.B.Y);

        var p2 = prepared.Single(a => a.Kind == AnnotationKind.Text);
        Assert.Equal(432, p2.A.X);
        Assert.Equal(482, p2.A.Y);
        Assert.Equal(672, p2.B.X);
        Assert.Equal(530, p2.B.Y);
    }

    [Fact]
    public void PrepareForPaste_ConnectedArrowAndCards_PreservesConnections()
    {
        var targetDesktop = Guid.NewGuid();
        var cardA = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text, new(100, 100), new(300, 150), Text: "Card A");
        var cardB = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text, new(400, 100), new(600, 150), Text: "Card B");
        var arrow = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Arrow, new(300, 125), new(400, 125),
            SourceId: cardA.Id, SourcePort: ConnectionPort.Right,
            TargetId: cardB.Id, TargetPort: ConnectionPort.Left);

        var prepared = AnnotationClipboard.PrepareForPaste([cardA, cardB, arrow], targetDesktop, 64, 64);
        Assert.Equal(3, prepared.Count);

        var newCardA = prepared.Single(a => a.Text == "Card A");
        var newCardB = prepared.Single(a => a.Text == "Card B");
        var newArrow = prepared.Single(a => a.Kind == AnnotationKind.Arrow);

        Assert.Equal(newCardA.Id, newArrow.SourceId);
        Assert.Equal(ConnectionPort.Right, newArrow.SourcePort);
        Assert.Equal(newCardB.Id, newArrow.TargetId);
        Assert.Equal(ConnectionPort.Left, newArrow.TargetPort);
    }

    [Fact]
    public void PrepareForPaste_IsolatedArrow_DetachesMissingEndpoints()
    {
        var targetDesktop = Guid.NewGuid();
        var externalSourceId = Guid.NewGuid();
        var externalTargetId = Guid.NewGuid();

        var arrow = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Arrow, new(100, 100), new(200, 200),
            SourceId: externalSourceId, SourcePort: ConnectionPort.Right,
            TargetId: externalTargetId, TargetPort: ConnectionPort.Left);

        var prepared = AnnotationClipboard.PrepareForPaste([arrow], targetDesktop, 32, 32);
        Assert.Single(prepared);

        var newArrow = prepared[0];
        Assert.Null(newArrow.SourceId);
        Assert.Equal(ConnectionPort.Auto, newArrow.SourcePort);
        Assert.Null(newArrow.TargetId);
        Assert.Equal(ConnectionPort.Auto, newArrow.TargetPort);
    }

    [Fact]
    public void CreateFromPlainText_ProducesValidTextAnnotation()
    {
        var text = "Hello Canvas Note";
        var pos = new CanvasPoint(500, 600);
        var item = AnnotationClipboard.CreateFromPlainText(text, Desktop, pos, color: "#60A5FA", stroke: 2, fontSize: 24);

        Assert.NotNull(item);
        Assert.Equal(Desktop, item.DesktopId);
        Assert.Equal(AnnotationKind.Text, item.Kind);
        Assert.Equal(text, item.Text);
        Assert.Equal("#60A5FA", item.Color);
        Assert.Equal(2, item.Stroke);
        Assert.Equal(24, item.FontSize);
        Assert.Equal(500, item.A.X);
        Assert.Equal(600, item.A.Y);
        Assert.Equal(500 + AnnotationCanvasTool.DefaultTextCardWidth, item.B.X);
        Assert.Equal(600 + AnnotationCanvasTool.DefaultTextCardHeight, item.B.Y);
        Assert.Equal(AnnotationCanvasTool.DefaultTextCardWidth, item.Bounds.Width);
        Assert.Equal(AnnotationCanvasTool.DefaultTextCardHeight, item.Bounds.Height);

        // Document validation must succeed
        AnnotationDocument.Validate([item]);
    }

    [Fact]
    public void AnnotationDocument_Paste_CreatesSingleUndoStep()
    {
        var doc = new AnnotationDocument();
        var item1 = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Text, new(10, 10), new(210, 58), Text: "Item 1");
        var item2 = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Rectangle, new(250, 10), new(350, 110));
        var item3 = new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Ellipse, new(400, 10), new(500, 110));

        bool pasted = doc.Paste(Desktop, [item1, item2, item3]);
        Assert.True(pasted);
        Assert.Equal(3, doc.Items.Count);

        // Single undo step must remove all 3 pasted items
        Assert.True(doc.Undo(Desktop));
        Assert.Empty(doc.Items);

        // Redo restores all 3 pasted items
        Assert.True(doc.Undo(Desktop, redo: true));
        Assert.Equal(3, doc.Items.Count);
    }

    [Fact]
    public void AnnotationDocument_Paste_ExceedingMaximumItems_Throws()
    {
        var initialItems = Enumerable.Range(0, AnnotationDocument.MaximumItems - 2)
            .Select(i => new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Rectangle, new(i, i), new(i + 10, i + 10)))
            .ToList();

        var doc = new AnnotationDocument(initialItems);
        Assert.Equal(AnnotationDocument.MaximumItems - 2, doc.Items.Count);

        var excessItems = new[]
        {
            new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Rectangle, new(100, 100), new(120, 120)),
            new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Rectangle, new(200, 200), new(220, 220)),
            new CanvasAnnotation(Guid.NewGuid(), Desktop, AnnotationKind.Rectangle, new(300, 300), new(320, 320)),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Paste(Desktop, excessItems));
        Assert.Contains("maximum limit", ex.Message);
        Assert.Equal(AnnotationDocument.MaximumItems - 2, doc.Items.Count);
    }
}
