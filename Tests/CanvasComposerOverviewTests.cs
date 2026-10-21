using CanvasDesktop;
using CanvasDesktop.Ai;
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
        public int Requests;
        public IReadOnlyList<AiModel> Models=[new("test-model","Test model")];
        public ComposerContext? Received;
        public Func<Task<ComposerPlan>> Respond=()=>Task.FromResult(Plan());
        public Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken token) => Task.FromResult(Models);
        public Task<ComposerPlan> GenerateAsync(string model,string prompt,ComposerContext context,IProgress<string>? progress,CancellationToken token)
        { Requests++;Received=context;return Respond(); }
    }
    private sealed class Accounts(Provider? provider = null) : IChatGptAccounts
    {
        public IReadOnlyList<ChatGptAccount> AccountsList = [];
        IReadOnlyList<ChatGptAccount> IChatGptAccounts.Accounts => AccountsList;
        public ICanvasComposerProvider CreateProvider(string id) => provider!;
        public Task<ChatGptAccount> SignInAsync(string? id, CancellationToken token) => throw new NotSupportedException();
        public Task<string> SignOutAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public void AcknowledgeWelcome(string id) { }
    }
    private sealed class SignInAccounts : IChatGptAccounts
    {
        public IReadOnlyList<ChatGptAccount> AccountsList = [];
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<ChatGptAccount> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<ChatGptAccount> IChatGptAccounts.Accounts => AccountsList;
        public ICanvasComposerProvider CreateProvider(string id) => throw new NotSupportedException();
        public async Task<ChatGptAccount> SignInAsync(string? id, CancellationToken token)
        {
            Started.TrySetResult();
            try { return await Completion.Task.WaitAsync(token); }
            finally { Finished.TrySetResult(); }
        }
        public Task<string> SignOutAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public void AcknowledgeWelcome(string id) { }
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
            var provider=new Provider();overview.OpenCanvasComposer(provider,new Credentials(),new Accounts());var window=overview.ComposerWindow!;Configure(window);
            window.GenerateButton.PerformClick();PumpUntil(()=>!window.Busy);
            Assert.Empty(overview.Annotations!.Items);Assert.True(window.ApplyButton.Enabled);
            Assert.Empty(provider.Received!.SharedAnnotations);Assert.True(overview.CanvasInputSuspended);
            if (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is {} workspace)
            {
                string artifacts = Path.Combine(workspace, "TestResults"); Directory.CreateDirectory(artifacts);
                using var bitmap = new System.Drawing.Bitmap(window.Width,window.Height);
                window.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,window.Width,window.Height));
                bitmap.Save(Path.Combine(artifacts,"ai-composer-preview.png"),System.Drawing.Imaging.ImageFormat.Png);
            }
            window.ApplyButton.PerformClick();Assert.Equal(3,overview.Annotations.Items.Count);
            Assert.False(window.ApplyButton.Enabled);Assert.True(overview.Annotations.Undo(desktops.CurrentDesktopId));Assert.Empty(overview.Annotations.Items);
            Assert.False(overview.Annotations.Undo(desktops.CurrentDesktopId));
        });
    [Fact]
    [Trait("Category","NativeRendering")]
    public void GlobalEscapeDiscardsPreviewAndDoesNotCloseOverview()
        =>WithOverview((overview,desktops,input)=>
        {
            overview.OpenCanvasComposer(new Provider(),new Credentials(),new Accounts());var window=overview.ComposerWindow!;Configure(window);
            window.GenerateButton.PerformClick();PumpUntil(()=>!window.Busy);Assert.True(window.ApplyButton.Enabled);
            input.RaiseEscPressed();Assert.Null(overview.ComposerWindow);Assert.Empty(overview.Annotations!.Items);Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
        });
    [Fact]
    [Trait("Category","NativeRendering")]
    public void BrowserSignInYieldsForegroundAndRestoresCanvasWhenItFinishes()
        =>WithOverview((overview,desktops,input)=>
        {
            var accounts=new SignInAccounts();
            overview.OpenCanvasComposer(new Provider(),new Credentials(),accounts);var window=overview.ComposerWindow!;
            var pass=(OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            Assert.True(window.TopMost);Assert.True(pass.TopMost);
            window.Connections.SourceBox.SelectedIndex=1;
            window.Connections.SignInButton.PerformClick();
            PumpUntil(()=>window.WaitingForBrowser);
            Assert.False(window.TopMost);Assert.False(pass.TopMost);Assert.True(overview.CanvasInputSuspended);
            accounts.Completion.SetResult(new ChatGptAccount("account","Account",true,false,false));
            PumpUntil(()=>!window.WaitingForBrowser);
            Assert.True(window.TopMost);Assert.True(pass.TopMost);
        });
    [Fact]
    [Trait("Category","NativeRendering")]
    public void CancellingBrowserSignInRestoresOverviewTopmost()
        =>WithOverview((overview,desktops,input)=>
        {
            var accounts=new SignInAccounts();
            overview.OpenCanvasComposer(new Provider(),new Credentials(),accounts);var window=overview.ComposerWindow!;
            var pass=(OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            window.Connections.SourceBox.SelectedIndex=1;
            window.Connections.SignInButton.PerformClick();
            PumpUntil(()=>window.WaitingForBrowser);Assert.False(pass.TopMost);
            input.RaiseEscPressed();
            PumpUntil(()=>overview.ComposerWindow==null);
            PumpUntil(()=>accounts.Finished.Task.IsCompleted);
            Assert.True(pass.TopMost);Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
        });
    [Fact]
    [Trait("Category","NativeRendering")]
    public void ManualEditsInvalidatePreviewAndDesktopSwitchClosesComposer()
        =>WithOverview((overview,desktops,input)=>
        {
            overview.OpenCanvasComposer(new Provider(),new Credentials(),new Accounts());var window=overview.ComposerWindow!;Configure(window);
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
            overview.OpenCanvasComposer(provider,new Credentials(),new Accounts());var window=overview.ComposerWindow!;Configure(window);window.GenerateButton.PerformClick();Assert.True(window.Busy);
            window.Discard("Cancelled");completion.SetResult(Plan());Application.DoEvents();
            Assert.False(window.ApplyButton.Enabled);Assert.Empty(overview.Annotations!.Items);Assert.Equal("Cancelled",window.Status);
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void SwitchingFundingSourceDiscardsPreviewAndUsesOnlyTheSelectedProvider()
        =>WithOverview((overview,desktops,input)=>
        {
            var api=new Provider();var chat=new Provider();
            var accounts=new Accounts(chat) { AccountsList=[new("account-a","Account A",true,true,false)] };
            overview.OpenCanvasComposer(api,new Credentials(),accounts);var window=overview.ComposerWindow!;Configure(window);
            window.GenerateButton.PerformClick();PumpUntil(()=>!window.Busy);Assert.True(window.ApplyButton.Enabled);
            window.Connections.SourceBox.SelectedIndex=1;PumpUntil(()=>!window.Busy);
            Assert.False(window.ApplyButton.Enabled);Assert.Empty(overview.Annotations!.Items);
            Assert.Equal(ComboBoxStyle.DropDownList,window.ModelBox.DropDownStyle);
            Assert.Equal("test-model",Assert.IsType<AiModel>(window.ModelBox.SelectedItem).Id);
            window.GenerateButton.PerformClick();PumpUntil(()=>!window.Busy);
            Assert.Equal(1,api.Requests);Assert.Equal(1,chat.Requests);Assert.True(window.ApplyButton.Enabled);
            if(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is {} workspace)
            {
                string artifacts=Path.Combine(workspace,"TestResults");Directory.CreateDirectory(artifacts);
                using var bitmap=new System.Drawing.Bitmap(window.Width,window.Height);
                window.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,window.Width,window.Height));
                bitmap.Save(Path.Combine(artifacts,"ai-chatgpt-connection.png"),System.Drawing.Imaging.ImageFormat.Png);
            }
            window.Connections.SourceBox.SelectedIndex=0;
            Assert.False(window.ApplyButton.Enabled);Assert.Equal(ComboBoxStyle.DropDown,window.ModelBox.DropDownStyle);
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void ClosingComposerClearsSecretsWithoutReenteringConnectionEvents()
        =>WithOverview((overview,desktops,input)=>
        {
            overview.OpenCanvasComposer(new Provider(),new Credentials(),new Accounts());var window=overview.ComposerWindow!;Configure(window);
            int changes=0;window.Connections.ConnectionChanged+=()=>changes++;
            input.RaiseEscPressed();
            Assert.Null(overview.ComposerWindow);Assert.Equal(0,changes);Assert.Empty(overview.Annotations!.Items);
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void ModelSelectionInvalidatesProposalEvenWhenDisplayNamesAreIdentical()
        =>WithOverview((overview,desktops,input)=>
        {
            var chat=new Provider { Models=[new("first","Same name"),new("second","Same name")] };
            var accounts=new Accounts(chat) { AccountsList=[new("account","Account",true,true,false)] };
            overview.OpenCanvasComposer(new Provider(),new Credentials(),accounts);var window=overview.ComposerWindow!;Configure(window);
            window.Connections.SourceBox.SelectedIndex=1;PumpUntil(()=>!window.Busy);
            window.GenerateButton.PerformClick();PumpUntil(()=>!window.Busy);Assert.True(window.ApplyButton.Enabled);
            window.ModelBox.SelectedIndex=1;
            Assert.False(window.ApplyButton.Enabled);Assert.Empty(overview.Annotations!.Items);
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void AccountSwitchClearsModelsAndInvalidatesTheOldProposal()
        =>WithOverview((overview,desktops,input)=>
        {
            var chat=new Provider();var accounts=new Accounts(chat)
            { AccountsList=[new("account-a","Same email A",true,true,false),new("account-b","Same email B",true,false,false)] };
            overview.OpenCanvasComposer(new Provider(),new Credentials(),accounts);var window=overview.ComposerWindow!;Configure(window);
            window.Connections.SourceBox.SelectedIndex=1;PumpUntil(()=>!window.Busy);
            window.GenerateButton.PerformClick();PumpUntil(()=>!window.Busy);Assert.True(window.ApplyButton.Enabled);
            window.Connections.AccountBox.SelectedIndex=1;
            Assert.False(window.ApplyButton.Enabled);Assert.Empty(window.ModelBox.Items.Cast<object>());
            window.GenerateButton.PerformClick();PumpUntil(()=>!window.Busy);
            Assert.False(window.ApplyButton.Enabled);Assert.Equal(1,chat.Requests);Assert.Empty(overview.Annotations!.Items);
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void SharedSelectionStartsCheckedAndLayoutActionPreservesExistingNotes()
        =>WithOverview((overview,desktops,input)=>
        {
            var a=new CanvasAnnotation(Guid.NewGuid(),desktops.CurrentDesktopId,AnnotationKind.Text,new(600,300),new(900,460),Text:"Exact wording");
            var b=a with { Id=Guid.NewGuid(),A=new(1200,300),B=new(1500,460),Text="Second note" };
            overview.Annotations!.ApplyBatch(0,desktops.CurrentDesktopId,[a,b]);
            var project=new WorkspaceZone { Name="Guardian",DesktopId=desktops.CurrentDesktopId,Bounds=new WorldRect { X=0,Y=0,W=2000,H=1000 } };
            overview.AttachZones(new ZoneService(new WorkspaceDocument { Zones=[project] },()=>desktops.CurrentDesktopId,()=>true));
            overview.Selection.Pick(new(CanvasItemKind.Project,project.Id),overview.SelectionItems(),false);
            var provider=new Provider { Respond=()=>Task.FromResult(new ComposerPlan("Arranged",[new(a.Id.ToString(),"note","Ignored rewrite","#FFFFFF"),new(b.Id.ToString(),"note","Ignored","#FFFFFF")],[],[])) };
            overview.OpenCanvasComposer(provider,new Credentials(),new Accounts());var window=overview.ComposerWindow!;Configure(window);
            Assert.Equal(3,window.ContextBox.CheckedItems.Count);window.ActionBox.SelectedIndex=4;
            window.GenerateButton.PerformClick();PumpUntil(()=>!window.Busy);
            Assert.True(provider.Received!.PreserveWording);Assert.Equal(2,provider.Received.SharedAnnotations.Count);Assert.Equal("Guardian",Assert.Single(provider.Received.SharedReferences).Title);Assert.True(window.ApplyButton.Enabled);
            Assert.Equal(new[]{a,b},overview.Annotations.Items);
            window.ApplyButton.PerformClick();Assert.Equal(2,overview.Annotations.Items.Count);
            Assert.Equal(a.Text,overview.Annotations.Items.Single(n=>n.Id==a.Id).Text);
            Assert.True(overview.Annotations.Undo(desktops.CurrentDesktopId));Assert.Equal(new[]{a,b},overview.Annotations.Items);
        });
}
