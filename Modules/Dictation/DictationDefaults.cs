namespace CanvasDesktop.Dictation;

/// <summary>Default configuration values, URL normalization, and user-facing messages for local dictation.</summary>
public static class DictationDefaults
{
    public const string DefaultBaseUrl = "http://127.0.0.1:8765";

    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan StreamConnectTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AbortStopTimeout = TimeSpan.FromSeconds(3);

    public const string ModelLoadingMessage = "Transcription model is still loading, try again in a moment.";
    public const string AlreadyRecordingMessage = "Another dictation recording is already in progress.";
    public const string StreamDisconnectedMessage = "Live transcription stream disconnected. Click Stop to finalize.";

    public static bool TryNormalizeBaseUrl(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host)
            || uri.HostNameType == UriHostNameType.Unknown
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        string authority = uri.GetLeftPart(UriPartial.Authority);
        string path = uri.AbsolutePath.TrimEnd('/');
        normalized = path.Length == 0 ? authority : authority + path;
        return true;
    }

    public static string NormalizeOrDefault(string? raw)
        => TryNormalizeBaseUrl(raw, out var normalized) ? normalized : DefaultBaseUrl;

    public static string FormatDisplayEndpoint(string? baseUrl)
    {
        string normalized = NormalizeOrDefault(baseUrl);
        var uri = new Uri(normalized, UriKind.Absolute);
        string path = uri.AbsolutePath.TrimEnd('/');
        return path.Length == 0 ? uri.Authority : uri.Authority + path;
    }

    public static string UnreachableMessage(string? baseUrl)
        => $"Local transcription server is not running on {FormatDisplayEndpoint(baseUrl)}";
}
