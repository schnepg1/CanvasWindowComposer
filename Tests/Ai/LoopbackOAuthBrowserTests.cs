using System.Net;
using System.Net.Sockets;
using CanvasDesktop.Ai;

namespace CanvasDesktop.Tests;

public class LoopbackOAuthBrowserTests
{
    [Fact]
    public async Task ListenerIsBoundBeforeBrowserAndReturnsOnlyTheExactCallbackPath()
    {
        using var http = new HttpClient();
        Task? requests = null;
        Uri? callback = null;
        var browser = new LoopbackOAuthBrowser(_ =>
        {
            requests = Task.Run(async () =>
            {
                using var bad = await http.GetAsync(new Uri(callback!, "/wrong?code=x"));
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
                using var good = await http.GetAsync(new Uri(callback!, "?code=ok&state=state"));
                Assert.Equal(HttpStatusCode.OK, good.StatusCode);
                Assert.DoesNotContain("code=", await good.Content.ReadAsStringAsync());
                Assert.True(good.Headers.CacheControl!.NoStore);
            });
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await browser.AuthorizeAsync(uri => { callback = uri; return new("https://auth.openai.com/"); }, deadline.Token);
        await requests!;
        Assert.Equal("127.0.0.1", result.Host); Assert.Equal("/auth/callback", result.AbsolutePath); Assert.Equal("?code=ok&state=state", result.Query);
        using var rebound = new TcpListener(IPAddress.Loopback, result.Port); rebound.Start();
    }

    [Fact]
    public async Task CancellationReleasesTheCallbackPort()
    {
        Uri? callback = null; var browser = new LoopbackOAuthBrowser(_ => { });
        using var cancel = new CancellationTokenSource();
        var task = browser.AuthorizeAsync(uri => { callback = uri; return new("https://auth.openai.com/"); }, cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        using var rebound = new TcpListener(IPAddress.Loopback, callback!.Port); rebound.Start();
    }
}
