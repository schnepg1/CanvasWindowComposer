using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class TutorialLayoutTests
{
    private static readonly Guid Desktop=Guid.NewGuid();
    private static readonly CanvasRect View=new(0,0,1920,1080);

    [Fact]
    public void NotesAvoidWindowsAndPersonalContentAndStayNearTheVisibleLandmark()
    {
        CanvasRect[] windows=[new(100,200,800,600),new(900,0,1000,1080),new(100000,100000,1000,1000)];
        CanvasRect[] content=[new(-1000,200,900,800)];
        var layout=TutorialLayout.Create(Desktop,View,windows,content);
        AnnotationDocument.Validate(layout.Items);
        var notes=layout.Items.Where(a=>a.Kind==AnnotationKind.Text).ToArray();
        Assert.Equal(4,notes.Length);
        foreach(var note in notes)
            Assert.All(windows.Concat(content),r=>Assert.False(TutorialLayout.Intersects(note.Bounds,r)));
        Assert.True(Math.Abs(layout.Welcome.X)<10000); // A distant saved window must not anchor the tutorial.
        Assert.All(layout.Items,a=>Assert.Equal(Desktop,a.DesktopId));
    }

    [Fact]
    public void EmptyDesktopGetsSampleLandmarkAndNeverRequiresAnApplicationToLaunch()
    {
        var layout=TutorialLayout.Create(Desktop,View,[],[]);
        Assert.Single(layout.Items,a=>a.Kind==AnnotationKind.Rectangle);
        Assert.Equal(4,layout.Items.Count(a=>a.Kind==AnnotationKind.Text));
        Assert.Equal(layout.Items[0].Bounds,layout.Welcome);
    }

    [Fact]
    public void DisabledShortcutsAreNotTaughtAsAvailable()
    {
        var layout=TutorialLayout.Create(Desktop,View,[],[],overviewHotkey:false,altPan:false);
        Assert.DoesNotContain(layout.Items,a=>a.Text.Contains("Alt+Q") || a.Text.Contains("Alt+middle"));
        Assert.Contains(layout.Items,a=>a.Text.Contains("Ctrl+scroll"));
        Assert.Contains(layout.Items,a=>a.Text.Contains("two fingers"));
    }

    [Theory]
    [InlineData(1,2)]
    [InlineData(2,1)]
    public void DpiMigrationPreservesOriginIdentityAndRealWindowConnector(double initial,double next)
    {
        var layout=TutorialLayout.Create(Desktop,View,[new(100,100,800,500)],[],initial);
        var updated=layout.WithScale(next);
        Assert.Equal(layout.Items.Select(a=>a.Id),updated.Items.Select(a=>a.Id));
        Assert.Equal(layout.Welcome.X,updated.Welcome.X);Assert.Equal(layout.Welcome.Y,updated.Welcome.Y);
        Assert.Equal(next/initial*layout.Welcome.Width,updated.Welcome.Width);
        var originalArrow=layout.Items.Single(a=>a.Id==layout.LandmarkConnectorId);
        Assert.Equal(originalArrow.B,updated.Items.Single(a=>a.Id==layout.LandmarkConnectorId).B);
        var restored=updated.WithScale(initial);
        Assert.Equal(layout.Welcome,restored.Welcome);
        Assert.Same(updated,updated.WithScale(next));
    }

    [Theory]
    [InlineData(-1920,-1080,1)]
    [InlineData(1920,0,2)]
    [InlineData(0,0,3)]
    public void LayoutSupportsMonitorOriginsAndDpi(double x,double y,double scale)
    {
        var layout=TutorialLayout.Create(Desktop,new(x,y,1920,1080),[],[],scale);
        Assert.Equal(390*scale,layout.Welcome.Width);
        Assert.Equal(scale,layout.Scale);
        AnnotationDocument.Validate(layout.Items);
    }
}
