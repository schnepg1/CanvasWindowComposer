using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace CanvasDesktop.Tests;

public class LaunchSecurityTests
{
    private static WorkspaceItem App(string target = "") => new()
    {
        ExecutablePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"), Target = target
    };

    [Fact]
    public void ElevatedHostCannotReachTheProcessStarter()
    {
        bool started = false;
        var launcher = new WorkspaceLauncher(() => true, _ => started = true);
        Assert.Throws<InvalidOperationException>(() => launcher.Open(App()));
        Assert.False(started);
    }

    [Fact]
    public void NormalHostUsesExplicitExecutableWithoutShellOrRunAs()
    {
        ProcessStartInfo? launch = null;
        var launcher = new WorkspaceLauncher(() => false, info => launch = info);
        launcher.Open(App());
        Assert.NotNull(launch);
        Assert.False(launch.UseShellExecute);
        Assert.Empty(launch.Verb);
        Assert.Empty(launch.Arguments);
        Assert.Empty(launch.ArgumentList);
        Assert.Equal(App().ExecutablePath, launch.FileName);
        Assert.Equal(Environment.SystemDirectory, launch.WorkingDirectory);
    }

    [Theory]
    [InlineData("--gpu-launcher=cmd.exe")]
    [InlineData("/c calc.exe")]
    [InlineData("https://example.com")]
    [InlineData("relative.txt")]
    [InlineData("shell:startup")]
    public void SavedOptionsUrlsAndRelativeTargetsAreRejected(string target)
    {
        bool started = false;
        var launcher = new WorkspaceLauncher(() => false, _ => started = true);
        Assert.Throws<IOException>(() => launcher.Open(App(target)));
        Assert.False(started);
    }

    [Fact]
    public void ExistingFileWithSpacesAndMetacharactersIsOneArgument()
    {
        string dir = Path.Combine(Path.GetTempPath(), "canvas-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "notes & spaces.txt");
            File.WriteAllText(file, "test");
            var info = WorkspaceIdentity.LaunchInfo(App(file));
            Assert.Equal(file, Assert.Single(info.ArgumentList));
            Assert.Empty(info.Arguments);
            var script = App(file); script.ExecutablePath = file;
            Assert.Throws<IOException>(() => WorkspaceIdentity.LaunchInfo(script));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FolderLaunchUsesExplorerWithEscapedArguments_NotShellCommands()
    {
        var item = new WorkspaceItem { IsFolder = true, Target = Path.GetTempPath() };
        var info = WorkspaceIdentity.LaunchInfo(item);
        Assert.False(info.UseShellExecute);
        Assert.Equal("explorer.exe", Path.GetFileName(info.FileName));
        Assert.Equal(new[] { "/n,", item.Target }, info.ArgumentList);
        item.Target = "::not-a-guid";
        Assert.Throws<IOException>(() => WorkspaceIdentity.LaunchInfo(item));
        item.Target = "::{645FF040-5081-101B-9F08-00AA002F954E}";
        Assert.Equal(item.Target, WorkspaceIdentity.LaunchInfo(item).ArgumentList[1]);
    }

    [Fact]
    public void StartupQuotesExecutableWithoutAddingAnInterpreter()
    {
        Assert.Equal("\"C:\\Program Files\\Canvas\\CanvasDesktop.exe\"",
            WorkspaceStartup.StartupCommand(@"C:\Program Files\Canvas\CanvasDesktop.exe"));
        Assert.Throws<ArgumentException>(() => WorkspaceStartup.StartupCommand("CanvasDesktop.exe"));
        Assert.Throws<ArgumentException>(() => WorkspaceStartup.StartupCommand("C:\\Canvas\\bad\".exe"));
    }

    private static string TaskXml(string command, string user = "DOMAIN\\User", string arguments = "", string enabled = "true") => $"""
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Principals><Principal><UserId>{System.Security.SecurityElement.Escape(user)}</UserId></Principal></Principals>
          <Settings><Enabled>{enabled}</Enabled></Settings>
          <Actions><Exec><Command>{System.Security.SecurityElement.Escape(command)}</Command><Arguments>{System.Security.SecurityElement.Escape(arguments)}</Arguments></Exec></Actions>
        </Task>
        """;

    [Fact]
    public void MigrationRecognizesOnlyExactExecutableAndCurrentUser_AndPreservesDisabledState()
    {
        const string exe = @"G:\Canvas\CanvasDesktop.exe";
        Assert.True(WorkspaceStartup.IsOwnedLegacyTask(TaskXml('"' + exe + '"'), exe, @"DOMAIN\User", "S-1-5-21-1", out bool enabled));
        Assert.True(enabled);
        Assert.True(WorkspaceStartup.IsOwnedLegacyTask(TaskXml(exe, "S-1-5-21-1", enabled: "false"), exe, @"DOMAIN\User", "S-1-5-21-1", out enabled));
        Assert.False(enabled);
        Assert.False(WorkspaceStartup.IsOwnedLegacyTask(TaskXml(exe, "OTHER\\User"), exe, @"DOMAIN\User", "S-1-5-21-1", out _));
        Assert.False(WorkspaceStartup.IsOwnedLegacyTask(TaskXml(@"G:\Other\CanvasDesktop.exe"), exe, @"DOMAIN\User", "S-1-5-21-1", out _));
        Assert.False(WorkspaceStartup.IsOwnedLegacyTask(TaskXml(exe, arguments: "--other-command"), exe, @"DOMAIN\User", "S-1-5-21-1", out _));
        Assert.False(WorkspaceStartup.IsOwnedLegacyTask(TaskXml("CanvasDesktop.exe"), exe, @"DOMAIN\User", "S-1-5-21-1", out _));
    }
}
