using CanvasDesktop;

namespace CanvasDesktop.Tests;

// Self-updating programs replace their versioned install folder. The saved
// place must keep working across that update.
public class WorkspaceAppIdentityTests
{
    private const string OldDiscord = @"C:\Users\me\AppData\Local\Discord\app-1.0.9265\Discord.exe";
    private const string NewDiscord = @"C:\Users\me\AppData\Local\Discord\app-1.0.9267\Discord.exe";

    [Fact]
    public void ABuildFolderUpdateKeepsTheSameApplicationIdentity()
    {
        Assert.True(WorkspaceAppIdentity.Same(OldDiscord, NewDiscord));
        Assert.True(WorkspaceAppIdentity.Same(NewDiscord, OldDiscord));
    }

    [Fact]
    public void DifferentProgramsStayDifferentAcrossBuildFolders()
    {
        Assert.False(WorkspaceAppIdentity.Same(OldDiscord, @"C:\Users\me\AppData\Local\Other\app-1.0.9267\Discord.exe"));
        Assert.False(WorkspaceAppIdentity.Same(OldDiscord, @"C:\Users\me\AppData\Local\Discord\app-1.0.9267\Other.exe"));
        Assert.False(WorkspaceAppIdentity.Same(OldDiscord, @"C:\Users\me\AppData\Local\Discord\app-1.0.9267\tools\Discord.exe"));
    }

    [Fact]
    public void AFolderThatIsNotABuildNumberIsNotNormalized()
    {
        Assert.False(WorkspaceAppIdentity.Same(@"C:\Apps\app-abc\Tool.exe", @"C:\Apps\app-def\Tool.exe"));
        Assert.False(WorkspaceAppIdentity.Same(@"C:\Apps\app-1.0x\Tool.exe", @"C:\Apps\app-2.0x\Tool.exe"));
        Assert.False(WorkspaceAppIdentity.Same(@"C:\my-app-1.0\Tool.exe", @"C:\my-app-2.0\Tool.exe"));
    }

    [Fact]
    public void StorePackagesAndPlainPathsKeepTheirIdentity()
    {
        const string old = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1016.8622.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
        const string current = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1022.10000.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
        Assert.True(WorkspaceAppIdentity.Same(old, current));
        Assert.False(WorkspaceAppIdentity.Same(old, @"C:\Program Files\WindowsApps\OpenAI.Other_26.1022.10000.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"));
        Assert.False(WorkspaceAppIdentity.Same(@"C:\Apps\Signal.exe", @"C:\Apps\Discord.exe"));
    }

    [Fact]
    public void TheInstalledBuildIsFoundWhenTheSavedFolderIsGone()
    {
        string root = Path.Combine(Path.GetTempPath(), "canvas-build-" + Guid.NewGuid().ToString("N"));
        string old = Path.Combine(root, "app-1.0.1", "Program.exe");
        string current = Path.Combine(root, "app-1.0.2", "Program.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(old, "old");
        File.WriteAllText(current, "current");
        try
        {
            Assert.Equal(current, WorkspaceAppIdentity.CurrentBuild(old));
            Assert.Equal(current, WorkspaceAppIdentity.CurrentBuild(current));
            Assert.Null(WorkspaceAppIdentity.CurrentBuild(Path.Combine(root, "app-1.0.2", "Missing.exe")));
            Assert.Null(WorkspaceAppIdentity.CurrentBuild(@"C:\Apps\Signal.exe"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AStaleSavedBuildStillStartsTheInstalledProgram()
    {
        string root = Path.Combine(Path.GetTempPath(), "canvas-launch-" + Guid.NewGuid().ToString("N"));
        string old = Path.Combine(root, "app-1.0.1", "Program.exe");
        string current = Path.Combine(root, "app-1.0.2", "Program.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "current");
        try
        {
            var item = new WorkspaceItem { ExecutablePath = old };
            var info = WorkspaceIdentity.LaunchInfo(item);
            Assert.Equal(current, info.FileName);
            Assert.Equal(current, item.ExecutablePath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
