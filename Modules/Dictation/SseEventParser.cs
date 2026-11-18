using System.Runtime.CompilerServices;
using System.Text;

namespace CanvasDesktop.Dictation;

/// <summary>Incremental WHATWG Server-Sent Events (text/event-stream) parser yielding raw event data payloads.</summary>
public static class SseEventParser
{
    public static async IAsyncEnumerable<string> ReadDataEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        var dataBuilder = new StringBuilder();
        bool hasData = false;

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                yield break;

            if (line.Length == 0)
            {
                if (hasData)
                {
                    string payload = dataBuilder.ToString();
                    dataBuilder.Clear();
                    hasData = false;
                    yield return payload;
                }
                continue;
            }

            if (line[0] == ':')
                continue;

            int colonIndex = line.IndexOf(':');
            string field;
            string value;
            if (colonIndex < 0)
            {
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line[..colonIndex];
                value = line[(colonIndex + 1)..];
                // Per WHATWG SSE spec, strip at most one leading U+0020 space after the colon.
                if (value.Length > 0 && value[0] == ' ')
                    value = value[1..];
            }

            if (string.Equals(field, "data", StringComparison.Ordinal))
            {
                if (hasData)
                    dataBuilder.Append('\n');
                dataBuilder.Append(value);
                hasData = true;
            }
        }
    }
}
