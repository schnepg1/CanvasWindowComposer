using System.Text;
using CanvasDesktop.Dictation;

namespace CanvasDesktop.Dictation.Tests;

public class SseEventParserTests
{
    [Fact]
    public async Task ParsesStandardDataEventsWithLfAndCrLfAndOptionalLeadingSpace()
    {
        string raw =
            ": heartbeat comment\n\n" +
            "data: {\"type\":\"partial\",\"full_text\":\"hello\"}\n\n" +
            "data:{\"type\":\"final\",\"full_text\":\"hello world\"}\r\n\r\n" +
            "event: custom\r\n" +
            "data:  two leading spaces keeps one\r\n\r\n";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var events = new List<string>();
        await foreach (var item in SseEventParser.ReadDataEventsAsync(stream))
            events.Add(item);

        Assert.Equal(3, events.Count);
        Assert.Equal("{\"type\":\"partial\",\"full_text\":\"hello\"}", events[0]);
        Assert.Equal("{\"type\":\"final\",\"full_text\":\"hello world\"}", events[1]);
        Assert.Equal(" two leading spaces keeps one", events[2]);
    }

    [Fact]
    public async Task JoinsMultiLineDataFieldsWithNewline()
    {
        string raw = "data: line1\ndata: line2\n\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));

        var events = new List<string>();
        await foreach (var item in SseEventParser.ReadDataEventsAsync(stream))
            events.Add(item);

        Assert.Single(events);
        Assert.Equal("line1\nline2", events[0]);
    }

    [Fact]
    public async Task IgnoresCommentsAndUnterminatedTrailingBlocks()
    {
        string raw = ": comment only\n\n: another\ndata: {\"ok\":true}\n\ndata: truncated-without-blank-line";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));

        var events = new List<string>();
        await foreach (var item in SseEventParser.ReadDataEventsAsync(stream))
            events.Add(item);

        Assert.Single(events);
        Assert.Equal("{\"ok\":true}", events[0]);
    }

    [Fact]
    public async Task SupportsIncrementalChunkedDeliveryAndCancellation()
    {
        using var pipe = new BlockingChunkStream();
        using var cts = new CancellationTokenSource();

        var received = new List<string>();
        var readerTask = Task.Run(async () =>
        {
            await foreach (var ev in SseEventParser.ReadDataEventsAsync(pipe, cts.Token))
                received.Add(ev);
        });

        pipe.Push("data: first\n\n");
        await WaitUntilAsync(() => received.Count == 1);
        Assert.Equal("first", received[0]);

        pipe.Push("data: sec");
        pipe.Push("ond\n\n");
        await WaitUntilAsync(() => received.Count == 2);
        Assert.Equal("second", received[1]);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readerTask);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not satisfied in time.");
            await Task.Delay(10);
        }
    }

    private sealed class BlockingChunkStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _chunks =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        public void Push(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current == null || _offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                    return 0;
                if (_chunks.Reader.TryRead(out var next))
                {
                    _current = next;
                    _offset = 0;
                }
            }

            int count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
