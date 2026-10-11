using System.Text.Json;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WorkspaceTargetTests
{
    [Theory]
    [InlineData(@"C:\Apps\Code.exe", @"C:\Work\Project")]
    [InlineData(@"C:\Apps\Code - Insiders.exe", @"C:\Work\Project")]
    [InlineData(@"C:\Apps\CODE.EXE", @"C:\Work\Project\")]
    [InlineData(@"C:\Apps\Code.exe", @"\\server\share\Project")]
    [InlineData(@"C:\Apps\Code.exe", @"C:\Work\Project.code-workspace")]
    [InlineData(@"C:\Apps\Code.exe", @"C:\Work\Project [one]")]
    public void FullPathMarkerIsStableWhenTheActiveFileCaptionChanges(string exe, string target)
    {
        var expected = Path.TrimEndingDirectorySeparator(target);
        Assert.Equal(expected, WorkspaceTargetIdentity.Read(exe, $"README.md — Project — Visual Studio Code [CanvasRoot:{target}]"));
        Assert.Equal(expected, WorkspaceTargetIdentity.Read(exe, $"other.cs — Project — Visual Studio Code [CanvasRoot:{target}]"));
    }

    [Theory]
    [InlineData(@"C:\Apps\Editor.exe", @"README — Project [CanvasRoot:C:\Work\Project]")]
    [InlineData(@"C:\Apps\Code.exe", "README — Project — Visual Studio Code")]
    [InlineData(@"C:\Apps\Code.exe", "Project [CanvasRoot:]")]
    [InlineData(@"C:\Apps\Code.exe", "Project [CanvasRoot:relative/path]")]
    [InlineData(@"C:\Apps\Code.exe", "Project [CanvasRoot:vscode-remote://ssh-remote/host/work]")]
    [InlineData(@"C:\Apps\Code.exe", @"Project [CanvasRoot:C:\Work\Project")]
    public void BasenamesUnsupportedAppsAndRemoteUrisAreNotGuessed(string exe, string title)
        => Assert.Equal("", WorkspaceTargetIdentity.Read(exe, title));

    [Fact]
    public void SameBasenameProjectsBindByFullPathNotCaptionOrEnumerationOrder()
    {
        var first = new WorkspaceItem { ExecutablePath = @"C:\Apps\Code.exe", Title = "README — Project",
            Target = @"C:\One\Project", Bounds = new WorldRect { X = 5000, Y = 100, W = 800, H = 600 } };
        var second = new WorkspaceItem { ExecutablePath = first.ExecutablePath, Title = first.Title,
            Target = @"C:\Two\Project", Bounds = new WorldRect { X = -5000, Y = 100, W = 800, H = 600 } };
        var document = new WorkspaceDocument { Items = new() { first, second } };
        using var rig = new WorkspaceHarness(document);
        rig.Identity.Exe = first.ExecutablePath;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: $"other.cs — Project [CanvasRoot:{second.Target}]");
        rig.Api.AddWindow((IntPtr)43, 30, 40, 800, 600, title: $"README — Project [CanvasRoot:{first.Target}]");
        rig.Workspace.Start();
        Assert.Equal(-5000, rig.Canvas.Windows[(IntPtr)42].X);
        Assert.Equal(5000, rig.Canvas.Windows[(IntPtr)43].X);
        Assert.All(rig.Workspace.Items, item => Assert.Equal("Open", item.State));
        Assert.Equal(2, rig.Workspace.Items.Count);
    }

    [Fact]
    public void CapturingCodeMarkerSavesTheFolderAsAnEditorTargetNotAnExplorerLaunch()
    {
        using var rig = new WorkspaceHarness();
        rig.Identity.Exe = @"C:\Apps\Code.exe";
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: @"Project [CanvasRoot:C:\Work\Project]");
        rig.Workspace.Start();
        var item = Assert.Single(rig.Workspace.Document.Items);
        Assert.Equal(@"C:\Work\Project", item.Target);
        Assert.False(item.IsFolder);
    }

    [Fact]
    public void CodeLaunchUsesNewWindowAndOneEscapedTargetArgument()
    {
        var directory = Path.Combine(Path.GetTempPath(), "canvas-code-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Never execute this file. LaunchInfo constructs an argument vector only.
            var exe = Path.Combine(directory, "Code.exe"); File.WriteAllText(exe, "test");
            var target = Path.Combine(directory, "Project & spaces"); Directory.CreateDirectory(target);
            var info = WorkspaceIdentity.LaunchInfo(new WorkspaceItem { ExecutablePath = exe, Target = target });
            Assert.Equal(new[] { "--new-window", target }, info.ArgumentList);
            Assert.Empty(info.Arguments); Assert.False(info.UseShellExecute);
            Assert.Throws<IOException>(() => WorkspaceIdentity.LaunchInfo(new WorkspaceItem
                { ExecutablePath = exe, Target = "--user-data-dir=untrusted" }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ExactCaptionOutranksWeakSingleAppFallbackInTheSameBatch()
    {
        var item = new WorkspaceItem { Title = "First", ExecutablePath = @"C:\Apps\Editor.exe",
            Bounds = new WorldRect { X = 5000, Y = 100, W = 800, H = 600 } };
        using var rig = new WorkspaceHarness(new() { Items = new() { item } });
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Unrelated");
        rig.Api.AddWindow((IntPtr)43, 30, 40, 800, 600, title: "First");
        rig.Workspace.Start();
        Assert.Equal(5000, rig.Canvas.Windows[(IntPtr)43].X);
        Assert.Equal(10, rig.Canvas.Windows[(IntPtr)42].X);
        Assert.Equal(2, rig.Workspace.Items.Count);
    }

    [Fact]
    public void NamesRoundTripAndOldV1DocumentsWithoutNamesStillLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "canvas-name-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workspace.json");
            var store = new WorkspaceStore(path);
            var item = new WorkspaceItem { Name = "VS Code — Canvas project", Title = "Changing caption",
                ExecutablePath = @"C:\Apps\Code.exe", Target = @"C:\Work\Canvas",
                Bounds = new WorldRect { X = 100, Y = 200, W = 800, H = 600 } };
            store.Save(new() { Items = new() { item } });
            Assert.Equal(item.Name, Assert.Single(store.Load(out _).Items).Name);
            var json = JsonSerializer.SerializeToNode(store.Load(out _), new JsonSerializerOptions { IncludeFields = true })!;
            json["Items"]![0]!.AsObject().Remove("Name");
            File.WriteAllText(path, json.ToJsonString());
            Assert.Equal("", Assert.Single(store.Load(out var warning).Items).Name);
            Assert.Null(warning);
            item.Name = new string('x', 201);
            Assert.Throws<InvalidDataException>(() => store.Save(new() { Items = new() { item } }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
