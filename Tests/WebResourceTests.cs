using System.Text.Json;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public sealed class WebResourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "canvas-web-tests-" + Guid.NewGuid().ToString("N"));
    public WebResourceTests() => Directory.CreateDirectory(_root);
    private string Exe(string name)
    { string exe = Path.Combine(_root, name); File.WriteAllText(exe, "Not an executable; argument construction only."); return exe; }
    private string Profile(string browser, string name = "Work & spaces")
    {
        string path = Path.Combine(_root, browser, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, browser == "firefox" ? "prefs.js" : "Preferences"), "{}"); return path;
    }
    private WorkspaceItem Item(string exe, string url = "https://dash.cloudflare.com/", string profile = "") => new()
    {
        ExecutablePath = exe, Name = "Cloudflare", Title = "Cloudflare dashboard", CaptureChromeProfile = false,
        WebResource = new(url, profile), Bounds = new WorldRect { X = 2400, Y = -100, W = 1000, H = 700 }
    };

    [Theory]
    [InlineData("https://dash.cloudflare.com/")]
    [InlineData("http://localhost:8080/path?one=1&two=2#section")]
    [InlineData("https://example.com/a%20b?x=%22%26")]
    public void WebAddressesAreAccepted(string url) => Assert.True(BrowserSupport.ValidUrl(url));

    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("shell:startup")]
    [InlineData("--profile-directory=Default")]
    [InlineData("https://example.com/ --gpu-launcher=cmd.exe")]
    [InlineData("https://example.com/\r\n--flag")]
    [InlineData("https://user:password@example.com/")]
    public void NonWebSchemesCredentialsAndOptionsAreRejected(string url) => Assert.False(BrowserSupport.ValidUrl(url));

    [Theory]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("msedge.exe", "edge")]
    [InlineData("firefox.exe", "firefox")]
    public void EachBrowserGetsItsOwnEscapedProfileAndNewWindowArguments(string exeName, string kind)
    {
        string exe = Exe(exeName), profile = Profile(kind);
        var item = Item(exe, "https://example.com/?x=one&y=two#part", profile);
        var launch = WorkspaceIdentity.LaunchInfo(item);
        Assert.False(launch.UseShellExecute); Assert.Empty(launch.Arguments); Assert.Empty(launch.Verb);
        if (kind == "firefox")
            Assert.Equal(new[] { "--profile", profile, "--new-window", item.WebResource!.Url }, launch.ArgumentList);
        else
            Assert.Equal(new[] { "--user-data-dir=" + Path.GetDirectoryName(profile), "--profile-directory=" + Path.GetFileName(profile), "--new-window", item.WebResource!.Url }, launch.ArgumentList);
        var unprofiled = WorkspaceIdentity.LaunchInfo(Item(exe));
        Assert.Equal(new[] { "--new-window", "https://dash.cloudflare.com/" }, unprofiled.ArgumentList);
        File.Delete(Path.Combine(profile, kind == "firefox" ? "prefs.js" : "Preferences"));
        Assert.Throws<IOException>(() => WorkspaceIdentity.LaunchInfo(item));
    }

    [Fact]
    public void WebsiteTargetsCannotReachUnsupportedExecutablesOrCombineWithFileTargets()
    {
        Assert.Throws<IOException>(() => WorkspaceIdentity.LaunchInfo(Item(Exe("cmd.exe"))));
        var item = Item(Exe("chrome.exe")); item.Target = _root;
        Assert.Throws<IOException>(() => WorkspaceIdentity.LaunchInfo(item));
        item.IsFolder = true;
        Assert.Throws<IOException>(() => WorkspaceIdentity.LaunchInfo(item));
        item.IsFolder = false;
        item.Target = ""; item.WebResource = new("javascript:alert(1)");
        Assert.Throws<IOException>(() => WorkspaceIdentity.LaunchInfo(item));
    }

    [Fact]
    public void FirefoxProfilesResolveRelativeAndAbsolutePathsWithoutChangingMetadata()
    {
        string relative = Path.Combine(_root, "Profiles", "abc.work"); Directory.CreateDirectory(relative);
        string absolute = Profile("firefox", "Personal");
        string ini = "[Profile0]\nName=Work\nIsRelative=1\nPath=Profiles/abc.work\n\n[Install123]\nDefault=ignored\n\n[Profile1]\nName=Personal\nIsRelative=0\nPath=" + absolute + "\n[Profile2]\nName=Missing\nPath=Profiles/missing\n";
        File.WriteAllText(Path.Combine(_root, "profiles.ini"), ini);
        var profiles = BrowserSupport.ReadFirefoxProfiles(_root);
        Assert.Equal(2, profiles.Count); Assert.Contains(profiles, p => p.Name == "Work" && p.Path == relative);
        Assert.Contains(profiles, p => p.Name == "Personal" && p.Path == absolute);
        Assert.Equal(ini, File.ReadAllText(Path.Combine(_root, "profiles.ini")));
    }

    [Fact]
    public void WebResourcesRoundTripAndOldWorkspacesRemainApplicationPlaceholders()
    {
        var item = Item(Exe("firefox.exe"), profile: Profile("firefox"));
        var store = new WorkspaceStore(Path.Combine(_root, "workspace.json"));
        store.Save(new() { Items = [item] });
        var loaded = Assert.Single(store.Load(out _).Items);
        Assert.Equal(item.WebResource, loaded.WebResource); Assert.Equal(item.Bounds.X, loaded.Bounds.X);
        var json = JsonSerializer.SerializeToNode(new WorkspaceDocument { Items = [item] }, new JsonSerializerOptions { IncludeFields = true })!;
        json["Items"]![0]!.AsObject().Remove("WebResource"); File.WriteAllText(store.FilePath, json.ToJsonString());
        Assert.Null(Assert.Single(store.Load(out _).Items).WebResource);
        item.WebResource = new("file:///C:/secret");
        Assert.Throws<InvalidDataException>(() => store.Save(new() { Items = [item] }));
    }

    [Fact]
    public void AddAndEditAreAtomicAndChangingBrowserDoesNotCloseTheCurrentWindow()
    {
        string chrome = Exe("chrome.exe"), firefox = Exe("firefox.exe");
        using var rig = new WorkspaceHarness(); rig.Workspace.Start();
        rig.Store.SaveError = new IOException("disk full");
        Assert.False(rig.Workspace.SaveWebResource(null, chrome, "Cloudflare", new("https://dash.cloudflare.com/")));
        Assert.Empty(rig.Workspace.Document.Items); Assert.Empty(rig.Cards.Created);
        rig.Store.SaveError = null;
        Assert.True(rig.Workspace.SaveWebResource(null, chrome, "Cloudflare", new("https://dash.cloudflare.com/")));
        var item = Assert.Single(rig.Workspace.Document.Items);
        rig.Identity.Exe = chrome; rig.Workspace.Open(item.Id);
        rig.Api.AddWindow((IntPtr)42, 0, 0, 800, 600); rig.Clock.Advance(2100); rig.Workspace.Tick();
        Assert.True(rig.Workspace.IsOpen(item));
        rig.Store.SaveError = new IOException("disk full");
        Assert.False(rig.Workspace.SaveWebResource(item.Id, firefox, "Changed", new("https://example.com/")));
        Assert.Same(item, Assert.Single(rig.Workspace.Document.Items)); Assert.True(rig.Workspace.IsOpen(item));
        rig.Store.SaveError = null;
        Assert.True(rig.Workspace.SaveWebResource(item.Id, firefox, "Changed", new("https://example.com/")));
        var updated = Assert.Single(rig.Workspace.Document.Items);
        Assert.Equal(firefox, updated.ExecutablePath); Assert.Equal(item.Bounds.X, updated.Bounds.X);
        Assert.False(rig.Workspace.IsOpen(updated)); Assert.Empty(rig.Actions.Closed);
        Assert.False(rig.Workspace.SetChromeProfile(item.Id, ""));
        rig.Workspace.SetTarget(item.Id, _root, true); Assert.Empty(updated.Target);
    }

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("msedge.exe")]
    [InlineData("firefox.exe")]
    public void ExistingTitlesOrAUniqueProfileCannotProveAWebsiteIdentity(string name)
    {
        var item = Item(Exe(name), profile: Profile(name == "firefox.exe" ? "firefox" : "chrome"));
        var match = WorkspaceMatcher.Evaluate([item], Guid.Empty, item.ExecutablePath, item.Title, "", [], chromeProfilePath: item.WebResource!.ProfilePath);
        Assert.True(match.IsAmbiguous); Assert.Null(match.Item);
        match = WorkspaceMatcher.Evaluate([item], Guid.Empty, item.ExecutablePath, "New window", "", [], item.Id, item.WebResource.ProfilePath);
        Assert.Same(item, match.Item); Assert.Equal(WorkspaceMatchStrength.Launch, match.Strength);
        Assert.Null(WorkspaceMatcher.Evaluate([item], Guid.Empty, item.ExecutablePath, item.Title, "", [], item.Id, Profile("other")).Item);
    }

    [Fact]
    public void MultipleNewWindowsCannotWinByEnumerationOrder()
    {
        var item = Item(Exe("firefox.exe"));
        var windows = new[] { new WorkspaceMatchInput(Guid.NewGuid(), item.ExecutablePath, "One", "", item.Id),
            new WorkspaceMatchInput(Guid.NewGuid(), item.ExecutablePath, "Two", "", item.Id) };
        var plan = WorkspaceMatcher.Plan([item], Guid.Empty, windows, []);
        Assert.All(plan.Values, m => Assert.True(m.IsAmbiguous));
    }

    [Fact]
    public void SameBrowserResourcesLaunchOneAtATimeAndKeepSeparateBindings()
    {
        string exe = Exe("firefox.exe"), profile = Profile("firefox");
        var first = Item(exe, "https://dash.cloudflare.com/", profile);
        var second = Item(exe, "https://github.com/", profile);
        second.Bounds = new WorldRect { X = -2200, Y = 100, W = 900, H = 600 };
        using var rig = new WorkspaceHarness(new() { Items = [first, second] }); rig.Identity.Exe = exe;
        rig.Workspace.Start(); rig.Workspace.Open(first.Id); rig.Workspace.Open(second.Id);
        Assert.Single(rig.Launcher.Opened); Assert.Equal("Queued", rig.Workspace.ItemState(second));
        rig.Api.AddWindow((IntPtr)42, 0, 0, 800, 600, title: "First site"); rig.Clock.Advance(2100); rig.Workspace.Tick();
        Assert.Equal(new[] { first.Id, second.Id }, rig.Launcher.Opened);
        rig.Api.AddWindow((IntPtr)43, 0, 0, 800, 600, title: "Second site"); rig.Clock.Advance(2100); rig.Workspace.Tick();
        Assert.True(rig.Workspace.IsOpen(first)); Assert.True(rig.Workspace.IsOpen(second));
        Assert.Equal(2400, rig.Canvas.Windows[(IntPtr)42].X); Assert.Equal(-2200, rig.Canvas.Windows[(IntPtr)43].X);
        Assert.True(rig.Workspace.SaveWebResource(first.Id, exe, "Renamed", first.WebResource!));
        Assert.True(rig.Workspace.IsOpen(Assert.Single(rig.Workspace.Document.Items, i => i.Id == first.Id)));
    }

    [Fact]
    public void ReopenFocusesAttachedWindowAndBrowsingDoesNotRewriteTheSavedUrl()
    {
        var item = Item(Exe("firefox.exe"));
        using var rig = new WorkspaceHarness(new() { Items = [item] }); rig.Identity.Exe = item.ExecutablePath;
        rig.Workspace.Start(); rig.Workspace.Open(item.Id);
        rig.Api.AddWindow((IntPtr)42, 0, 0, 800, 600, title: "Login redirect"); rig.Clock.Advance(2100); rig.Workspace.Tick();
        Assert.True(rig.Workspace.IsOpen(item)); Assert.Equal(item.Bounds.X, rig.Canvas.Windows[(IntPtr)42].X);
        rig.Workspace.Open(item.Id); Assert.Single(rig.Launcher.Opened);
        Assert.Single(rig.Actions.Focused, f => f.Handle == (IntPtr)42);
        rig.Api.Windows[(IntPtr)42].Title = "Another website"; rig.Clock.Advance(2100); rig.Workspace.Tick();
        Assert.Equal("https://dash.cloudflare.com/", item.WebResource!.Url);
    }

    [Fact]
    public void RestartedBrowserWindowsRequireExplicitLinking()
    {
        var item = Item(Exe("firefox.exe"), profile: Profile("firefox"));
        using var rig = new WorkspaceHarness(new() { Items = [item] }); rig.Identity.Exe = item.ExecutablePath;
        rig.Api.AddWindow((IntPtr)42, 0, 0, 800, 600, title: item.Title); rig.Workspace.Start();
        Assert.False(rig.Workspace.IsOpen(item)); Assert.Equal("Needs linking", rig.Workspace.Items[0].State);
        Assert.Single(rig.Workspace.Document.Items);
        var candidate = Assert.Single(rig.Workspace.LinkCandidates(item.Id));
        Assert.True(rig.Workspace.LinkWindow(item.Id, candidate.Token));
        Assert.Equal(item.Bounds.X, rig.Canvas.Windows[(IntPtr)42].X);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
