using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace CanvasDesktop;

internal sealed record VsCodeProjectWindow(WorkspaceProcessIdentity Process, string Title, string Target);

/// <summary>Read-only access to the local diagnostics channel used by Code's --status command.</summary>
internal static class VsCodeDiagnostics
{
    // VS Code's ipc.net.ts framing and ipc.ts value serialization. This is an
    // internal protocol: unsupported replies/timeouts simply disable detection.
    // https://github.com/microsoft/vscode/tree/main/src/vs/base/parts/ipc
    private const int MaximumFrame = 1024 * 1024;

    public static async Task<IReadOnlyList<VsCodeProjectWindow>> ReadAsync()
    {
        var results = new List<VsCodeProjectWindow>();
        try
        {
            var identity = new WorkspaceIdentity();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            foreach (string endpoint in Directory.EnumerateFiles(@"\\.\pipe\")
                .Select(Path.GetFileName).OfType<string>().Where(p => p.EndsWith("-main-sock", StringComparison.Ordinal)).Take(16))
            {
                if (timeout.IsCancellationRequested) break;
                try
                {
                    using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)
                        || identity.InspectProcess(pid) is not { } process
                        || !WorkspaceTargetIdentity.IsVsCode(process.ExecutablePath)) continue;
                    var diagnostics = await QueryAsync(pipe, timeout.Token).ConfigureAwait(false);
                    if (identity.InspectProcess(pid) != process) continue;
                    results.AddRange(Parse(diagnostics, process, Directory.Exists));
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException
                    or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { /* Code can exit or update between discovery and connection. */ }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return results;
    }

    internal static IReadOnlyList<VsCodeProjectWindow> Parse(JsonElement data, WorkspaceProcessIdentity process,
        Func<string, bool> directoryExists)
    {
        var result = new List<VsCodeProjectWindow>();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("mainPID", out var pid)
            || pid.ValueKind != JsonValueKind.Number || !pid.TryGetUInt32(out uint reportedPid)
            || reportedPid != process.ProcessId || !data.TryGetProperty("windows", out var windows)
            || windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() > 1000) return result;
        foreach (var window in windows.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("title", out var title)
                || title.ValueKind != JsonValueKind.String) continue;
            string target = "";
            bool remote = window.TryGetProperty("remoteAuthority", out var authority)
                && authority.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(authority.GetString());
            if (!remote && window.TryGetProperty("folderURIs", out var folders)
                && folders.ValueKind == JsonValueKind.Array && folders.GetArrayLength() == 1
                && folders[0].ValueKind == JsonValueKind.Object
                && folders[0].TryGetProperty("external", out var external) && external.ValueKind == JsonValueKind.String
                && Uri.TryCreate(external.GetString(), UriKind.Absolute, out var uri) && uri.IsFile && !uri.IsUnc)
            {
                // VS Code URI.toJSON escapes the drive colon (file:///c%3A/...);
                // System.Uri then leaves a leading slash in LocalPath.
                string local = uri.LocalPath;
                if (local.Length >= 4 && local[0] == '/' && char.IsAsciiLetter(local[1])
                    && local[2] == ':' && local[3] == '/') local = local[1..].Replace('/', '\\');
                string path = Path.TrimEndingDirectorySeparator(local);
                if (Path.IsPathFullyQualified(path) && directoryExists(path)) target = path;
            }
            // Keep empty/unsupported windows too: identical captions must remain
            // ambiguous even if only one of them has a usable local folder.
            result.Add(new(process, title.GetString()!, target));
        }
        return result;
    }

    internal static async Task<JsonElement> QueryAsync(Stream stream, CancellationToken cancellation)
    {
        await SendAsync(stream, Encode("CanvasWindowComposer"), cancellation).ConfigureAwait(false);
        await SendAsync(stream, Encode(new object[] { 200 }).Concat(new byte[] { 0 }).ToArray(), cancellation).ConfigureAwait(false);
        await SendAsync(stream, Encode(new object[] { 100, 1, "diagnostics", "getMainDiagnostics" })
            .Concat(Encode(Array.Empty<object>())).ToArray(), cancellation).ConfigureAwait(false);
        for (int message = 0; message < 16; message++)
        {
            byte[] frame = new byte[13];
            await stream.ReadExactlyAsync(frame, cancellation).ConfigureAwait(false);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(9));
            if (size > MaximumFrame) throw new InvalidDataException("Oversized Code diagnostics frame.");
            byte[] payload = new byte[size];
            await stream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
            if (frame[0] != 1) continue;
            using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            if (reader.ReadByte() != 4) throw new InvalidDataException("Invalid Code reply header.");
            int count = ReadLength(reader);
            if (count is < 1 or > 2 || reader.ReadByte() != 6) throw new InvalidDataException("Invalid Code reply type.");
            int type = ReadLength(reader);
            if (type == 200) continue; // Channel initialization.
            if (count != 2 || reader.ReadByte() != 6 || ReadLength(reader) != 1 || type != 201)
                throw new InvalidDataException("Code diagnostics request was rejected.");
            if (reader.ReadByte() != 5) throw new InvalidDataException("Invalid Code diagnostics payload.");
            int length = ReadLength(reader);
            byte[] json = reader.ReadBytes(length);
            if (json.Length != length) throw new EndOfStreamException();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Code diagnostics object.");
            return document.RootElement.Clone();
        }
        throw new InvalidDataException("No Code diagnostics reply.");
    }

    private static int ReadLength(BinaryReader reader)
    {
        uint result = 0;
        for (int shift = 0; shift < 28; shift += 7)
        {
            byte value = reader.ReadByte();
            result |= (uint)(value & 127) << shift;
            if ((value & 128) == 0)
            {
                if (result > MaximumFrame) throw new InvalidDataException("Oversized Code diagnostics value.");
                return (int)result;
            }
        }
        throw new InvalidDataException("Invalid Code diagnostics length.");
    }

    private static byte[] Encode(object value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Write(value);
        return stream.ToArray();
        void Write(object data)
        {
            switch (data)
            {
                case int number: writer.Write((byte)6); writer.Write7BitEncodedInt(number); break;
                case string text:
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    writer.Write((byte)1); writer.Write7BitEncodedInt(bytes.Length); writer.Write(bytes); break;
                case object[] array:
                    writer.Write((byte)4); writer.Write7BitEncodedInt(array.Length);
                    foreach (var item in array) Write(item);
                    break;
            }
        }
    }

    private static async Task SendAsync(Stream stream, byte[] payload, CancellationToken cancellation)
    {
        byte[] header = new byte[13]; header[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(9), (uint)payload.Length);
        await stream.WriteAsync(header, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellation).ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
