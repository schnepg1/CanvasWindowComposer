using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace CanvasDesktop;

internal sealed record BrowserInstallation(string ExecutablePath, string Name)
{
    public string Description => $"{Name} — {ExecutablePath}";
}

// Browser-neutral saved intent. The executable, name and geometry belong to the
// containing workspace item; profile paths are always local to that browser.
internal sealed record WebResource(string Url, string ProfilePath = "");

internal static class BrowserSupport
{
    public static string? Name(string executable) => Path.GetFileName(executable).ToLowerInvariant() switch
    {
        "chrome.exe" => "Chrome", "msedge.exe" => "Edge", "firefox.exe" => "Firefox", _ => null
    };
    public static bool Supports(string executable) => Name(executable) != null;
    public static bool IsFirefox(string executable) => Name(executable) == "Firefox";

    public static bool ValidUrl(string? url) => url is { Length: > 0 and <= 16384 }
        && !url.Any(char.IsControl) && !url.Any(char.IsWhiteSpace)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.Host.Length > 0 && uri.UserInfo.Length == 0;

    public static bool ValidResource(string executable, WebResource? resource) => resource == null
        || (Supports(executable) && ValidUrl(resource.Url) && ChromeProfiles.ValidPath(resource.ProfilePath));

    internal static string UserDataDirectory(string executable)
    {
        if (ChromeProfiles.Supports(executable)) return ChromeProfiles.UserDataDirectory(executable);
        if (IsFirefox(executable)) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");
        string channel = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(executable))) ?? "";
        if (channel is not ("Edge Beta" or "Edge Dev" or "Edge SxS")) channel = "Edge";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", channel, "User Data");
    }

    public static IReadOnlyList<ChromeProfileChoice> Profiles(string executable)
        => !Supports(executable) ? Array.Empty<ChromeProfileChoice>()
            : IsFirefox(executable) ? ReadFirefoxProfiles(UserDataDirectory(executable))
            : ChromeProfiles.Read(UserDataDirectory(executable));

    internal static IReadOnlyList<ChromeProfileChoice> ReadFirefoxProfiles(string root)
    {
        var profiles = new List<ChromeProfileChoice>();
        try
        {
            using var stream = new FileStream(Path.Combine(root, "profiles.ini"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) return profiles;
            using var reader = new StreamReader(stream);
            string section = "", name = "", path = "";
            bool relative = true;
            void Add()
            {
                if (!section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) || path.Length == 0 || profiles.Count >= 256) return;
                string full = relative ? Path.GetFullPath(Path.Combine(root, path)) : path;
                if (ChromeProfiles.ValidPath(full) && Directory.Exists(full))
                    profiles.Add(new(full, name.Length > 0 ? name : Path.GetFileName(full)));
            }
            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                { Add(); section = line[1..^1]; name = path = ""; relative = true; continue; }
                int split = line.IndexOf('=');
                if (split < 0) continue;
                string key = line[..split].Trim(), value = line[(split + 1)..].Trim();
                if (key == "Name") name = value;
                else if (key == "Path") path = value;
                else if (key == "IsRelative") relative = value == "1";
            }
            Add();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return profiles.DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Name).ToArray();
    }

    public static bool ProfileExists(string executable, string path) => ChromeProfiles.ValidPath(path) && path.Length > 0
        && File.Exists(Path.Combine(path, IsFirefox(executable) ? "prefs.js" : "Preferences"));

    public static void AddLaunchArguments(ProcessStartInfo launch, WebResource resource)
    {
        if (!ValidResource(launch.FileName, resource)) throw new IOException("Choose a supported browser and an HTTP or HTTPS website address.");
        if (resource.ProfilePath.Length > 0)
        {
            if (!ProfileExists(launch.FileName, resource.ProfilePath))
                throw new IOException("The saved browser profile is unavailable. Choose an existing profile in Web resource settings.");
            if (IsFirefox(launch.FileName))
            { launch.ArgumentList.Add("--profile"); launch.ArgumentList.Add(resource.ProfilePath); }
            else
            {
                launch.ArgumentList.Add("--user-data-dir=" + Path.GetDirectoryName(resource.ProfilePath));
                launch.ArgumentList.Add("--profile-directory=" + Path.GetFileName(resource.ProfilePath));
            }
        }
        launch.ArgumentList.Add("--new-window");
        launch.ArgumentList.Add(resource.Url);
    }

    public static IReadOnlyList<BrowserInstallation> Installed()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string exe in new[] { "chrome.exe", "msedge.exe", "firefox.exe" })
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    try
                    {
                        using var registry = RegistryKey.OpenBaseKey(hive, view);
                        using var key = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                        if (key?.GetValue("") is string value) paths.Add(value.Trim('"'));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        {
            paths.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
            paths.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            paths.Add(Path.Combine(root, "Mozilla Firefox", "firefox.exe"));
        }
        return paths.Where(p => Supports(p) && File.Exists(p)).OrderBy(p => Name(p)).ThenBy(p => p)
            .Select(p => new BrowserInstallation(p, Name(p)!)).ToArray();
    }
}
