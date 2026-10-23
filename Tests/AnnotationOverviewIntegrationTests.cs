using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class AnnotationOverviewIntegrationTests
{
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
                    pass.OnMouseButtonDown!(pass,new(MouseButtons.Left,1,300,300,0));
                    pass.OnMouseButtonUp!(pass,new(MouseButtons.Left,1,300,300,0));Application.DoEvents();
                }
                Place();Assert.NotNull(overview.AnnotationEditor);Assert.True(overview.CanvasInputSuspended);
                overview.AnnotationEditor!.Editor.Text="سلام 🌸\nZ and N are text";
                input.RaiseOverviewHotkey();Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
                input.RaiseEscPressed();Assert.Null(overview.AnnotationEditor);Assert.Empty(overview.Annotations!.Items);
                Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
                Place();overview.AnnotationEditor!.Editor.Text="Saved note";
                // The actual Save button uses the same callback as Ctrl+Enter.
                var save=overview.AnnotationEditor.Controls.OfType<Button>().Single();save.PerformClick();
                Assert.Equal("Saved note",Assert.Single(overview.Annotations.Items).Text);Assert.Null(overview.AnnotationEditor);
                Assert.Equal(new CanvasItemId(CanvasItemKind.Annotation,overview.Annotations.Items[0].Id),Assert.Single(overview.Selection.Ids));
                overview.SelectAnnotationMode(AnnotationMode.Navigate);overview.SelectAnnotationMode(AnnotationMode.Select);
                Assert.Equal(overview.Annotations.Items[0].Id,overview.AnnotationTool!.SelectedId);
                overview.TransitionTo(OverviewMode.Hidden);Assert.Null(overview.AnnotationEditor);
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });
}
