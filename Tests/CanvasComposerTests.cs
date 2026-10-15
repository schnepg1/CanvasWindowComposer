using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class CanvasComposerTests
{
    private static readonly Guid Desktop = Guid.NewGuid();
    private static CanvasAnnotation Note(string text = "Original") => new(Guid.NewGuid(), Desktop, AnnotationKind.Text, new(0,0), new(300,160), Text:text);
    private static ComposerContext Context(params CanvasAnnotation[] shared) => new(Desktop, 0, new(100,100), shared, []);
    private static ComposerPlan Diagram() => new("Two steps", [new("a","note","Start","#60A5FA"), new("b","rectangle","Finish","#34D399")], [new("a","b","#FFFFFF")], []);

    [Fact]
    public void ProposalsDoNotChangeDocumentAndEntireApplyIsOneUndoStep()
    {
        var doc = new AnnotationDocument([Note()]); int notifications = 0; doc.Changed += () => notifications++;
        var preview = CanvasComposer.Prepare(Context(), Diagram());
        Assert.Single(doc.Items); Assert.Equal(0,notifications);
        Assert.True(doc.ApplyBatch(preview.Revision,Desktop,preview.Items)); Assert.Equal(4,doc.Items.Count); Assert.Equal(1,notifications);
        Assert.True(doc.Undo(Desktop)); Assert.Single(doc.Items); Assert.False(doc.Undo(Desktop));
        Assert.True(doc.Undo(Desktop,true)); Assert.Equal(4,doc.Items.Count);
    }
    [Fact]
    public void InvalidBatchIsAtomicAndDoesNotCreateHistory()
    {
        var original = Note(); var doc = new AnnotationDocument([original]);
        Assert.Throws<ArgumentException>(()=>doc.ApplyBatch(0,Desktop,[original with { Text="Edited" },Note() with { Color="bad" }]));
        Assert.Equal(original,Assert.Single(doc.Items)); Assert.Equal(0,doc.Revision); Assert.False(doc.Undo(Desktop));
    }
    [Fact]
    public void RevisionPreventsStalePreviewAfterManualChangesAndUndo()
    {
        var doc = new AnnotationDocument(); var preview = CanvasComposer.Prepare(Context(),Diagram());
        doc.Put(Note()); doc.Undo(Desktop);
        Assert.Empty(doc.Items); Assert.Equal(2,doc.Revision);
        Assert.Throws<InvalidOperationException>(()=>doc.ApplyBatch(preview.Revision,Desktop,preview.Items));
    }
    [Fact]
    public void CrossDesktopBatchAndIdTheftAreRejectedBeforeAnyChange()
    {
        var original = Note() with { DesktopId=Guid.NewGuid() }; var doc = new AnnotationDocument([original]);
        Assert.Throws<ArgumentException>(()=>doc.ApplyBatch(0,Desktop,[original]));
        Assert.Throws<ArgumentException>(()=>doc.ApplyBatch(0,Desktop,[original with { DesktopId=Desktop }]));
        Assert.Equal(original,Assert.Single(doc.Items));
    }
    [Fact]
    public void RewriteCanOnlyTouchExplicitlySharedNotesAndPreservesGeometryAndStyle()
    {
        var original = Note() with { Color="#C084FC",Stroke=6 }; var doc = new AnnotationDocument([original]);
        var plan = new ComposerPlan("Rewrite",[],[],[new(original.Id.ToString(),"Rewritten")]);
        Assert.Throws<ArgumentException>(()=>CanvasComposer.Prepare(Context(),plan));
        var preview = CanvasComposer.Prepare(Context(original),plan);
        Assert.Equal(original with { Text="Rewritten" },Assert.Single(preview.Items));
        doc.ApplyBatch(0,Desktop,preview.Items); Assert.Equal("Rewritten",Assert.Single(doc.Items).Text);
        doc.Undo(Desktop); Assert.Equal(original,Assert.Single(doc.Items));
    }
    [Fact]
    public void DuplicateTargetsAndUnknownGraphReferencesAreRejected()
    {
        var plan = Diagram();
        Assert.Throws<ArgumentException>(()=>CanvasComposer.Prepare(Context(),plan with { Nodes=[plan.Nodes[0],plan.Nodes[0]] }));
        Assert.Throws<ArgumentException>(()=>CanvasComposer.Prepare(Context(),plan with { Edges=[new("a","missing","#FFFFFF")] }));
        Assert.Throws<ArgumentException>(()=>CanvasComposer.Prepare(Context(),plan with { Edges=[new("a","a","#FFFFFF")] }));
        var note=Note(); var edit=new ComposerEdit(note.Id.ToString(),"new");
        Assert.Throws<ArgumentException>(()=>CanvasComposer.Prepare(Context(note),new("",[],[],[edit,edit])));
    }
    [Fact]
    public void LocalLayoutAvoidsExistingObjectsAndConnectsNodeBoundaries()
    {
        var context=Context() with { Obstacles=[new(0,0,1000,1000)] };
        var preview=CanvasComposer.Prepare(context,Diagram()); var nodes=preview.Items.Where(a=>a.Kind!=AnnotationKind.Arrow).ToArray();
        Assert.All(nodes,a=>Assert.True(a.Bounds.X>1000 || a.Bounds.Y>1000));
        var arrow=Assert.Single(preview.Items,a=>a.Kind==AnnotationKind.Arrow);
        Assert.Equal(nodes[0].Bounds.X+nodes[0].Bounds.Width,arrow.A.X,5);
        Assert.Equal(nodes[1].Bounds.X,arrow.B.X,5);
        var repeated=CanvasComposer.Prepare(context,Diagram());
        Assert.Equal(preview.Items.Select(a=>(a.A,a.B)),repeated.Items.Select(a=>(a.A,a.B)));
    }
    [Fact]
    public void CyclesHaveFiniteNonOverlappingLayout()
    {
        var plan=Diagram() with { Edges=[new("a","b","#FFFFFF"),new("b","a","#FFFFFF")] };
        var preview=CanvasComposer.Prepare(Context(),plan); AnnotationDocument.Validate(preview.Items);
        Assert.Equal(4,preview.Items.Count); Assert.NotEqual(preview.Items[0].Bounds,preview.Items[1].Bounds);
    }
    [Fact]
    public void LimitsAndOutOfRangeLayoutAreRejected()
    {
        var tooMany=Enumerable.Range(0,41).Select(i=>new ComposerNode(i.ToString(),"note","x","#FFFFFF")).ToArray();
        Assert.Throws<ArgumentException>(()=>CanvasComposer.Prepare(Context(),new("",tooMany,[],[])));
        Assert.Throws<ArgumentException>(()=>CanvasComposer.Prepare(Context() with { Anchor=new(double.NaN,0) },Diagram()));
        var doc=new AnnotationDocument(Enumerable.Range(0,2000).Select(_=>Note()));
        Assert.Throws<ArgumentException>(()=>doc.ApplyBatch(0,Desktop,CanvasComposer.Prepare(Context(),Diagram()).Items));
        Assert.Equal(2000,doc.Items.Count); Assert.Equal(0,doc.Revision);
    }
    [Fact]
    public void IdenticalBatchDoesNotCreateHistoryOrNotifications()
    {
        var note=Note();var doc=new AnnotationDocument([note]);int changes=0;doc.Changed+=()=>changes++;
        Assert.False(doc.ApplyBatch(0,Desktop,[note]));Assert.Equal(0,changes);Assert.Equal(0,doc.Revision);Assert.False(doc.Undo(Desktop));
    }
}
