using CanvasDesktop.Dictation;

namespace CanvasDesktop.Dictation.Tests;

public class DictationDefaultsTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8765", "http://127.0.0.1:8765")]
    [InlineData("http://127.0.0.1:8765/", "http://127.0.0.1:8765")]
    [InlineData("  http://localhost:9000///  ", "http://localhost:9000")]
    [InlineData("https://127.0.0.1:8443/api/", "https://127.0.0.1:8443/api")]
    [InlineData("http://[::1]:8765/", "http://[::1]:8765")]
    public void TryNormalizeBaseUrlAcceptsValidHttpAndHttpsUrls(string input, string expected)
    {
        Assert.True(DictationDefaults.TryNormalizeBaseUrl(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected, DictationDefaults.NormalizeOrDefault(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("127.0.0.1:8765")]
    [InlineData("ftp://127.0.0.1:8765")]
    [InlineData("file:///C:/temp")]
    [InlineData("http://user:pass@127.0.0.1:8765")]
    [InlineData("http://127.0.0.1:8765?foo=bar")]
    [InlineData("http://127.0.0.1:8765#frag")]
    [InlineData("not a url")]
    public void TryNormalizeBaseUrlRejectsInvalidUrlsAndFallsBackToDefault(string? input)
    {
        Assert.False(DictationDefaults.TryNormalizeBaseUrl(input, out _));
        Assert.Equal(DictationDefaults.DefaultBaseUrl, DictationDefaults.NormalizeOrDefault(input));
    }

    [Fact]
    public void UnreachableMessageIncludesFormattedEndpoint()
    {
        Assert.Equal(
            "Local transcription server is not running on 127.0.0.1:8765",
            DictationDefaults.UnreachableMessage(null));
        Assert.Equal(
            "Local transcription server is not running on localhost:9123",
            DictationDefaults.UnreachableMessage("http://localhost:9123/"));
    }
}
