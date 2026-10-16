using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace CanvasDesktop;

/// <summary>Atomic DPAPI CurrentUser-encrypted credentials, outside workspace persistence.</summary>
internal sealed class WindowsChatGptStore(string path) : IChatGptStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public ChatGptState? Load()
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("The credential file is too large.");
        byte[] clear = Transform(File.ReadAllBytes(path), protect: false);
        try { return JsonSerializer.Deserialize<ChatGptState>(clear, Json) ?? throw new InvalidDataException("The credential file is invalid."); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void Save(ChatGptState state)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        byte[] encrypted;
        try
        {
            if (clear.Length > 3 * 1024 * 1024) throw new InvalidDataException("The credential file is too large.");
            encrypted = Transform(clear, protect: true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(encrypted); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var source = new Blob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);
            bool success = protect
                ? CryptProtectData(ref source, "Canvas Window Composer ChatGPT", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows could not protect or unlock the ChatGPT credentials.");
            var bytes = new byte[output.Size]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return bytes;
        }
        finally
        {
            for (int i = 0; i < source.Size; i++) Marshal.WriteByte(source.Data, i, 0);
            Marshal.FreeHGlobal(source.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
}
