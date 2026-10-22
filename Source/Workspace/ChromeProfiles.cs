using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CanvasDesktop;

internal sealed record ChromeProfileChoice(string Path, string Name)
{
    public string Description => Path.Length == 0 ? Name : $"{Name} ({System.IO.Path.GetFileName(Path)})";
}

/// <summary>Read-only profile discovery and Chrome's Windows browser-window identity convention.</summary>
internal static class ChromeProfiles
{
    private static readonly Dictionary<string, (DateTime Until, IReadOnlyList<ChromeProfileChoice> Profiles)> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static bool Supports(string executable) => string.Equals(Path.GetFileName(executable), "chrome.exe", StringComparison.OrdinalIgnoreCase);

    private static string Channel(string executable)
    {
        string channel = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(executable))) ?? "";
        return channel is "Chrome Beta" or "Chrome Dev" or "Chrome SxS" ? channel : "Chrome";
    }

    internal static string UserDataDirectory(string executable) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", Channel(executable), "User Data");

    public static bool ValidPath(string? profile)
    {
        if (profile == null) return false;
        if (profile.Length == 0) return true;
        try
        {
            if (profile.Length > 32767 || !Path.IsPathFullyQualified(profile) || profile.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            string name = Path.GetFileName(profile);
            return name.Length > 0 && name is not "." and not ".." and not "Guest Profile" and not "System Profile"
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Path.GetDirectoryName(profile) is { Length: > 0 };
        }
        catch (ArgumentException) { return false; }
    }

    public static IReadOnlyList<ChromeProfileChoice> Read(string userDataDirectory)
    {
        var profiles = new Dictionary<string, ChromeProfileChoice>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = new FileStream(Path.Combine(userDataDirectory, "Local State"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= 4 * 1024 * 1024)
            {
                using var json = JsonDocument.Parse(stream);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object
                    && profile.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                    foreach (var entry in cache.EnumerateObject().Take(256))
                    {
                        if (entry.Name != Path.GetFileName(entry.Name)) continue;
                        string path = Path.Combine(userDataDirectory, entry.Name);
                        if (!ValidPath(path) || !File.Exists(Path.Combine(path, "Preferences"))) continue;
                        string name = entry.Value.ValueKind == JsonValueKind.Object
                            && entry.Value.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String
                                ? value.GetString() ?? entry.Name : entry.Name;
                        profiles[path] = new(path, name);
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        // Profiles remain selectable if Chrome is replacing/writing Local State.
        try
        {
            foreach (string path in Directory.EnumerateDirectories(userDataDirectory).Take(256))
                if (ValidPath(path) && File.Exists(Path.Combine(path, "Preferences")))
                    profiles.TryAdd(path, new(path, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return profiles.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static IReadOnlyList<ChromeProfileChoice> Get(string executable, bool refresh = false)
    {
        if (!Supports(executable)) return Array.Empty<ChromeProfileChoice>();
        string root = UserDataDirectory(executable);
        lock (Cache)
        {
            if (!refresh && Cache.TryGetValue(root, out var entry) && entry.Until > DateTime.UtcNow) return entry.Profiles;
            var profiles = Read(root);
            Cache[root] = (DateTime.UtcNow.AddMinutes(1), profiles);
            return profiles;
        }
    }

    internal static string ProfileId(string path) => new((Path.GetFileName(Path.GetDirectoryName(path)) + "." + Path.GetFileName(path))
        .Where(c => char.IsAsciiLetterOrDigit(c) || c == '.').ToArray());

    private static string BrowserId(string exe) => Channel(exe) switch
    { "Chrome Beta" => "ChromeBeta", "Chrome Dev" => "ChromeDev", "Chrome SxS" => "ChromeCanary", _ => "Chrome" };

    private static bool IsBrowserId(string appId, string browserId) => appId == browserId
        || (appId.StartsWith(browserId + ".", StringComparison.Ordinal) && appId.Length > browserId.Length + 1
            && appId.AsSpan(browserId.Length + 1).IndexOfAnyExcept("0123456789abcdefABCDEF".AsSpan()) < 0);

    internal static string? Resolve(string exe, string? appId, IEnumerable<string> candidates)
    {
        if (!Supports(exe) || string.IsNullOrEmpty(appId)) return null;
        string browserId = BrowserId(exe);
        var matches = candidates.Where(p => p.Length > 0 && ValidPath(p)).Distinct(StringComparer.OrdinalIgnoreCase).Where(path =>
        {
            bool initial = string.Equals(path, Path.Combine(UserDataDirectory(exe), "Default"), StringComparison.OrdinalIgnoreCase);
            if (initial) return IsBrowserId(appId, browserId);
            string suffix = "." + ProfileId(path);
            return appId.EndsWith(suffix, StringComparison.Ordinal) && IsBrowserId(appId[..^suffix.Length], browserId);
        }).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static void AddLaunchArguments(ProcessStartInfo launch, string profile)
    {
        if (!Supports(launch.FileName) || !ValidPath(profile) || profile.Length == 0
            || !File.Exists(Path.Combine(profile, "Preferences")))
            throw new IOException("The saved Chrome profile is unavailable. Choose an existing profile from the card's context menu.");
        launch.ArgumentList.Add("--user-data-dir=" + Path.GetDirectoryName(profile));
        launch.ArgumentList.Add("--profile-directory=" + Path.GetFileName(profile));
        launch.ArgumentList.Add("--new-window");
    }
}
