using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class AnnotationOverviewIntegrationTests
{
    [Fact]
    [Trait("Category","NativeRendering")]
    public void AFullCanvasPreservesTheDraftAndRejectsClickAwayUntilTheSaveCanSucceed()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder=Path.Combine(Path.GetTempPath(),"canvas-draft-limit-"+Guid.NewGuid().ToString("N"));
            try
            {
                var canvas=new Canvas(); var api=new FakeWindowApi(); var input=new FakeInputRouter();
                var config=new FakeAppConfig { EnableStartupIntro=false };
                using var wm=new WindowManager(canvas,api,config,input,new FakeClock());
                using var overview=new OverviewManager(canvas,wm,api,input,config);
                var desktops=new FakeVirtualDesktops();
                overview.AttachAnnotations(desktops,Path.Combine(folder,"annotations.json"));
                var document=overview.Annotations!;
                var items=Enumerable.Range(0,AnnotationDocument.MaximumItems).Select(_=>new CanvasAnnotation(
                    Guid.NewGuid(),desktops.CurrentDesktopId,AnnotationKind.Text,new(0,0),new(100,50),Text:"Existing")).ToArray();
                document.ApplyBatch(document.Revision,desktops.CurrentDesktopId,items);
                overview.TransitionTo(OverviewMode.Zooming); overview.SelectAnnotationMode(AnnotationMode.Text);
                var pass=(OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
                pass.OnMouseButtonDown!(pass,new(MouseButtons.Left,1,300,300,0));
                pass.OnMouseButtonUp!(pass,new(MouseButtons.Left,1,300,300,0)); Application.DoEvents();
                var editor=overview.AnnotationEditor!;
                editor.Editor.Text="Preserve this draft";
                Assert.False(editor.Complete(true)); Assert.Same(editor,overview.AnnotationEditor);
                overview.SelectAnnotationMode(AnnotationMode.Arrow);
                Assert.Equal(AnnotationMode.Select,overview.AnnotationTool!.Mode);
                pass.OnMouseButtonDown!(pass,new(MouseButtons.Left,1,800,600,0));
                Assert.Same(editor,overview.AnnotationEditor); Assert.False(pass.Capture);
                Assert.Equal("Preserve this draft",editor.Editor.Text);
                document.Delete(desktops.CurrentDesktopId,items[0].Id);
                Assert.True(editor.Complete(true)); Assert.Null(overview.AnnotationEditor);
                Assert.Contains(document.Items,n=>n.Text=="Preserve this draft");
                Assert.Equal(AnnotationDocument.MaximumItems,document.Items.Count);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder,true); }
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void InlineTextCommitsOnOutsideClickScalesAsATitleAndReopensWithoutLosingGeometry()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(),"canvas-inline-text-"+Guid.NewGuid().ToString("N"));
            try
            {
                var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro=false };
                using var wm = new WindowManager(canvas,api,config,input,new FakeClock());
                using var overview = new OverviewManager(canvas,wm,api,input,config);
                var desktops = new FakeVirtualDesktops();
                overview.AttachAnnotations(desktops,Path.Combine(folder,"annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming); overview.SelectAnnotationMode(AnnotationMode.Text);
                var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
                void Click(int x,int y)
                {
                    pass.OnMouseButtonDown!(pass,new(MouseButtons.Left,1,x,y,0));
                    pass.OnMouseButtonUp!(pass,new(MouseButtons.Left,1,x,y,0));
                    Application.DoEvents();
                }
                Click(300,300);
                var editor = overview.AnnotationEditor!;
                editor.Editor.Text = "PROJECTS";
                editor.FormattingToolbar.Activate(); Application.DoEvents();
                Assert.Same(editor,overview.AnnotationEditor); // Formatting belongs to the same editing session.
                Click(800,600);
                Assert.Null(overview.AnnotationEditor);
                var title = Assert.Single(overview.Annotations!.Items);
                Assert.Equal("PROJECTS",title.Text);
                Assert.Equal(AnnotationMode.Select,overview.AnnotationTool!.Mode);
                Assert.True(overview.Annotations.Undo(desktops.CurrentDesktopId));
                Assert.Empty(overview.Annotations.Items);
                Assert.False(overview.Annotations.Undo(desktops.CurrentDesktopId));
                Assert.True(overview.Annotations.Undo(desktops.CurrentDesktopId,true));

                overview.AnnotationTool.Select(title.Id);
                var view = new CanvasViewTransform(0,0,1,new(0,0,1920,1080));
                CanvasPointer At(double x,double y) => new(0,new(x,y),CanvasPointerButton.Left,CanvasModifiers.None,view);
                overview.AnnotationTool.PointerDown(At(title.Bounds.X+title.Bounds.Width,title.Bounds.Y+title.Bounds.Height));
                overview.AnnotationTool.PointerUp(At(title.Bounds.X+title.Bounds.Width*30,title.Bounds.Y+title.Bounds.Height*30));
                var large = Assert.Single(overview.Annotations.Items);
                Assert.True(large.FontSize>400);
                var restored = Assert.Single(new AnnotationPersistence(Path.Combine(folder,"annotations.json")).Document.Items);
                Assert.Equal(large,restored);
                overview.AnnotationTool.Select(large.Id);
                pass.OnKey!(pass,new(Keys.Enter)); Application.DoEvents();
                Assert.Equal(large.Bounds,overview.AnnotationEditor!.CurrentItem.Bounds);
                overview.AnnotationEditor.Editor.Text = "Changed draft";
                overview.HandleEscape();
                Assert.Equal(large,Assert.Single(overview.Annotations.Items));

                overview.AnnotationTool.Select(large.Id);
                pass.OnKey!(pass,new(Keys.Enter)); Application.DoEvents();
                overview.AnnotationEditor!.Editor.Text = "";
                overview.AnnotationEditor.Complete(true);
                Assert.Empty(overview.Annotations.Items);
                Assert.True(overview.Annotations.Undo(desktops.CurrentDesktopId));
                Assert.Equal(large,Assert.Single(overview.Annotations.Items));
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder,true); }
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void AnnotationsConsumeWindowGesturesCancelOnEscapeAndPersistOnlyCompletedEdits()
        =>WorkspaceViewTests.OnSta(()=>
        {
            var folder=Path.Combine(Path.GetTempPath(),"canvas-annotation-ui-"+Guid.NewGuid().ToString("N"));
            try
            {
                var canvas=new Canvas();var api=new FakeWindowApi();var input=new FakeInputRouter();var config=new FakeAppConfig { EnableStartupIntro=false };
                using var wm=new WindowManager(canvas,api,config,input,new FakeClock());
                using var overview=new OverviewManager(canvas,wm,api,input,config);
                var desktops=new FakeVirtualDesktops { CurrentDesktopId=Guid.NewGuid() };
                overview.AttachAnnotations(desktops,Path.Combine(folder,"annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);overview.SelectAnnotationMode(AnnotationMode.Arrow);
                var pass=(OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
                void Down()=>pass.OnMouseButtonDown!(pass,new(MouseButtons.Left,1,300,300,0));
                void Up()=>pass.OnMouseButtonUp!(pass,new(MouseButtons.Left,1,500,450,0));
                Down();Assert.True(pass.Capture);input.RaiseEscPressed();Up();Assert.Empty(overview.Annotations!.Items);
                Down();Up();Assert.Single(overview.Annotations.Items);Assert.False(pass.Capture);
                Assert.Single(new AnnotationPersistence(Path.Combine(folder,"annotations.json")).Document.Items);
                pass.OnKey!(pass,new(Keys.Control|Keys.Z));Assert.Empty(overview.Annotations.Items);
                pass.OnKey!(pass,new(Keys.Control|Keys.Y));Assert.Single(overview.Annotations.Items);
                Down();desktops.SwitchTo(Guid.NewGuid());Up();Assert.Single(overview.Annotations.Items);
                Assert.Equal(AnnotationMode.Select,overview.AnnotationTool!.Mode);Assert.False(pass.Capture);
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void TextEditorOwnsInputAndGlobalEscapeCancelsWithoutClosingOverview()
        =>WorkspaceViewTests.OnSta(()=>
        {
            var folder=Path.Combine(Path.GetTempPath(),"canvas-note-ui-"+Guid.NewGuid().ToString("N"));
            try
            {
                var canvas=new Canvas();var api=new FakeWindowApi();var input=new FakeInputRouter();var config=new FakeAppConfig { EnableStartupIntro=false };
                using var wm=new WindowManager(canvas,api,config,input,new FakeClock());using var overview=new OverviewManager(canvas,wm,api,input,config);
                overview.AttachAnnotations(new FakeVirtualDesktops(),Path.Combine(folder,"annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);overview.SelectAnnotationMode(AnnotationMode.Text);
                var pass=(OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
                void Place()
                {
                    overview.SelectAnnotationMode(AnnotationMode.Text);
                    pass.OnMouseButtonDown!(pass,new(MouseButtons.Left,1,300,300,0));
                    pass.OnMouseButtonUp!(pass,new(MouseButtons.Left,1,300,300,0));Application.DoEvents();
                }
                Place();Assert.NotNull(overview.AnnotationEditor);Assert.True(overview.CanvasInputSuspended);
                overview.AnnotationEditor!.Editor.Text="سلام 🌸\nZ and N are text";
                input.RaiseOverviewHotkey();Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
                input.RaiseEscPressed();Assert.Null(overview.AnnotationEditor);Assert.Empty(overview.Annotations!.Items);
                Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
                Place();
                var editor=overview.AnnotationEditor!;
                editor.Editor.Text="Saved note";editor.FontSize=28;
                editor.SetTextAlign(AnnotationTextAlign.Center);
                editor.SetEmphasis(AnnotationTextEmphasis.BoldItalic);
                editor.SetCardFill(AnnotationCardFill.None);
                // The contextual Done button uses the same callback as Ctrl+Enter.
                editor.Complete(true);
                var saved=Assert.Single(overview.Annotations.Items);
                Assert.Equal("Saved note",saved.Text);Assert.Equal(28,saved.FontSize);
                Assert.Equal(AnnotationTextAlign.Center,saved.TextAlign);
                Assert.Equal(AnnotationTextEmphasis.BoldItalic,saved.Emphasis);
                Assert.Equal(AnnotationCardFill.None,saved.CardFill);
                Assert.Null(overview.AnnotationEditor);
                Assert.Equal(new CanvasItemId(CanvasItemKind.Annotation,overview.Annotations.Items[0].Id),Assert.Single(overview.Selection.Ids));
                overview.SelectAnnotationMode(AnnotationMode.Navigate);overview.SelectAnnotationMode(AnnotationMode.Select);
                Assert.Equal(overview.Annotations.Items[0].Id,overview.AnnotationTool!.SelectedId);
                overview.TransitionTo(OverviewMode.Hidden);Assert.Null(overview.AnnotationEditor);
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });
}
