using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CanvasDesktop;

/// <summary>
/// Deterministic extraction of web destination domains and brand identities
/// from URLs, targets, and browser window captions.
/// </summary>
internal static class WebDestinationParser
{
    private static readonly string[] BrowserSuffixes =
    [
        " - Google Chrome",
        " - Microsoft Edge",
        " - Microsoft\u200b Edge", // Edge sometimes inserts zero-width space
        " — Mozilla Firefox",
        " - Mozilla Firefox",
        " - Brave",
        " - Chromium",
        " - Vivaldi",
        " - Opera"
    ];

    private static readonly Dictionary<string, string> KnownBrandDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        { "YouTube", "youtube.com" },
        { "GitHub", "github.com" },
        { "GitLab", "gitlab.com" },
        { "Figma", "figma.com" },
        { "Linear", "linear.app" },
        { "Jira", "atlassian.net" },
        { "Confluence", "atlassian.net" },
        { "Trello", "trello.com" },
        { "Notion", "notion.so" },
        { "Slack", "slack.com" },
        { "Discord", "discord.com" },
        { "Reddit", "reddit.com" },
        { "Twitter", "x.com" },
        { "X", "x.com" },
        { "Twitch", "twitch.tv" },
        { "Netflix", "netflix.com" },
        { "Spotify", "spotify.com" },
        { "ChatGPT", "chatgpt.com" },
        { "OpenAI", "openai.com" },
        { "Claude", "claude.ai" },
        { "Google Docs", "docs.google.com" },
        { "Google Sheets", "docs.google.com" },
        { "Google Slides", "docs.google.com" },
        { "Google Drive", "drive.google.com" },
        { "Google Mail", "mail.google.com" },
        { "Gmail", "mail.google.com" },
        { "Google Calendar", "calendar.google.com" },
        { "Google Maps", "maps.google.com" },
        { "Google", "google.com" },
        { "Cloudflare", "cloudflare.com" },
        { "AWS", "aws.amazon.com" },
        { "Azure", "azure.microsoft.com" },
        { "Wikipedia", "wikipedia.org" },
        { "Stack Overflow", "stackoverflow.com" }
    };

    /// <summary>
    /// Checks if the item belongs to a supported browser and has a distinguishable web destination.
    /// </summary>
    public static bool TryGetDestination(WorkspaceItemView item, out string destinationDomain, out string brandName)
    {
        destinationDomain = "";
        brandName = "";

        if (!BrowserSupport.Supports(item.ExecutablePath))
            return false;

        // 1. Direct configured URL (WebResource or Target URL)
        string candidateUrl = item.WebResource?.Url ?? "";
        if (string.IsNullOrWhiteSpace(candidateUrl) && BrowserSupport.ValidUrl(item.Target))
        {
            candidateUrl = item.Target;
        }

        if (TryExtractDomainFromUrl(candidateUrl, out destinationDomain))
        {
            brandName = GetBrandFromDomain(destinationDomain);
            return true;
        }

        // 2. Infer from window title
        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            if (TryInferFromTitle(item.Title, out destinationDomain, out brandName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts a normalized domain name from an HTTP/HTTPS URL.
    /// </summary>
    public static bool TryExtractDomainFromUrl(string? url, out string domain)
    {
        domain = "";
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            string host = uri.Host.Trim().ToLowerInvariant();
            if (host.StartsWith("www.")) host = host[4..];
            domain = host;
            return domain.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// Cleans browser suffixes and inspects window title segments to infer the web app/brand.
    /// </summary>
    public static bool TryInferFromTitle(string title, out string domain, out string brand)
    {
        domain = "";
        brand = "";
        if (string.IsNullOrWhiteSpace(title)) return false;

        string cleaned = CleanBrowserSuffix(title);
        if (string.IsNullOrWhiteSpace(cleaned)) return false;

        // Check if the title explicitly mentions a known brand
        // Example: "Lo-Fi Beats - YouTube" -> segments: ["Lo-Fi Beats", "YouTube"]
        var segments = cleaned.Split(new[] { " - ", " – ", " — ", " · ", " | " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Check from right to left (service brand is usually the rightmost segment)
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            string seg = segments[i];
            if (KnownBrandDomains.TryGetValue(seg, out var matchedDomain))
            {
                domain = matchedDomain;
                brand = seg;
                return true;
            }
        }

        // Fallback: check if any segment ends with a known brand
        foreach (var kvp in KnownBrandDomains)
        {
            if (cleaned.EndsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                domain = kvp.Value;
                brand = kvp.Key;
                return true;
            }
        }

        // If title itself looks like a domain name (e.g. "github.com")
        if (cleaned.Contains('.') && !cleaned.Contains(' ') && TryExtractDomainFromUrl("https://" + cleaned, out domain))
        {
            brand = GetBrandFromDomain(domain);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Strips trailing browser markers and profile indicators (e.g. "- Personal - Microsoft Edge").
    /// </summary>
    public static string CleanBrowserSuffix(string title)
    {
        string t = title.Trim();

        foreach (var suffix in BrowserSuffixes)
        {
            int idx = t.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                t = t[..idx].Trim();
                break;
            }
        }

        // Handle Edge profile inserts like "- Personal" or "- Work" at end of stripped title
        if (t.EndsWith(" - Personal", StringComparison.OrdinalIgnoreCase))
            t = t[..^11].Trim();
        else if (t.EndsWith(" - Work", StringComparison.OrdinalIgnoreCase))
            t = t[..^7].Trim();

        return t;
    }

    /// <summary>
    /// Computes a user-friendly brand name from a domain name.
    /// </summary>
    public static string GetBrandFromDomain(string domain)
    {
        string d = domain.ToLowerInvariant();
        if (d.StartsWith("www.")) d = d[4..];

        foreach (var kvp in KnownBrandDomains)
        {
            if (d == kvp.Value || d.EndsWith("." + kvp.Value))
                return kvp.Key;
        }

        int dot = d.IndexOf('.');
        if (dot > 0)
        {
            string name = d[..dot];
            return char.ToUpperInvariant(name[0]) + name[1..];
        }

        return domain;
    }

    /// <summary>
    /// Computes a granular cache key for browser placeholder artwork.
    /// </summary>
    public static string GetCacheKey(WorkspaceItemView item)
    {
        if (BrowserSupport.Supports(item.ExecutablePath) && TryGetDestination(item, out var domain, out _))
        {
            string browser = Path.GetFileName(item.ExecutablePath).ToLowerInvariant();
            return $"browser:{browser}|web:{domain}";
        }

        return string.IsNullOrEmpty(item.ExecutablePath)
            ? "empty"
            : item.ExecutablePath.Trim().ToLowerInvariant();
    }
}
