using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class VsCodeProjectDiscoveryTests
{
    private static readonly WorkspaceProcessIdentity Process = new(123, 456, @"C:\Apps\Code.exe");
    private static readonly WorkspaceWindowIdentity Window = new((IntPtr)42, 123, 456);
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void DiagnosticsProvideTheDecodedFullFolderWithoutInferringAnythingFromTheCaption()
    {
        var data = Json("""
            {"mainPID":123,"windows":[{"title":"Custom caption","folderURIs":[
              {"external":"file:///g%3A/Work/Project%20%26%20%23one","scheme":"file","fsPath":"C:\\UntrustedFallback"}]}]}
            """);
        var window = Assert.Single(VsCodeDiagnostics.Parse(data, Process, p => p == @"g:\Work\Project & #one"));
        Assert.Equal(@"g:\Work\Project & #one", window.Target);
        Assert.Equal("Custom caption", window.Title);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"external\":\"vscode-remote://ssh-remote/host/work\"}")]
    [InlineData("{\"external\":\"file://server/share/project\"}")]
    [InlineData("{\"external\":\"https://example.com/project\"}")]
    [InlineData("{\"fsPath\":\"C:\\\\Work\\\\Project\"}")]
    public void UnsupportedFolderEvidenceDoesNotBecomeALaunchTarget(string folder)
    {
        var data = Json("{\"mainPID\":123,\"windows\":[{\"title\":\"Project\",\"folderURIs\":[" + folder + "]}]}");
        Assert.Empty(Assert.Single(VsCodeDiagnostics.Parse(data, Process, _ => true)).Target);
    }

    [Theory]
    [InlineData("{\"mainPID\":999,\"windows\":[]}")]
    [InlineData("{\"mainPID\":\"123\",\"windows\":[]}")]
    [InlineData("[]")]
    public void WrongProcessAndMalformedDiagnosticsAreRejected(string data)
        => Assert.Empty(VsCodeDiagnostics.Parse(Json(data), Process, _ => true));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MissingRemoteAndMultiRootWorkspacesAreKeptForManualSelection(bool remote, bool multiple)
    {
        var data = Json(JsonSerializer.Serialize(new { mainPID = 123, windows = new[] { new {
            title = "Project", remoteAuthority = remote ? "ssh-remote+host" : "",
            folderURIs = multiple ? new[] { new { external = "file:///c:/One" }, new { external = "file:///c:/Two" } }
                : new[] { new { external = "file:///c:/One" } }
        } } }));
        Assert.Empty(Assert.Single(VsCodeDiagnostics.Parse(data, Process, _ => remote || multiple)).Target);
    }

    [Fact]
    public async Task PollDoesNotBlockAndSameNamedProjectsUseTheWindowsOwnFullPath()
    {
        var ready = new TaskCompletionSource<IReadOnlyList<VsCodeProjectWindow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new VsCodeProjectDiscovery(() => ready.Task);
        Assert.Null(discovery.Read(Window, Process.ExecutablePath, "one.cs - Project - Visual Studio Code"));
        ready.SetResult(new[] { new VsCodeProjectWindow(Process, "one.cs - Project - Visual Studio Code", @"C:\One\Project"),
            new VsCodeProjectWindow(Process, "two.cs - Project - Visual Studio Code", @"C:\Two\Project") });
        await WaitUntil(() => discovery.Read(Window, Process.ExecutablePath, "one.cs - Project - Visual Studio Code") != null);
        Assert.Equal(@"C:\One\Project", discovery.Read(Window, Process.ExecutablePath, "one.cs - Project - Visual Studio Code"));
        Assert.Equal(@"C:\Two\Project", discovery.Read(Window, Process.ExecutablePath, "two.cs - Project - Visual Studio Code"));
        Assert.Null(discovery.Read(Window with { ProcessStartedAtUtcTicks = 789 }, Process.ExecutablePath, "one.cs - Project - Visual Studio Code"));
    }

    [Fact]
    public async Task IdenticalLiveCaptionsAreAmbiguousRatherThanFirstMatchWins()
    {
        var snapshot = new[] { new VsCodeProjectWindow(Process, "Project", @"C:\One\Project"),
            new VsCodeProjectWindow(Process, "Project", @"C:\Two\Project") };
        var discovery = new VsCodeProjectDiscovery(() => Task.FromResult<IReadOnlyList<VsCodeProjectWindow>>(snapshot));
        await WaitUntil(() => discovery.Read(Window, Process.ExecutablePath, "Project") != null);
        Assert.Equal("", discovery.Read(Window, Process.ExecutablePath, "Project"));
    }

    [Fact]
    public async Task FailureAndExpiredSnapshotsCannotAssignAnOldFolder()
    {
        long now = 0; int calls = 0;
        var second = new TaskCompletionSource<IReadOnlyList<VsCodeProjectWindow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new VsCodeProjectDiscovery(() => Interlocked.Increment(ref calls) == 1
            ? Task.FromResult<IReadOnlyList<VsCodeProjectWindow>>(new[] { new VsCodeProjectWindow(Process, "Project", @"C:\Old") })
            : second.Task, () => Interlocked.Read(ref now));
        await WaitUntil(() => discovery.Read(Window, Process.ExecutablePath, "Project") != null);
        Interlocked.Exchange(ref now, 9000);
        Assert.Null(discovery.Read(Window, Process.ExecutablePath, "Project"));
        await WaitUntil(() => Volatile.Read(ref calls) == 2);
        second.SetException(new IOException("Closed"));
        await Task.Delay(20);
        Assert.Null(discovery.Read(Window, Process.ExecutablePath, "Project"));
    }

    [Fact]
    public async Task DiagnosticProtocolHandlesFragmentedRepliesAndSendsOnlyTheReadOnlyRequest()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"mainPID\":123,\"windows\":[]}");
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(new byte[] { 4, 2, 6 }); writer.Write7BitEncodedInt(201);
            writer.Write(new byte[] { 6, 1, 5 }); writer.Write7BitEncodedInt(json.Length); writer.Write(json);
        }
        using var stream = new FragmentedDuplexStream(Frame(payload.ToArray()));
        var data = await VsCodeDiagnostics.QueryAsync(stream, CancellationToken.None);
        Assert.Equal(123, data.GetProperty("mainPID").GetInt32());
        string requests = Encoding.UTF8.GetString(stream.Written.ToArray());
        Assert.Contains("diagnostics", requests); Assert.Contains("getMainDiagnostics", requests);
        Assert.DoesNotContain("launch", requests);
    }

    [Fact]
    public async Task OversizedFramesAreRejectedBeforeAllocatingTheirPayload()
    {
        byte[] header = new byte[13]; header[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(9), uint.MaxValue);
        using var stream = new FragmentedDuplexStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => VsCodeDiagnostics.QueryAsync(stream, CancellationToken.None));
    }

    private static byte[] Frame(byte[] payload)
    {
        byte[] header = new byte[13]; header[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(9), (uint)payload.Length);
        return header.Concat(payload).ToArray();
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (int i = 0; i < 200; i++) { if (done()) return; await Task.Delay(5); }
        Assert.True(done(), "Background poll did not finish.");
    }

    private sealed class FragmentedDuplexStream(byte[] reply) : Stream
    {
        private readonly MemoryStream _reply = new(reply);
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _reply.Read(buffer, offset, Math.Min(count, 3));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
            => ValueTask.FromResult(_reply.Read(buffer.Span[..Math.Min(buffer.Length, 3)]));
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default)
        { Written.Write(buffer.Span); return ValueTask.CompletedTask; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _reply.Dispose(); Written.Dispose(); } base.Dispose(disposing); }
    }
}
