using System.Text.Json;

namespace CanvasDesktop.Ai;

internal static class BoundedHttp
{
    internal const int MaximumBytes = 1024 * 1024;

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("The service response was too large.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaximumBytes) throw new InvalidDataException("The service response was too large.");
            memory.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(memory.ToArray());
    }

    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, Uri uri, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new AiConnectionException($"Connection discovery returned HTTP {(int)response.StatusCode}. Try again later.");
        return await ReadJsonAsync(response, cancellation).ConfigureAwait(false);
    }
}
