using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class PlaceholderPathDisplayTests
{
    private static WorkspaceItemView Item(string executable, string target, bool folder = false)
        => new(Guid.NewGuid(), "Notes", executable, target, false, new WorldRect { W = 800, H = 550 }, "Placeholder", IsFolder: folder);

    [Fact]
    public void FileTargetLeadsWithItsLeafAndKeepsTheFullPath()
    {
        var path = PlaceholderPathDisplay.For(Item(@"C:\Apps\Editor.exe", @"C:\Work\notes.txt"));
        Assert.Equal("notes.txt", path.Leaf);
        Assert.Equal(@"C:\Work", path.Parent);
        Assert.Equal("TXT FILE", path.Kind);
        Assert.Equal(@"C:\Work\notes.txt", path.Full);
    }

    [Fact]
    public void FolderTargetTrimsTrailingSeparatorsAndKeepsItsParent()
    {
        var path = PlaceholderPathDisplay.For(Item(@"C:\Windows\explorer.exe", @"C:\Projects\Canvas\", folder: true));
        Assert.Equal("Canvas", path.Leaf);
        Assert.Equal(@"C:\Projects", path.Parent);
        Assert.Equal("FOLDER", path.Kind);
    }

    [Fact]
    public void ApplicationWithoutATargetShowsTheExecutableLeaf()
    {
        var path = PlaceholderPathDisplay.For(Item(@"C:\Windows\System32\notepad.exe", ""));
        Assert.Equal("notepad.exe", path.Leaf);
        Assert.Equal(@"C:\Windows\System32", path.Parent);
        Assert.Equal("APPLICATION", path.Kind);
    }
}
