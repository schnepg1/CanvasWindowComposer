using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace CanvasDesktop;

internal static class AiServices
{
    // Redirects must never forward API keys or OAuth credentials to another origin.
    internal static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMinutes(3) };
    internal static readonly Uri UsageSettings = new("https://chatgpt.com/settings/usage");
    private static readonly Lazy<IChatGptAccounts> SavedAccounts = new(() => new ChatGptAccounts(Http,
        new WindowsChatGptStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CanvasWindowComposer", "AI", "chatgpt.dat")), new LoopbackOAuthBrowser(OpenBrowser)));
    internal static IChatGptAccounts Accounts => SavedAccounts.Value;
    internal static void OpenBrowser(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}
