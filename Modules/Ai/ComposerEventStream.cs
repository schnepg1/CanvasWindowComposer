using System.Text;
using System.Text.Json;

namespace CanvasDesktop.Ai;

internal static class ComposerEventStream
{
    public static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, IProgress<string>? progress, CancellationToken cancellation)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            if (mediaType != null && (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)))
            {
                var doc = await BoundedHttp.ReadJsonAsync(response, cancellation).ConfigureAwait(false);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    string? type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                    if (type is "error" or "response.failed" or "response.incomplete" || root.TryGetProperty("error", out _) || (root.TryGetProperty("status", out var st) && st.GetString() == "failed"))
                    {
                        using (doc)
                        {
                            throw ChatGptErrors.Failure(root, response, incomplete: type == "response.incomplete");
                        }
                    }
                    if (type == "response.completed" && root.TryGetProperty("response", out var respElement))
                    {
                        var inner = JsonDocument.Parse(respElement.GetRawText());
                        doc.Dispose();
                        return inner;
                    }
                }
                return doc;
            }
            throw new InvalidDataException("ChatGPT did not return an event stream.");
        }
        using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var limited = new LimitedStream(stream);
        using var reader = new StreamReader(limited, new UTF8Encoding(false, true));
        var data = new StringBuilder();
        int characters = 0;
        long nextProgress = 0;
        string? eventType = null;
        while (await reader.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
        {
            if (line.Length != 0)
            {
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventType = line.AsSpan(6).Trim().ToString();
                    continue;
                }
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                data.Append(line.AsSpan(5).TrimStart()).Append('\n');
                if (data.Length > BoundedHttp.MaximumBytes) throw new InvalidDataException("The AI event was too large.");
                continue;
            }
            if (data.Length == 0)
            {
                eventType = null;
                continue;
            }
            string text = data.ToString(); data.Clear();
            string? currentEventType = eventType; eventType = null;
            if (text.Trim() == "[DONE]") break;
            using var item = JsonDocument.Parse(text);
            var root = item.RootElement;
            string? type = (root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null) ?? currentEventType;
            if (type == "response.output_text.delta")
            {
                characters += root.TryGetProperty("delta", out var deltaProp) ? deltaProp.GetString()?.Length ?? 0 : 0;
                if (characters > BoundedHttp.MaximumBytes) throw new InvalidDataException("The AI proposal was too large.");
                if (Environment.TickCount64 >= nextProgress)
                {
                    progress?.Report($"Receiving proposal… {characters:N0} characters");
                    nextProgress = Environment.TickCount64 + 150;
                }
            }
            if (type == "response.refusal.delta") throw new InvalidDataException("The model declined this request. Try rephrasing it.");
            if (type is "error" or "response.failed" or "response.incomplete" || root.TryGetProperty("error", out _) || (root.TryGetProperty("status", out var st) && st.GetString() == "failed"))
                throw ChatGptErrors.Failure(root, response, incomplete: type == "response.incomplete");
            if (type == "response.completed")
            {
                if (root.TryGetProperty("response", out var resp))
                    return JsonDocument.Parse(resp.GetRawText());
                return JsonDocument.Parse(text);
            }
        }
        throw new InvalidDataException("The response stream ended before completion. Nothing was applied.");
    }

    // Bound the wire stream as well as each event; ReadLineAsync cannot allocate an unbounded line.
    private sealed class LimitedStream(Stream inner) : Stream
    {
        private long _read;
        private int Count(int count)
        {
            _read += count;
            if (_read > 8 * BoundedHttp.MaximumBytes) throw new InvalidDataException("The AI stream was too large.");
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
