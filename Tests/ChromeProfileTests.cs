using CanvasDesktop;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CanvasDesktop.Tests;

public sealed class ChromeProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "canvas-profiles-" + Guid.NewGuid().ToString("N"));
    private const string Chrome = @"C:\Apps\chrome.exe";

    private string Profile(string directory)
    {
        string path = Path.Combine(_root, directory);
        Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "Preferences"), "{}");
        return path;
    }

    private static WorkspaceItem Item(string profile) => new() { ExecutablePath = Chrome, Title = "Same tab caption",
        ChromeProfilePath = profile, Bounds = new WorldRect { X = 100, Y = 200, W = 800, H = 550 } };

    [Fact]
    public void DiscoveryUsesNamesAndExistingProfilesAndIgnoresTraversalAndGuestEntries()
    {
        string work = Profile("Profile 1"), personal = Profile("Default"); Profile("Guest Profile");
        File.WriteAllText(Path.Combine(_root, "Local State"), """
            {"profile":{"info_cache":{"Profile 1":{"name":"Work"},"Default":{"name":"Personal"},
            "Guest Profile":{"name":"Guest"},"../outside":{"name":"Bad"},"Missing":{"name":"Gone"}}}}
            """);
        var profiles = ChromeProfiles.Read(_root);
        Assert.Equal(2, profiles.Count);
        Assert.Contains(profiles, p => p.Path == work && p.Name == "Work");
        Assert.Contains(profiles, p => p.Path == personal && p.Name == "Personal");
        File.WriteAllText(Path.Combine(_root, "Local State"), "[]");
        Assert.Equal(2, ChromeProfiles.Read(_root).Count);
        File.WriteAllText(Path.Combine(_root, "Local State"), "{partial");
        Assert.Equal(2, ChromeProfiles.Read(_root).Count);
    }

    [Fact]
    public void ProfileLaunchUsesThreeEscapedArgumentsAndMissingProfilesCannotSilentlyFallBack()
    {
        string profile = Profile("Profile 1 & work");
        string exe = Path.Combine(_root, "chrome.exe"); File.WriteAllText(exe, "");
        var item = Item(profile); item.ExecutablePath = exe;
        var launch = WorkspaceIdentity.LaunchInfo(item);
        Assert.False(launch.UseShellExecute); Assert.Empty(launch.Arguments);
        Assert.Equal(new[] { "--user-data-dir=" + _root, "--profile-directory=Profile 1 & work", "--new-window" }, launch.ArgumentList);
        File.Delete(Path.Combine(profile, "Preferences"));
        Assert.Throws<IOException>(() => WorkspaceIdentity.LaunchInfo(item));
        Assert.Throws<IOException>(() => ChromeProfiles.AddLaunchArguments(new ProcessStartInfo("cmd.exe"), profile));
    }

    [Fact]
    public void WindowIdentityDistinguishesProfilesAndRejectsWebAppsAndSanitizedCollisions()
    {
        string first = Profile("Profile 1"), second = Profile("Profile 2");
        string id = "Chrome." + ChromeProfiles.ProfileId(first);
        Assert.Equal(first, ChromeProfiles.Resolve(Chrome, id, [first, second]));
        Assert.Equal(first, ChromeProfiles.Resolve(Chrome, "Chrome.2LGS6APA4UDF4BG3HSPLQ25VFU." + ChromeProfiles.ProfileId(first), [first, second]));
        Assert.Null(ChromeProfiles.Resolve(Chrome, "Chrome._crx_example." + ChromeProfiles.ProfileId(first), [first, second]));
        Assert.Null(ChromeProfiles.Resolve(Chrome, null, [first, second]));
        Assert.Null(ChromeProfiles.Resolve(Chrome, id, [first, Profile("Profile1")]));
        string defaultProfile = Path.Combine(ChromeProfiles.UserDataDirectory(Chrome), "Default");
        Assert.Equal(defaultProfile, ChromeProfiles.Resolve(Chrome, "Chrome.2LGS6APA4UDF4BG3HSPLQ25VFU", [defaultProfile]));
        Assert.Null(ChromeProfiles.Resolve(Chrome, "Chrome.UserData.Profile1", [defaultProfile]));
    }

    [Theory]
    [InlineData(@"C:\Google\Chrome\Application\chrome.exe", "Chrome")]
    [InlineData(@"C:\Google\Chrome Beta\Application\chrome.exe", "ChromeBeta")]
    [InlineData(@"C:\Google\Chrome Dev\Application\chrome.exe", "ChromeDev")]
    [InlineData(@"C:\Google\Chrome SxS\Application\chrome.exe", "ChromeCanary")]
    public void PerUserBase32SuffixMatchesBothDefaultAndNamedProfilesAcrossChannels(string exe, string browserId)
    {
        // Actual unpadded base32(MD5("S-1-5-21-1-2-3-1001")), including letters beyond F.
        const string suffix = ".2LGS6APA4UDF4BG3HSPLQ25VFU";
        string initial = Path.Combine(ChromeProfiles.UserDataDirectory(exe), "Default");
        string named = Profile("Profile 1");
        Assert.Equal(initial, ChromeProfiles.Resolve(exe, browserId + suffix, [initial, named]));
        Assert.Equal(named, ChromeProfiles.Resolve(exe, browserId + suffix + "." + ChromeProfiles.ProfileId(named), [initial, named]));
        Assert.Null(ChromeProfiles.Resolve(exe, browserId + suffix[..^1], [initial, named]));
        Assert.Null(ChromeProfiles.Resolve(exe, browserId + suffix + ".webapp", [initial, named]));
    }

    [Fact]
    public void MatchingNeverUsesAnotherProfileOrLaunchTimingAsProfileEvidence()
    {
        var first = Item(Profile("Profile 1")); var second = Item(Profile("Profile 2"));
        var chosen = WorkspaceMatcher.Evaluate([first, second], Guid.Empty, Chrome, first.Title, "", [],
            first.Id, second.ChromeProfilePath);
        Assert.Same(second, chosen.Item);
        var unknown = WorkspaceMatcher.Evaluate([first, second], Guid.Empty, Chrome, first.Title, "", [], first.Id);
        Assert.True(unknown.IsAmbiguous); Assert.Null(unknown.Item);
        var duplicate = Item(first.ChromeProfilePath);
        Assert.True(WorkspaceMatcher.Evaluate([first, duplicate], Guid.Empty, Chrome, first.Title, "", [],
            chromeProfilePath: first.ChromeProfilePath).IsAmbiguous);
    }

    [Fact]
    public void ChangingAnOpenSlotsProfileLeavesItsOldWindowAloneAndBindsOnlyTheNewProfile()
    {
        string first = Profile("Profile 1"), second = Profile("Profile 2");
        var item = Item(first);
        using var rig = new WorkspaceHarness(new WorkspaceDocument { Items = [item] }); rig.Identity.Exe = Chrome;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 550, title: item.Title);
        rig.Identity.AppIds[(IntPtr)42] = "Chrome." + ChromeProfiles.ProfileId(first);
        rig.Workspace.Start(); Assert.True(rig.Workspace.IsOpen(item));
        Assert.True(rig.Workspace.SetChromeProfile(item.Id, second));
        Assert.False(rig.Workspace.IsOpen(item)); Assert.Empty(rig.Actions.Closed);
        rig.Workspace.Open(item.Id); Assert.Equal(item.Id, Assert.Single(rig.Launcher.Opened));
        rig.Api.AddWindow((IntPtr)43, 30, 40, 800, 550, title: item.Title);
        rig.Identity.AppIds[(IntPtr)43] = "Chrome." + ChromeProfiles.ProfileId(second);
        rig.Workspace.SaveNow();
        Assert.True(rig.Workspace.IsOpen(item)); Assert.Equal(second, item.ChromeProfilePath);
        Assert.Equal(item.Id, rig.Workspace.GetOverviewItemIds()[(IntPtr)43]);
        Assert.False(rig.Workspace.GetOverviewItemIds().ContainsKey((IntPtr)42));
    }

    [Fact]
    public void NamesUpdateBothCardRepresentationsImmediatelyAndExplicitDefaultChoiceIsPreserved()
    {
        var item = Item(Profile("Profile 1"));
        using var rig = new WorkspaceHarness(new WorkspaceDocument { Items = [item] }); rig.Identity.Exe = Chrome;
        rig.Workspace.Start();
        rig.Workspace.SetName(item.Id, "Work browser");
        Assert.Equal("Work browser", Assert.Single(rig.Cards.Created).Item.Title);
        Assert.Equal("Work browser", Assert.Single(rig.Workspace.GetPlaceholderTitles()).Value);
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 550, title: item.Title);
        rig.Identity.AppIds[(IntPtr)42] = "Chrome." + ChromeProfiles.ProfileId(item.ChromeProfilePath);
        rig.Workspace.SaveNow();
        rig.Workspace.Document.Items.Add(Item(item.ChromeProfilePath));
        Assert.True(rig.Workspace.SetChromeProfile(item.Id, ""));
        rig.Api.Windows[(IntPtr)42].Title = "Another tab";
        rig.Workspace.SaveNow();
        Assert.Equal("", item.ChromeProfilePath); Assert.Equal("Work browser", item.Name);
        Assert.Equal("Another tab", item.Title);
    }

    [Fact]
    public void ProfilesAndNamesRoundTripAndOldWorkspacesKeepTheirDefaults()
    {
        var item = Item(Profile("Profile 1")); item.Name = "Work"; item.CaptureChromeProfile = false;
        var store = new WorkspaceStore(Path.Combine(_root, "workspace.json"));
        store.Save(new WorkspaceDocument { Items = [item] });
        var loaded = Assert.Single(store.Load(out _).Items);
        Assert.Equal(item.ChromeProfilePath, loaded.ChromeProfilePath); Assert.Equal("Work", loaded.Name);
        Assert.False(loaded.CaptureChromeProfile);
        var json = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        json["Items"]![0]!.AsObject().Remove("ChromeProfilePath");
        json["Items"]![0]!.AsObject().Remove("CaptureChromeProfile");
        File.WriteAllText(store.FilePath, json.ToJsonString());
        loaded = Assert.Single(store.Load(out _).Items);
        Assert.Equal("", loaded.ChromeProfilePath); Assert.True(loaded.CaptureChromeProfile);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
