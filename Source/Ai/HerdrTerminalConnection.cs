using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CanvasDesktop;

/// <summary>One Herdr attach stream. Closing the client leaves the hosted terminal running.</summary>
internal sealed class HerdrTerminalConnection : IDisposable
{
    private const int HeaderLength = 9;
    private const int MaximumPayload = 16 * 1024 * 1024;
    private const int MaximumHello = 64 * 1024;
    private const byte Data = 0;
    private const byte Credit = 1;
    private const byte Close = 2;
    private const byte ResizeFrame = 3;
    private const byte FocusFrame = 4;
    private readonly Process _bridge;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeGate = new(1);
    private uint _channel;
    private int _disposed;

    public bool IsObserver { get; }
    public string Address { get; private set; } = "";
    public int Columns { get; private set; }
    public int Rows { get; private set; }

    private HerdrTerminalConnection(Process bridge, bool observe)
    {
        _bridge = bridge;
        _input = bridge.StandardInput.BaseStream;
        _output = new BufferedStream(bridge.StandardOutput.BaseStream);
        _ = DrainErrors(bridge.StandardError.BaseStream);
        IsObserver = observe;
    }

    public static async Task<HerdrTerminalConnection> Connect(string executable, Guid card, bool observe, int cols, int rows,
        CancellationToken cancellation)
    {
        ValidateGrid(cols, rows);
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        start.ArgumentList.Add("connect");
        var bridge = Process.Start(start) ?? throw new IOException("Could not connect to Herdr Core.");
        var connection = new HerdrTerminalConnection(bridge, observe);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await connection.Attach(card, cols, rows, timeout.Token).ConfigureAwait(false);
            return connection;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            connection.Dispose();
            throw new IOException("Herdr Core did not respond in time.");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task Attach(Guid card, int cols, int rows, CancellationToken cancellation)
    {
        var hello = Request(1, "hello", new { generation = 1, framed = true, features = new[] { "resize_notifications" } });
        await _input.WriteAsync(hello, cancellation).ConfigureAwait(false);
        await _input.WriteAsync("\n"u8.ToArray(), cancellation).ConfigureAwait(false);
        await _input.FlushAsync(cancellation).ConfigureAwait(false);
        using var response = JsonDocument.Parse(await ReadLine(cancellation).ConfigureAwait(false));
        if (!Result(response.RootElement, 1).GetProperty("framed").GetBoolean())
            throw new IOException("This version of Herdr Core does not support terminal streams.");

        var name = card.ToString("N");
        var attach = Request(2, "terminal.attach", new
        {
            terminal = name, cols, rows, observe = IsObserver, history = 300,
            resize_notifications = IsObserver
        });
        await WriteFrame(0, Data, attach, cancellation).ConfigureAwait(false);
        var frame = await ReadFrame(cancellation).ConfigureAwait(false);
        if (frame.Channel != 0 || frame.Kind != Data)
            throw new IOException("Herdr Core returned an invalid attach response.");
        using var reply = JsonDocument.Parse(frame.Payload);
        var attached = Result(reply.RootElement, 2);
        _channel = attached.GetProperty("channel").GetUInt32();
        if (_channel == 0)
            throw new IOException("Herdr Core returned an invalid terminal channel.");
        var terminal = attached.GetProperty("terminal");
        if (terminal.GetProperty("name").GetString() != name
            || !terminal.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Object
            || !labels.TryGetProperty("canvas_agent", out var owner) || owner.GetString() != name)
            throw new IOException("Herdr Core returned a terminal belonging to a different card.");
        if (IsObserver && (!attached.TryGetProperty("resize_notifications", out var resized) || resized.ValueKind != JsonValueKind.True))
            throw new IOException("Update Herdr Core to watch terminal size changes.");
        Address = terminal.GetProperty("address").GetString() ?? throw new IOException("Herdr Core returned no terminal address.");
        Columns = terminal.GetProperty("cols").GetUInt16();
        Rows = terminal.GetProperty("rows").GetUInt16();
        ValidateGrid(Columns, Rows);
    }

    public async Task Pump(Func<byte[], Task> output, Func<int, int, Task> resized, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        await resized(Columns, Rows).ConfigureAwait(false);
        while (true)
        {
            var frame = await ReadFrame(cancellation).ConfigureAwait(false);
            if (frame.Channel != _channel) continue;
            switch (frame.Kind)
            {
                case Data:
                    if (frame.Payload.Length == 0) break;
                    await output(frame.Payload).ConfigureAwait(false);
                    var credit = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(credit, (uint)frame.Payload.Length);
                    await WriteFrame(_channel, Credit, credit, cancellation).ConfigureAwait(false);
                    break;
                case ResizeFrame when frame.Payload.Length == 4:
                    Columns = BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload);
                    Rows = BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload.AsSpan(2));
                    ValidateGrid(Columns, Rows);
                    await resized(Columns, Rows).ConfigureAwait(false);
                    break;
                case ResizeFrame:
                    throw new IOException("Herdr Core sent an invalid terminal size.");
                case Close:
                    if (frame.Payload.Length == 0) return;
                    throw new IOException(CloseMessage(frame.Payload));
            }
        }
    }

    public Task Send(byte[] bytes, CancellationToken cancellation)
        => IsObserver ? Task.CompletedTask : WriteFrame(_channel, Data, bytes, cancellation);

    public Task Resize(int cols, int rows, CancellationToken cancellation)
    {
        if (IsObserver) return Task.CompletedTask;
        ValidateGrid(cols, rows);
        var size = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)cols);
        BinaryPrimitives.WriteUInt16LittleEndian(size.AsSpan(2), (ushort)rows);
        return WriteFrame(_channel, ResizeFrame, size, cancellation);
    }

    public Task Focus(bool focused, CancellationToken cancellation)
        => IsObserver ? Task.CompletedTask : WriteFrame(_channel, FocusFrame, [focused ? (byte)1 : (byte)0], cancellation);

    private static string CloseMessage(byte[] payload)
    {
        try
        {
            using var close = JsonDocument.Parse(payload);
            var reason = close.RootElement.TryGetProperty("reason", out var value) ? value.GetString() : null;
            return reason switch
            {
                "exited" => "The agent terminal ended.",
                "killed" => "The agent terminal was stopped.",
                "server_stop" => "Herdr Core stopped.",
                _ => "Herdr Core closed the terminal stream."
            };
        }
        catch (JsonException)
        {
            return "The agent terminal was stopped.";
        }
    }

    private static void ValidateGrid(int cols, int rows)
    {
        if (cols is < 1 or > ushort.MaxValue || rows is < 1 or > ushort.MaxValue)
            throw new IOException("Invalid terminal size.");
    }

    private static byte[] Request(int id, string method, object parameters)
        => JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters });

    private static JsonElement Result(JsonElement reply, int id)
    {
        if (!reply.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out int number) || number != id)
            throw new IOException("Herdr Core returned an invalid response ID.");
        if (reply.TryGetProperty("result", out var result)) return result;
        if (reply.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
            throw new IOException($"Herdr Core: {message.GetString()}");
        throw new IOException("Herdr Core returned an invalid response.");
    }

    private async Task<byte[]> ReadLine(CancellationToken cancellation)
    {
        using var line = new MemoryStream();
        var one = new byte[1];
        while (line.Length < MaximumHello)
        {
            await _output.ReadExactlyAsync(one, cancellation).ConfigureAwait(false);
            if (one[0] == (byte)'\n') return line.ToArray();
            line.WriteByte(one[0]);
        }
        throw new IOException("Herdr Core returned an oversized handshake.");
    }

    private async Task<(uint Channel, byte Kind, byte[] Payload)> ReadFrame(CancellationToken cancellation)
    {
        var header = new byte[HeaderLength];
        await _output.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(5));
        if (length > MaximumPayload)
            throw new IOException("Herdr Core returned an oversized terminal frame.");
        var payload = new byte[length];
        await _output.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
        return (BinaryPrimitives.ReadUInt32LittleEndian(header), header[4], payload);
    }

    private async Task WriteFrame(uint channel, byte kind, byte[] payload, CancellationToken cancellation)
    {
        if (payload.Length > MaximumPayload) throw new IOException("The terminal frame is too large.");
        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var header = new byte[HeaderLength];
            BinaryPrimitives.WriteUInt32LittleEndian(header, channel);
            header[4] = kind;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(5), (uint)payload.Length);
            try
            {
                await _input.WriteAsync(header, cancellation).ConfigureAwait(false);
                await _input.WriteAsync(payload, cancellation).ConfigureAwait(false);
                await _input.FlushAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A partial frame makes subsequent writes unsafe on this bridge.
                Dispose();
                throw;
            }
        }
        finally { _writeGate.Release(); }
    }

    private static async Task DrainErrors(Stream errors)
    {
        try { await errors.CopyToAsync(Stream.Null).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_bridge.HasExited) _bridge.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        _bridge.Dispose();
        _input.Dispose();
        _output.Dispose();
    }
}
