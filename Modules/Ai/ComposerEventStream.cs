using System.Text;
using System.Text.Json;

namespace CanvasDesktop.Ai;

internal static class ComposerEventStream
{
    public static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, IProgress<string>? progress, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new InvalidDataException("ChatGPT did not return an event stream.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var limited = new LimitedStream(stream);
        using var reader = new StreamReader(limited, new UTF8Encoding(false, true));
        var data = new StringBuilder();
        int characters = 0;
        long nextProgress = 0;
        while (await reader.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
        {
            if (line.Length != 0)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                data.Append(line.AsSpan(5).TrimStart()).Append('\n');
                if (data.Length > BoundedHttp.MaximumBytes) throw new InvalidDataException("The AI event was too large.");
                continue;
            }
            if (data.Length == 0) continue;
            string text = data.ToString(); data.Clear();
            if (text.Trim() == "[DONE]") break;
            using var item = JsonDocument.Parse(text);
            var root = item.RootElement;
            string? type = root.GetProperty("type").GetString();
            if (type == "response.output_text.delta")
            {
                characters += root.GetProperty("delta").GetString()?.Length ?? 0;
                if (characters > BoundedHttp.MaximumBytes) throw new InvalidDataException("The AI proposal was too large.");
                if (Environment.TickCount64 >= nextProgress)
                {
                    progress?.Report($"Receiving proposal… {characters:N0} characters");
                    nextProgress = Environment.TickCount64 + 150;
                }
            }
            if (type == "response.refusal.delta") throw new InvalidDataException("The model declined this request. Try rephrasing it.");
            if (type is "error" or "response.failed" or "response.incomplete")
                throw new AiConnectionException("ChatGPT could not complete this request. Check your connection, model and usage settings; nothing was applied.", true);
            if (type == "response.completed")
                return JsonDocument.Parse(root.GetProperty("response").GetRawText());
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
