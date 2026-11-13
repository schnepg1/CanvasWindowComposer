using System;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

public class WebDestinationParserTests
{
    private static WorkspaceItemView CreateBrowserItem(string title, string exe, string url = "", string target = "")
    {
        return new WorkspaceItemView(
            Id: Guid.NewGuid(),
            Title: title,
            ExecutablePath: exe,
            Target: target,
            OpenOnStartup: false,
            Bounds: new WorldRect { W = 400, H = 300 },
            State: "Normal",
            WebResource: string.IsNullOrEmpty(url) ? null : new WebResource(url));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=123", "youtube.com")]
    [InlineData("https://github.com/schnepg1/CanvasWindowComposer", "github.com")]
    [InlineData("http://localhost:3000/app", "localhost")]
    [InlineData("https://docs.google.com/document/d/xyz", "docs.google.com")]
    public void ExtractsDomainFromValidUrl(string inputUrl, string expectedDomain)
    {
        bool success = WebDestinationParser.TryExtractDomainFromUrl(inputUrl, out var domain);
        Assert.True(success);
        Assert.Equal(expectedDomain, domain);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    [InlineData("")]
    public void RejectsInvalidUrl(string inputUrl)
    {
        bool success = WebDestinationParser.TryExtractDomainFromUrl(inputUrl, out _);
        Assert.False(success);
    }

    [Theory]
    [InlineData("Chillhop Radio - YouTube - Google Chrome", "youtube.com", "YouTube")]
    [InlineData("schnepg1/CanvasWindowComposer: PR #42 - GitHub - Google Chrome", "github.com", "GitHub")]
    [InlineData("Design System · Figma - Google Chrome", "figma.com", "Figma")]
    [InlineData("Sprint Backlog | Jira - Personal - Microsoft Edge", "atlassian.net", "Jira")]
    [InlineData("Weekly Notes — Notion — Mozilla Firefox", "notion.so", "Notion")]
    public void InfersWebDestinationFromWindowTitles(string windowTitle, string expectedDomain, string expectedBrand)
    {
        bool success = WebDestinationParser.TryInferFromTitle(windowTitle, out var domain, out var brand);
        Assert.True(success);
        Assert.Equal(expectedDomain, domain);
        Assert.Equal(expectedBrand, brand);
    }

    [Fact]
    public void WebResourceTakesPrecedenceOverTitle()
    {
        var item = CreateBrowserItem(
            title: "Some Random Title - Google Chrome",
            exe: @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            url: "https://www.youtube.com/watch?v=test");

        bool success = WebDestinationParser.TryGetDestination(item, out var domain, out var brand);
        Assert.True(success);
        Assert.Equal("youtube.com", domain);
        Assert.Equal("YouTube", brand);
    }

    [Fact]
    public void BrowserCacheKeysDifferentiateWebDestinations()
    {
        var chromeYoutube = CreateBrowserItem("YouTube - Google Chrome", @"C:\Chrome\chrome.exe");
        var chromeGithub = CreateBrowserItem("GitHub - Google Chrome", @"C:\Chrome\chrome.exe");
        var edgeYoutube = CreateBrowserItem("YouTube - Microsoft Edge", @"C:\Edge\msedge.exe");

        string key1 = WebDestinationParser.GetCacheKey(chromeYoutube);
        string key2 = WebDestinationParser.GetCacheKey(chromeGithub);
        string key3 = WebDestinationParser.GetCacheKey(edgeYoutube);

        Assert.Equal("browser:chrome.exe|web:youtube.com", key1);
        Assert.Equal("browser:chrome.exe|web:github.com", key2);
        Assert.Equal("browser:msedge.exe|web:youtube.com", key3);

        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1, key3);
    }

    [Fact]
    public void NonBrowserItemUsesExecutableCacheKey()
    {
        var codeItem = CreateBrowserItem("Project - VS Code", @"C:\Apps\Code.exe");
        string key = WebDestinationParser.GetCacheKey(codeItem);
        Assert.Equal(@"c:\apps\code.exe", key);
    }
}
