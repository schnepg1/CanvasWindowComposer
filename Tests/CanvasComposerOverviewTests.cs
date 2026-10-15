using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Diagnostics;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class CanvasComposerOverviewTests
{
    private sealed class Credentials : IComposerCredentials
    {
        public string? Read()=>null;
        public void Write(string key) { }
        public void Delete() { }
    }
    private sealed class Provider : ICanvasComposerProvider
    {
        public ComposerContext? Received;
        public Func<Task<ComposerPlan>> Respond=()=>Task.FromResult(Plan());
        public Task<ComposerPlan> GenerateAsync(string key,string model,string prompt,ComposerContext context,CancellationToken token)
        { Received=context;return Respond(); }
    }
    private static ComposerPlan Plan()=>new("A roadmap",[new("a","note","Start","#FFFFFF"),new("b","rectangle","Finish","#60A5FA")],[new("a","b","#FFFFFF")],[]);
    private static void Configure(CanvasComposerWindow window)
    { window.KeyBox.Text="fake-test-key";window.ModelBox.Text="test-model";window.PromptBox.Text="Build a roadmap"; }
    private static void PumpUntil(Func<bool> condition)
    {
        var clock=Stopwatch.StartNew();
        while(!condition() && clock.ElapsedMilliseconds<5000) { Application.DoEvents();Thread.Sleep(5); }
        Assert.True(condition());
    }
    private static void WithOverview(Action<OverviewManager,FakeVirtualDesktops,FakeInputRouter> action)
        =>WorkspaceViewTests.OnSta(()=>
        {
            string folder=Path.Combine(Path.GetTempPath(),"composer-ui-"+Guid.NewGuid().ToString("N"));
            try
            {
                var canvas=new Canvas();var api=new FakeWindowApi();var input=new FakeInputRouter();var config=new FakeAppConfig { EnableStartupIntro=false };
                using var wm=new WindowManager(canvas,api,config,input,new FakeClock());using var overview=new OverviewManager(canvas,wm,api,input,config);
                var desktops=new FakeVirtualDesktops { CurrentDesktopId=Guid.NewGuid() };
                overview.AttachAnnotations(desktops,Path.Combine(folder,"annotations.json"));overview.TransitionTo(OverviewMode.Zooming);
                action(overview,desktops,input);
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });
    [Fact]
    [Trait("Category","NativeRendering")]
    public void GenerationOnlyPreviewsAndApplyIsOneUndoableSavedEdit()
        =>WithOverview((overview,desktops,input)=>
        {
            var provider=new Provider();overview.OpenCanvasComposer(provider,new Credentials());var window=overview.ComposerWindow!;Configure(window);
            window.GenerateButton.PerformClick();PumpUntil(()=>!window.Busy);
            Assert.Empty(overview.Annotations!.Items);Assert.True(window.ApplyButton.Enabled);
            Assert.Empty(provider.Received!.SharedAnnotations);Assert.True(overview.CanvasInputSuspended);
            window.ApplyButton.PerformClick();Assert.Equal(3,overview.Annotations.Items.Count);
            Assert.False(window.ApplyButton.Enabled);Assert.True(overview.Annotations.Undo(desktops.CurrentDesktopId));Assert.Empty(overview.Annotations.Items);
            Assert.False(overview.Annotations.Undo(desktops.CurrentDesktopId));
        });
    [Fact]
    [Trait("Category","NativeRendering")]
    public void GlobalEscapeDiscardsPreviewAndDoesNotCloseOverview()
        =>WithOverview((overview,desktops,input)=>
        {
            overview.OpenCanvasComposer(new Provider(),new Credentials());var window=overview.ComposerWindow!;Configure(window);
            window.GenerateButton.PerformClick();PumpUntil(()=>!window.Busy);Assert.True(window.ApplyButton.Enabled);
            input.RaiseEscPressed();Assert.Null(overview.ComposerWindow);Assert.Empty(overview.Annotations!.Items);Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
        });
    [Fact]
    [Trait("Category","NativeRendering")]
    public void ManualEditsInvalidatePreviewAndDesktopSwitchClosesComposer()
        =>WithOverview((overview,desktops,input)=>
        {
            overview.OpenCanvasComposer(new Provider(),new Credentials());var window=overview.ComposerWindow!;Configure(window);
            window.GenerateButton.PerformClick();PumpUntil(()=>!window.Busy);Assert.True(window.ApplyButton.Enabled);
            overview.Annotations!.Put(new(Guid.NewGuid(),desktops.CurrentDesktopId,AnnotationKind.Text,new(0,0),new(200,100),Text:"Manual"));
            Assert.False(window.ApplyButton.Enabled);Assert.Contains("changed",window.Status);
            desktops.SwitchTo(Guid.NewGuid());Assert.Null(overview.ComposerWindow);Assert.Single(overview.Annotations.Items);
        });
    [Fact]
    [Trait("Category","NativeRendering")]
    public void LateResponseAfterCancelCannotReenableApplyOrChangeDocument()
        =>WithOverview((overview,desktops,input)=>
        {
            var completion=new TaskCompletionSource<ComposerPlan>();var provider=new Provider { Respond=()=>completion.Task };
            overview.OpenCanvasComposer(provider,new Credentials());var window=overview.ComposerWindow!;Configure(window);window.GenerateButton.PerformClick();Assert.True(window.Busy);
            window.Discard("Cancelled");completion.SetResult(Plan());Application.DoEvents();
            Assert.False(window.ApplyButton.Enabled);Assert.Empty(overview.Annotations!.Items);Assert.Equal("Cancelled",window.Status);
        });
}
